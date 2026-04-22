using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Memory;

/// <summary>
/// File-backed memo store. One JSON file per memo at
/// <c>{rootDirectory}/{memoId}.json</c>. Mutations are serialized per-memo
/// via a <see cref="SemaphoreSlim"/> so concurrent edits do not interleave.
/// On first access the store eagerly loads all memo files into memory.
/// </summary>
public sealed class JsonFileMemoStore : IMemoStore, IDisposable
{
    private readonly string _rootDirectory;
    private readonly ILogger<JsonFileMemoStore> _logger;
    private readonly ConcurrentDictionary<string, Memo> _memos = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public JsonFileMemoStore(string rootDirectory, ILogger<JsonFileMemoStore> logger)
    {
        _rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Test seam — directory the memos are persisted to.</summary>
    public string RootDirectory => _rootDirectory;

    public async Task<IReadOnlyList<Memo>> ListAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _memos.Values
            .OrderByDescending(m => m.Pinned)
            .ThenByDescending(m => m.UpdatedAt)
            .ToArray();
    }

    public async Task<Memo?> GetAsync(string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _memos.TryGetValue(id, out var memo) ? memo : null;
    }

    public async Task<Memo> CreateAsync(
        string title,
        string body,
        IReadOnlyList<string>? tags,
        bool pinned,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var memo = new Memo(
            Id: NewId(now),
            Title: NormalizeTitle(title),
            Body: body ?? string.Empty,
            Tags: NormalizeTags(tags),
            Pinned: pinned,
            CreatedAt: now,
            UpdatedAt: now);

        var gate = LockFor(memo.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _memos[memo.Id] = memo;
            await WriteAsync(memo, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }

        _logger.LogInformation("memory.memo.create id={Id}", memo.Id);
        return memo;
    }

    public async Task<Memo?> UpdateAsync(
        string id,
        string? title,
        string? body,
        IReadOnlyList<string>? tags,
        bool? pinned,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_memos.TryGetValue(id, out var current)) return null;
            var updated = current with
            {
                Title = title is not null ? NormalizeTitle(title) : current.Title,
                Body = body ?? current.Body,
                Tags = tags is not null ? NormalizeTags(tags) : current.Tags,
                Pinned = pinned ?? current.Pinned,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _memos[id] = updated;
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
            if (!_memos.TryRemove(id, out _)) return false;
            var path = PathFor(id);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { _logger.LogWarning(ex, "memory.memo.delete_file_failed path={Path}", path); }
            }
            _logger.LogInformation("memory.memo.delete id={Id}", id);
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
                    var memo = await JsonSerializer
                        .DeserializeAsync<Memo>(stream, s_jsonOptions, ct)
                        .ConfigureAwait(false);
                    if (memo is not null) _memos[memo.Id] = memo;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "memory.memo.load_failed path={Path}", file);
                }
            }
            _initialized = true;
            _logger.LogInformation("memory.store.initialized count={Count} root={Root}", _memos.Count, _rootDirectory);
        }
        finally { _initLock.Release(); }
    }

    private SemaphoreSlim LockFor(string id) =>
        _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    private string PathFor(string id) => Path.Combine(_rootDirectory, id + ".json");

    private async Task WriteAsync(Memo memo, CancellationToken ct)
    {
        var path = PathFor(memo.Id);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, memo, s_jsonOptions, ct).ConfigureAwait(false);
        }
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }

    private static string NormalizeTitle(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "Untitled";
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0) return Array.Empty<string>();
        return tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();
    }

    private static string NewId(DateTimeOffset when)
    {
        Span<byte> rand = stackalloc byte[6];
        RandomNumberGenerator.Fill(rand);
        return string.Create(CultureInfo.InvariantCulture, $"mem_{when.ToUnixTimeMilliseconds():x}_{Convert.ToHexString(rand)}");
    }
}
