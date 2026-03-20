using System.Collections.Concurrent;

namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Tracks files that need frontmatter generation.
/// Processed during idle time, between user interactions,
/// or on next startup.
/// </summary>
public sealed class TaggingQueue
{
    private readonly ConcurrentQueue<string> _pendingFiles = new();

    public void Enqueue(string relativePath) =>
        _pendingFiles.Enqueue(relativePath);

    public bool TryDequeue(out string? path) =>
        _pendingFiles.TryDequeue(out path);

    public int PendingCount => _pendingFiles.Count;
    public bool HasPending => !_pendingFiles.IsEmpty;
}
