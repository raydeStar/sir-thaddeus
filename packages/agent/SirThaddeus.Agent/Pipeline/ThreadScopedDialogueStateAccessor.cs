using System.Collections.Concurrent;
using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// <see cref="IDialogueStateAccessor"/> that keeps a separate
/// <see cref="DialogueState"/> per conversation id. Used by the desktop
/// UI runtime where multiple chat threads can be in-flight concurrently
/// and each needs its own topic / location / time-scope context.
///
/// <para>In-memory only — the thread store handles message history
/// persistence; dialogue state lives for the process lifetime and is
/// rebuilt as turns flow. If you need state to survive a restart, add a
/// persistent adapter that writes to SQLite on <see cref="Update"/>.</para>
///
/// <para>Thread-safe. Concurrent readers and writers on the same
/// conversation id are serialized via <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// atomic updates.</para>
/// </summary>
public sealed class ThreadScopedDialogueStateAccessor : IDialogueStateAccessor
{
    private readonly ConcurrentDictionary<string, DialogueState> _byThread = new(StringComparer.Ordinal);

    public DialogueState Get(string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        return _byThread.TryGetValue(conversationId, out var existing) ? existing : new DialogueState();
    }

    public void Update(string conversationId, DialogueState next)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(next);
        _byThread[conversationId] = next;
    }

    public void Reset(string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        _byThread.TryRemove(conversationId, out _);
    }
}
