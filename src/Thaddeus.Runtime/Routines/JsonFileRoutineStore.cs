using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Routines;

/// <summary>
/// File-backed routine store. Two sibling directories under the runtime's
/// lock-file root: <c>routines/</c> holds one JSON per routine definition,
/// <c>routines/runs/</c> holds one JSON per run. Mirrors the pattern used by
/// the memo and settings stores: eager load on first use, per-id semaphore
/// for serial mutations.
/// </summary>
public sealed class JsonFileRoutineStore : IRoutineStore, IDisposable
{
    private readonly string _routinesDir;
    private readonly string _runsDir;
    private readonly ILogger<JsonFileRoutineStore> _logger;
    private readonly ConcurrentDictionary<string, Routine> _routines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RoutineRun> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public JsonFileRoutineStore(string rootDirectory, ILogger<JsonFileRoutineStore> logger)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        _routinesDir = rootDirectory;
        _runsDir = Path.Combine(rootDirectory, "runs");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string RoutinesDirectory => _routinesDir;
    public string RunsDirectory => _runsDir;

    public async Task<IReadOnlyList<Routine>> ListRoutinesAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _routines.Values
            .OrderByDescending(r => r.UpdatedAt)
            .ToArray();
    }

    public async Task<Routine?> GetRoutineAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _routines.TryGetValue(id, out var item) ? item : null;
    }

    public async Task<Routine> CreateRoutineAsync(
        string name,
        string description,
        IReadOnlyList<RoutineChecklistItem> checklistItems,
        string? promptTemplate,
        bool enabled,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var routine = new Routine(
            Id: NewRoutineId(now),
            Name: NormalizeName(name),
            Description: description ?? string.Empty,
            ChecklistItems: NormalizeItems(checklistItems),
            PromptTemplate: NormalizePromptTemplate(promptTemplate),
            Enabled: enabled,
            CreatedAt: now,
            UpdatedAt: now,
            LastRunAt: null);

        var gate = LockFor(routine.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _routines[routine.Id] = routine;
            await WriteRoutineAsync(routine, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }

        _logger.LogInformation("routine.create id={Id}", routine.Id);
        return routine;
    }

    public async Task<Routine> SeedRoutineAsync(Routine seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var gate = LockFor(seed.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_routines.TryGetValue(seed.Id, out var existing))
            {
                // Caller-supplied edits already won — seed must not clobber
                // user intent. The whole point of the seeder is to fill in
                // missing defaults, not to reset them each startup.
                return existing;
            }

            _routines[seed.Id] = seed;
            await WriteRoutineAsync(seed, ct).ConfigureAwait(false);
            _logger.LogInformation("routine.seed id={Id}", seed.Id);
            return seed;
        }
        finally { gate.Release(); }
    }

    public async Task<Routine?> UpdateRoutineAsync(
        string id,
        string? name,
        string? description,
        IReadOnlyList<RoutineChecklistItem>? checklistItems,
        string? promptTemplate,
        bool? enabled,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_routines.TryGetValue(id, out var current)) return null;
            var updated = current with
            {
                Name = name is not null ? NormalizeName(name) : current.Name,
                Description = description ?? current.Description,
                ChecklistItems = checklistItems is not null
                    ? NormalizeItems(checklistItems)
                    : current.ChecklistItems,
                PromptTemplate = promptTemplate is not null
                    ? NormalizePromptTemplate(promptTemplate)
                    : current.PromptTemplate,
                Enabled = enabled ?? current.Enabled,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _routines[id] = updated;
            await WriteRoutineAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteRoutineAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_routines.TryRemove(id, out _)) return false;
            var path = RoutinePathFor(id);
            TryDeleteFile(path);

            // Cascade: drop every run that referenced this routine.
            var orphans = _runs.Values.Where(r => r.RoutineId == id).Select(r => r.Id).ToArray();
            foreach (var runId in orphans)
            {
                if (_runs.TryRemove(runId, out _))
                    TryDeleteFile(RunPathFor(runId));
            }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<RoutineRun>> ListRunsAsync(string routineId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _runs.Values
            .Where(r => r.RoutineId == routineId)
            .OrderByDescending(r => r.StartedAt)
            .ToArray();
    }

    public async Task<RoutineRun?> GetRunAsync(string runId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _runs.TryGetValue(runId, out var r) ? r : null;
    }

    public async Task<RoutineRun?> StartRunAsync(string routineId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        if (!_routines.TryGetValue(routineId, out var routine)) return null;

        var now = DateTimeOffset.UtcNow;
        var run = new RoutineRun(
            Id: NewRunId(now),
            RoutineId: routineId,
            StartedAt: now,
            CompletedAt: null,
            Items: routine.ChecklistItems
                .Select(item => new RoutineRunItem(
                    ChecklistItemId: item.Id,
                    Text: item.Text,
                    IsCompleted: false,
                    CompletedAt: null))
                .ToArray(),
            UserNote: null,
            GeneratedSummary: null);

        var gate = LockFor(run.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _runs[run.Id] = run;
            await WriteRunAsync(run, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }

        // Bump the routine's LastRunAt so the list card shows "last run just now".
        var routineGate = LockFor(routineId);
        await routineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_routines.TryGetValue(routineId, out var latest))
            {
                var bumped = latest with { LastRunAt = now, UpdatedAt = now };
                _routines[routineId] = bumped;
                await WriteRoutineAsync(bumped, ct).ConfigureAwait(false);
            }
        }
        finally { routineGate.Release(); }

        _logger.LogInformation("routine.run.started runId={RunId} routineId={RoutineId}", run.Id, routineId);
        return run;
    }

    public async Task<RoutineRun?> UpdateRunAsync(
        string runId,
        IReadOnlyDictionary<string, bool>? itemUpdates,
        string? userNote,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(runId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_runs.TryGetValue(runId, out var current)) return null;
            if (current.CompletedAt is not null)
            {
                // Sealed runs are immutable — the UI should send updates to a
                // fresh run. Return the existing record so the client sees the
                // terminal state and stops patching.
                return current;
            }

            var items = current.Items;
            if (itemUpdates is { Count: > 0 })
            {
                var now = DateTimeOffset.UtcNow;
                items = current.Items
                    .Select(item =>
                    {
                        if (!itemUpdates.TryGetValue(item.ChecklistItemId, out var nextCompleted))
                            return item;
                        if (item.IsCompleted == nextCompleted) return item;
                        return item with
                        {
                            IsCompleted = nextCompleted,
                            CompletedAt = nextCompleted ? now : null,
                        };
                    })
                    .ToArray();
            }

            var updated = current with
            {
                Items = items,
                UserNote = userNote ?? current.UserNote,
            };
            _runs[runId] = updated;
            await WriteRunAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task<RoutineRun?> CompleteRunAsync(string runId, string? userNote, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(runId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_runs.TryGetValue(runId, out var current)) return null;
            if (current.CompletedAt is not null) return current;

            var updated = current with
            {
                CompletedAt = DateTimeOffset.UtcNow,
                UserNote = userNote ?? current.UserNote,
            };
            _runs[runId] = updated;
            await WriteRunAsync(updated, ct).ConfigureAwait(false);
            _logger.LogInformation("routine.run.completed runId={RunId} routineId={RoutineId}", runId, updated.RoutineId);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DiscardRunAsync(string runId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(runId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_runs.TryRemove(runId, out _)) return false;
            TryDeleteFile(RunPathFor(runId));
            return true;
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sem in _locks.Values) sem.Dispose();
        _initLock.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(_routinesDir);
            Directory.CreateDirectory(_runsDir);

            foreach (var file in Directory.EnumerateFiles(_routinesDir, "*.json"))
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    var item = await JsonSerializer.DeserializeAsync<Routine>(stream, s_jsonOptions, ct).ConfigureAwait(false);
                    if (item is not null) _routines[item.Id] = item;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "routine.load_failed path={Path}", file);
                }
            }

            foreach (var file in Directory.EnumerateFiles(_runsDir, "*.json"))
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    var run = await JsonSerializer.DeserializeAsync<RoutineRun>(stream, s_jsonOptions, ct).ConfigureAwait(false);
                    if (run is not null) _runs[run.Id] = run;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "routine.run.load_failed path={Path}", file);
                }
            }

            _initialized = true;
            _logger.LogInformation(
                "routine.store.initialized routines={Routines} runs={Runs} root={Root}",
                _routines.Count, _runs.Count, _routinesDir);
        }
        finally { _initLock.Release(); }
    }

    private SemaphoreSlim LockFor(string id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    private string RoutinePathFor(string id) => Path.Combine(_routinesDir, id + ".json");
    private string RunPathFor(string id) => Path.Combine(_runsDir, id + ".json");

    private Task WriteRoutineAsync(Routine routine, CancellationToken ct) =>
        WriteJsonAsync(RoutinePathFor(routine.Id), routine, ct);

    private Task WriteRunAsync(RoutineRun run, CancellationToken ct) =>
        WriteJsonAsync(RunPathFor(run.Id), run, ct);

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, value, s_jsonOptions, ct).ConfigureAwait(false);
        }
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }

    private void TryDeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "routine.delete_file_failed path={Path}", path); }
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "Untitled";
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    private static string? NormalizePromptTemplate(string? template)
    {
        if (template is null) return null;
        var trimmed = template.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 4000 ? trimmed[..4000] : trimmed;
    }

    private static IReadOnlyList<RoutineChecklistItem> NormalizeItems(IReadOnlyList<RoutineChecklistItem>? items)
    {
        if (items is null || items.Count == 0) return Array.Empty<RoutineChecklistItem>();

        var result = new List<RoutineChecklistItem>(items.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var sortOrder = 0;
        foreach (var raw in items)
        {
            if (raw is null) continue;
            var text = (raw.Text ?? string.Empty).Trim();
            if (text.Length == 0) continue;
            if (text.Length > 500) text = text[..500];

            // Preserve the caller's id when provided (so historical runs stay
            // linked across edits). Mint a new id for items that arrive without
            // one, and dedupe collisions defensively.
            var id = string.IsNullOrWhiteSpace(raw.Id) ? NewChecklistItemId() : raw.Id.Trim();
            while (!seenIds.Add(id)) id = NewChecklistItemId();

            result.Add(new RoutineChecklistItem(id, text, sortOrder++));
            if (result.Count >= 64) break;
        }
        return result;
    }

    private static string NewRoutineId(DateTimeOffset when)
    {
        Span<byte> rand = stackalloc byte[6];
        RandomNumberGenerator.Fill(rand);
        return string.Create(CultureInfo.InvariantCulture, $"rt_{when.ToUnixTimeMilliseconds():x}_{Convert.ToHexString(rand)}");
    }

    private static string NewRunId(DateTimeOffset when)
    {
        Span<byte> rand = stackalloc byte[6];
        RandomNumberGenerator.Fill(rand);
        return string.Create(CultureInfo.InvariantCulture, $"rr_{when.ToUnixTimeMilliseconds():x}_{Convert.ToHexString(rand)}");
    }

    private static string NewChecklistItemId()
    {
        Span<byte> rand = stackalloc byte[5];
        RandomNumberGenerator.Fill(rand);
        return "ci_" + Convert.ToHexString(rand).ToLowerInvariant();
    }
}
