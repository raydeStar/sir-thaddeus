using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// File-backed thread store. Each thread is one JSON file at
/// <c>{rootDirectory}/{threadId}.json</c>. Mutations are serialized per-thread
/// via a <see cref="SemaphoreSlim"/> so concurrent appends do not interleave.
/// On startup the store loads all thread files into an in-memory index.
/// </summary>
public sealed class JsonFileThreadStore : IThreadStore, IDisposable
{
    private readonly string _rootDirectory;
    private readonly ILogger<JsonFileThreadStore> _logger;
    private readonly ConcurrentDictionary<string, ChatThread> _threads = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public JsonFileThreadStore(string rootDirectory, ILogger<JsonFileThreadStore> logger)
    {
        _rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Test seam — returns the directory threads are persisted to.</summary>
    public string RootDirectory => _rootDirectory;

    public async Task<IReadOnlyList<ChatThread>> ListAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _threads.Values
            .OrderByDescending(t => t.UpdatedAt)
            .ToArray();
    }

    public async Task<ChatThread?> GetAsync(string threadId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _threads.TryGetValue(threadId, out var thread) ? thread : null;
    }

    public async Task<ChatThread> CreateAsync(string title, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var id = NewId(now);
        var thread = new ChatThread(
            Id: id,
            Title: string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim(),
            CreatedAt: now,
            UpdatedAt: now,
            Messages: Array.Empty<ChatMessage>());

        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _threads[id] = thread;
            await WriteAsync(thread, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }

        _logger.LogInformation("chat.thread.create id={ThreadId}", id);
        return thread;
    }

    public async Task<ChatThread> AppendMessageAsync(string threadId, ChatMessage message, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(threadId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_threads.TryGetValue(threadId, out var current))
                throw new KeyNotFoundException($"Thread '{threadId}' not found.");

            var messages = current.Messages.ToList();
            messages.Add(message);
            var updated = current with
            {
                Messages = messages,
                UpdatedAt = message.CreatedAt > current.UpdatedAt ? message.CreatedAt : DateTimeOffset.UtcNow,
            };
            _threads[threadId] = updated;
            await WriteAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string threadId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(threadId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_threads.TryRemove(threadId, out _)) return false;
            var path = PathFor(threadId);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { _logger.LogWarning(ex, "chat.thread.delete_file_failed path={Path}", path); }
            }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<ChatThread?> RenameAsync(string threadId, string newTitle, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var trimmed = (newTitle ?? string.Empty).Trim();
        if (trimmed.Length == 0) trimmed = "Untitled";
        if (trimmed.Length > 200) trimmed = trimmed[..200];

        var gate = LockFor(threadId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_threads.TryGetValue(threadId, out var current)) return null;
            if (string.Equals(current.Title, trimmed, StringComparison.Ordinal)) return current;
            var updated = current with { Title = trimmed };
            _threads[threadId] = updated;
            await WriteAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task<ChatThread?> SetPinnedAsync(string threadId, bool pinned, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var gate = LockFor(threadId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_threads.TryGetValue(threadId, out var current)) return null;
            if (current.Pinned == pinned) return current;
            var updated = current with { Pinned = pinned };
            _threads[threadId] = updated;
            await WriteAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally { gate.Release(); }
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
                    var thread = await JsonSerializer
                        .DeserializeAsync<ChatThread>(stream, s_jsonOptions, ct)
                        .ConfigureAwait(false);
                    if (thread is not null) _threads[thread.Id] = thread;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "chat.thread.load_failed path={Path}", file);
                }
            }
            _initialized = true;
            _logger.LogInformation("chat.store.initialized count={Count} root={Root}", _threads.Count, _rootDirectory);
        }
        finally { _initLock.Release(); }
    }

    private SemaphoreSlim LockFor(string threadId) =>
        _locks.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));

    private string PathFor(string threadId) =>
        Path.Combine(_rootDirectory, threadId + ".json");

    private async Task WriteAsync(ChatThread thread, CancellationToken ct)
    {
        var path = PathFor(thread.Id);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, thread, s_jsonOptions, ct).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static string NewId(DateTimeOffset now)
    {
        // Time-prefixed id: keeps file listings naturally sorted by creation time.
        // Not cryptographic; collision risk is negligible at single-user scale.
        var ticks = now.UtcTicks.ToString("x");
        var rand = Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 6)).ToLowerInvariant();
        return $"th_{ticks}_{rand}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
        foreach (var sem in _locks.Values) sem.Dispose();
    }
}
