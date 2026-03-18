namespace SirThaddeus.Core.Caching;

public sealed class InMemoryResultCache : IResultCache
{
    private sealed record CacheEntry(object Value, DateTimeOffset ExpiresAtUtc);

    private readonly int _maxEntries;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryResultCache(int maxEntries = 500, TimeProvider? timeProvider = null)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<T?> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_lock)
        {
            PruneExpiredEntries();

            if (!_entries.TryGetValue(key, out var entry))
                return Task.FromResult(default(T));

            if (entry.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                RemoveEntry(key);
                return Task.FromResult(default(T));
            }

            TouchEntry(key);

            if (entry.Value is T typed)
                return Task.FromResult<T?>(typed);

            return Task.FromResult(default(T));
        }
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "ttl must be greater than zero.");

        var expiresAt = _timeProvider.GetUtcNow().Add(ttl);

        lock (_lock)
        {
            PruneExpiredEntries();

            _entries[key] = new CacheEntry(value!, expiresAt);
            TouchEntry(key);

            while (_entries.Count > _maxEntries && _lru.Last is not null)
            {
                var evictKey = _lru.Last.Value;
                RemoveEntry(evictKey);
            }
        }

        return Task.CompletedTask;
    }

    private void TouchEntry(string key)
    {
        if (_lruNodes.TryGetValue(key, out var existingNode))
        {
            _lru.Remove(existingNode);
        }

        var node = _lru.AddFirst(key);
        _lruNodes[key] = node;
    }

    private void PruneExpiredEntries()
    {
        var now = _timeProvider.GetUtcNow();
        var expiredKeys = _entries
            .Where(pair => pair.Value.ExpiresAtUtc <= now)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in expiredKeys)
            RemoveEntry(key);
    }

    private void RemoveEntry(string key)
    {
        _entries.Remove(key);
        if (_lruNodes.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lruNodes.Remove(key);
        }
    }
}
