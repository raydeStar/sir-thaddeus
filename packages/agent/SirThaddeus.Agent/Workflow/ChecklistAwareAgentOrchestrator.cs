using System.Diagnostics;
using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Agent.Workflow;

/// <summary>
/// Decorator that intercepts IAgentOrchestrator calls to enforce workflow
/// time-budget constraints. Set budget via SetRunBudget before each run.
/// </summary>
public sealed class ChecklistAwareAgentOrchestrator : IAgentOrchestrator
{
    private readonly IAgentOrchestrator _inner;
    private Stopwatch? _runStopwatch;
    private TimeSpan _runTimeBudget;

    public ChecklistAwareAgentOrchestrator(IAgentOrchestrator inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// Arms the per-run time budget. Must be called once after starting the
    /// run stopwatch and before any ProcessAsync calls.
    /// </summary>
    public void SetRunBudget(TimeSpan timeBudget, Stopwatch runStopwatch)
    {
        _runTimeBudget = timeBudget;
        _runStopwatch = runStopwatch;
    }

    public async Task<AgentResponse> ProcessAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        if (_runStopwatch is { } sw)
        {
            var remaining = _runTimeBudget - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException("Workflow time budget exhausted before LLM call.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(remaining);
            return await _inner.ProcessAsync(userMessage, cts.Token);
        }
        return await _inner.ProcessAsync(userMessage, cancellationToken);
    }

    public async Task<AgentResponse> ProcessAsync(
        string userMessage,
        string? conversationId,
        CancellationToken cancellationToken = default)
    {
        if (_runStopwatch is { } sw)
        {
            var remaining = _runTimeBudget - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException("Workflow time budget exhausted before LLM call.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(remaining);
            return await _inner.ProcessAsync(userMessage, conversationId, cts.Token);
        }
        return await _inner.ProcessAsync(userMessage, conversationId, cancellationToken);
    }

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