using System.Diagnostics;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests.Agent.Workflow;

public sealed class TimeBudgetedAgentOrchestratorTests
{
    [Fact]
    public async Task SetRunBudget_propagates_deadline_to_aware_inner_agent()
    {
        var inner = new DeadlineAwareOrchestrator();
        var decorator = new TimeBudgetedAgentOrchestrator(inner);
        var stopwatch = Stopwatch.StartNew();
        var before = DateTimeOffset.UtcNow.AddSeconds(29);

        decorator.SetRunBudget(TimeSpan.FromSeconds(30), stopwatch);
        await decorator.ProcessAsync("hello");

        Assert.NotNull(inner.DeadlineUtc);
        Assert.InRange(inner.DeadlineUtc!.Value, before, DateTimeOffset.UtcNow.AddSeconds(31));
    }

    private sealed class DeadlineAwareOrchestrator : IAgentOrchestrator, IWorkflowDeadlineAwareAgent
    {
        public DateTimeOffset? DeadlineUtc { get; private set; }

        public void SetWorkflowDeadline(DateTimeOffset? deadlineUtc) => DeadlineUtc = deadlineUtc;

        public Task<AgentResponse> ProcessAsync(
            string userMessage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = "ok", Success = true });

        public Task<AgentResponse> ProcessAsync(
            string userMessage,
            string? conversationId,
            CancellationToken cancellationToken = default) =>
            ProcessAsync(userMessage, cancellationToken);
    }
}
