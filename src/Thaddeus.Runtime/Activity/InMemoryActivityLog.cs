using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Activity;

/// <summary>
/// Bounded in-memory <see cref="IActivityLog"/>. The ring buffer keeps the most
/// recent <see cref="Capacity"/> entries; older entries are evicted silently.
/// All members are thread-safe; mutations take a single private lock so the
/// Changed event always reflects the post-mutation state of the buffer.
/// </summary>
public sealed class InMemoryActivityLog : IActivityLog
{
    private readonly object _gate = new();
    private readonly LinkedList<ActivityEntry> _entries = new();
    private readonly Dictionary<string, LinkedListNode<ActivityEntry>> _byId = new();

    /// <summary>Maximum number of entries retained. Defaults to 500.</summary>
    public int Capacity { get; }

    public InMemoryActivityLog(int capacity = 500)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    /// <inheritdoc />
    public event Action<ActivityEntry>? Changed;

    /// <inheritdoc />
    public ActivityEntry Append(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            // Append at front so newest-first listing is O(1).
            var node = _entries.AddFirst(entry);
            _byId[entry.Id] = node;
            while (_entries.Count > Capacity)
            {
                var oldest = _entries.Last!;
                _entries.RemoveLast();
                _byId.Remove(oldest.Value.Id);
            }
        }
        Changed?.Invoke(entry);
        return entry;
    }

    /// <inheritdoc />
    public ActivityEntry? Update(
        string id,
        ActivityStatus? status = null,
        DateTimeOffset? completedAt = null,
        string? summary = null,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ActivityEntry updated;
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var node)) return null;
            var current = node.Value;
            updated = current with
            {
                Status = status ?? current.Status,
                CompletedAt = completedAt ?? current.CompletedAt,
                Summary = summary ?? current.Summary,
                Detail = detail ?? current.Detail,
            };
            node.Value = updated;
        }
        Changed?.Invoke(updated);
        return updated;
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityEntry> List(int limit)
    {
        if (limit < 1) return Array.Empty<ActivityEntry>();
        lock (_gate)
        {
            var take = Math.Min(limit, _entries.Count);
            var arr = new ActivityEntry[take];
            var i = 0;
            foreach (var e in _entries)
            {
                if (i >= take) break;
                arr[i++] = e;
            }
            return arr;
        }
    }

    /// <inheritdoc />
    public ActivityEntry? Get(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var node) ? node.Value : null;
        }
    }

    /// <summary>Helper to mint a new id with the same shape as chat messages.</summary>
    public static string NewId() =>
        "act_" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8)).ToLowerInvariant();
}
