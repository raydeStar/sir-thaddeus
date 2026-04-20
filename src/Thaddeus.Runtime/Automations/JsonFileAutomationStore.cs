using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Automations;

/// <summary>
/// File-backed automation store. Mirrors <see cref="Memory.JsonFileMemoStore"/>:
/// one JSON file per automation, per-id semaphore for serial mutations,
/// eager load on first call.
/// </summary>
public sealed class JsonFileAutomationStore : IAutomationStore, IDisposable
{
    private readonly string _rootDirectory;
    private readonly ILogger<JsonFileAutomationStore> _logger;
    private readonly ConcurrentDictionary<string, Automation> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public JsonFileAutomationStore(string rootDirectory, ILogger<JsonFileAutomationStore> logger)
    {
        _rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string RootDirectory => _rootDirectory;

    public async Task<IReadOnlyList<Automation>> ListAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _items.Values
            .OrderByDescending(a => a.UpdatedAt)
            .ToArray();
    }

    public async Task<Automation?> GetAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _items.TryGetValue(id, out var item) ? item : null;
    }

    public async Task<Automation> CreateAsync(
        string name,
        string description,
        IReadOnlyList<string> steps,
        bool enabled,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var item = new Automation(
            Id: NewId(now),
            Name: NormalizeName(name),
            Description: description ?? string.Empty,
            Trigger: "manual",
            Steps: NormalizeSteps(steps),
            Enabled: enabled,
            CreatedAt: now,
            UpdatedAt: now,
            LastRunAt: null);

        var gate = LockFor(item.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _items[item.Id] = item;
            await WriteAsync(item, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }

        _logger.LogInformation("automation.create id={Id}", item.Id);
        return item;
    }

    public async Task<Automation?> UpdateAsync(
        string id,
        string? name,
        string? description,
        IReadOnlyList<string>? steps,
        bool? enabled,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_items.TryGetValue(id, out var current)) return null;
            var updated = current with
            {
                Name = name is not null ? NormalizeName(name) : current.Name,
                Description = description ?? current.Description,
                Steps = steps is not null ? NormalizeSteps(steps) : current.Steps,
                Enabled = enabled ?? current.Enabled,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _items[id] = updated;
            await WriteAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task<Automation?> RecordRunAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_items.TryGetValue(id, out var current)) return null;
            var updated = current with { LastRunAt = DateTimeOffset.UtcNow };
            _items[id] = updated;
            await WriteAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_items.TryRemove(id, out _)) return false;
            var path = PathFor(id);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { _logger.LogWarning(ex, "automation.delete_file_failed path={Path}", path); }
            }
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
            Directory.CreateDirectory(_rootDirectory);
            foreach (var file in Directory.EnumerateFiles(_rootDirectory, "*.json"))
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    var item = await JsonSerializer
                        .DeserializeAsync<Automation>(stream, s_jsonOptions, ct)
                        .ConfigureAwait(false);
                    if (item is not null) _items[item.Id] = item;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "automation.load_failed path={Path}", file);
                }
            }
            _initialized = true;
            _logger.LogInformation("automation.store.initialized count={Count} root={Root}", _items.Count, _rootDirectory);
        }
        finally { _initLock.Release(); }
    }

    private SemaphoreSlim LockFor(string id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    private string PathFor(string id) => Path.Combine(_rootDirectory, id + ".json");

    private async Task WriteAsync(Automation item, CancellationToken ct)
    {
        var path = PathFor(item.Id);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, item, s_jsonOptions, ct).ConfigureAwait(false);
        }
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "Untitled";
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    private static IReadOnlyList<string> NormalizeSteps(IReadOnlyList<string>? steps)
    {
        if (steps is null) return Array.Empty<string>();
        return steps
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Take(64)
            .ToArray();
    }

    private static string NewId(DateTimeOffset when)
    {
        Span<byte> rand = stackalloc byte[6];
        RandomNumberGenerator.Fill(rand);
        return string.Create(CultureInfo.InvariantCulture, $"auto_{when.ToUnixTimeMilliseconds():x}_{Convert.ToHexString(rand)}");
    }
}
