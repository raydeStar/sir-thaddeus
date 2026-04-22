using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Activity;

/// <summary>
/// In-memory ring buffer of recent <see cref="ActivityEntry"/> values surfaced
/// in the Activity UI. Bounded so a long-running runtime never grows unbounded
/// memory; the buffer evicts oldest entries first.
/// </summary>
public interface IActivityLog
{
    /// <summary>Append a new entry. Returns the entry as stored.</summary>
    ActivityEntry Append(ActivityEntry entry);

    /// <summary>
    /// Update the status, completion timestamp, summary, or detail of an existing
    /// entry. No-op if the id has been evicted from the ring buffer.
    /// </summary>
    /// <returns>The updated entry, or null if not found.</returns>
    ActivityEntry? Update(
        string id,
        ActivityStatus? status = null,
        DateTimeOffset? completedAt = null,
        string? summary = null,
        string? detail = null);

    /// <summary>List entries newest-first, capped at <paramref name="limit"/>.</summary>
    IReadOnlyList<ActivityEntry> List(int limit);

    /// <summary>Lookup a single entry by id. Null if missing or evicted.</summary>
    ActivityEntry? Get(string id);

    /// <summary>Raised whenever an entry is appended or updated. Subscribers must be cheap.</summary>
    event Action<ActivityEntry>? Changed;
}
