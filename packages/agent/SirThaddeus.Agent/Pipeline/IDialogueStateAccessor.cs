using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Port for per-conversation dialogue state (topic, location anchor,
/// time scope, rolling summary, context lock). Adapters decide <em>how</em>
/// state is stored — the UI runtime uses a thread-scoped dictionary so
/// different chat threads get different contexts; the CLI runtime wraps
/// the existing singleton <see cref="DialogueStateStore"/> to match the
/// legacy orchestrator's "one active conversation" model.
///
/// <para>Steps never hold the state directly — they read/write via the
/// accessor. That keeps the step logic identical across runtimes while
/// the storage model differs.</para>
///
/// <para>Implementations must be safe to call concurrently — a tool-loop
/// that spans seconds can race against a user's "stop" button that resets
/// the thread's state, for example.</para>
/// </summary>
public interface IDialogueStateAccessor
{
    /// <summary>Read the current state for the given conversation.
    /// Implementations return an empty <see cref="DialogueState"/> (not
    /// null) when the conversation has no prior context — matches the
    /// legacy <see cref="IDialogueStateStore.Get"/> contract.</summary>
    DialogueState Get(string conversationId);

    /// <summary>Replace the state for the given conversation atomically.
    /// Used when a step applies patches from tool results or from a
    /// context-locking directive.</summary>
    void Update(string conversationId, DialogueState next);

    /// <summary>Clear any stored state for the conversation. Called by
    /// the facade on "start new chat" / thread-reset actions.</summary>
    void Reset(string conversationId);
}

/// <summary>
/// Null accessor — every <see cref="Get"/> returns a fresh empty state,
/// every <see cref="Update"/> / <see cref="Reset"/> is a no-op. Used by
/// runtimes that don't persist dialogue state (tests, minimal bootstraps).
/// </summary>
public sealed class NullDialogueStateAccessor : IDialogueStateAccessor
{
    public static readonly NullDialogueStateAccessor Instance = new();

    public DialogueState Get(string conversationId) => new();
    public void Update(string conversationId, DialogueState next) { }
    public void Reset(string conversationId) { }
}
