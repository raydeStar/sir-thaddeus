using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// <see cref="IDialogueStateAccessor"/> backed by a single
/// <see cref="IDialogueStateStore"/>. Conversation ids are ignored —
/// the whole process holds one active conversation, matching the CLI
/// and the harness where there's never more than one chat thread at a
/// time.
///
/// <para>For per-thread partitioning (multiple chats in flight), use a
/// runtime-specific accessor that keys by conversation id instead.</para>
/// </summary>
public sealed class SingletonDialogueStateAccessor : IDialogueStateAccessor
{
    private readonly IDialogueStateStore _store;

    public SingletonDialogueStateAccessor(IDialogueStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public DialogueState Get(string conversationId) => _store.Get();

    public void Update(string conversationId, DialogueState next) => _store.Update(next);

    public void Reset(string conversationId) => _store.Reset();
}
