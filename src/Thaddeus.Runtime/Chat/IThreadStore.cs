using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Persistent store for chat threads. Implementations must be safe to call from
/// concurrent request handlers; mutations should serialize per-thread.
/// </summary>
public interface IThreadStore
{
    /// <summary>List threads ordered by most-recently-updated first.</summary>
    Task<IReadOnlyList<ChatThread>> ListAsync(CancellationToken ct);

    /// <summary>Get a thread by id, or null if not found.</summary>
    Task<ChatThread?> GetAsync(string threadId, CancellationToken ct);

    /// <summary>Create a new empty thread with the given title and return it.</summary>
    Task<ChatThread> CreateAsync(string title, CancellationToken ct);

    /// <summary>Append a message to the thread and return the updated thread.</summary>
    /// <exception cref="KeyNotFoundException">If the thread does not exist.</exception>
    Task<ChatThread> AppendMessageAsync(string threadId, ChatMessage message, CancellationToken ct);

    /// <summary>Rename a thread. Returns the updated thread, or null if not found.</summary>
    Task<ChatThread?> RenameAsync(string threadId, string newTitle, CancellationToken ct);

    /// <summary>Pin or unpin a thread. Returns the updated thread, or null if not found.</summary>
    Task<ChatThread?> SetPinnedAsync(string threadId, bool pinned, CancellationToken ct);

    /// <summary>Delete a thread; returns true if a thread was removed.</summary>
    Task<bool> DeleteAsync(string threadId, CancellationToken ct);
}
