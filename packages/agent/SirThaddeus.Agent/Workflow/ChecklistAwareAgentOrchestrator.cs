using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Agent.Workflow;

/// <summary>
/// Decorator seam for checklist/progress/confidence orchestration.
/// Phase 1 starts as a no-op wrapper to preserve existing behavior.
/// </summary>
public sealed class ChecklistAwareAgentOrchestrator : IAgentOrchestrator
{
    private readonly IAgentOrchestrator _inner;

    public ChecklistAwareAgentOrchestrator(IAgentOrchestrator inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<AgentResponse> ProcessAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
        => _inner.ProcessAsync(userMessage, cancellationToken);

    public Task<AgentResponse> ProcessAsync(
        string userMessage,
        string? conversationId,
        CancellationToken cancellationToken = default)
        => _inner.ProcessAsync(userMessage, conversationId, cancellationToken);

    public void ResetConversation()
        => _inner.ResetConversation();

    public void SeedDialogueState(DialogueState state)
        => _inner.SeedDialogueState(state);

    public DialogueContextSnapshot GetContextSnapshot()
        => _inner.GetContextSnapshot();

    public bool ContextLocked
    {
        get => _inner.ContextLocked;
        set => _inner.ContextLocked = value;
    }

    public Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default)
        => _inner.GetAvailableToolCountAsync(cancellationToken);
}