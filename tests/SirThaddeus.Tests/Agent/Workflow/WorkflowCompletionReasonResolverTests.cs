using SirThaddeus.Agent;
using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests;

public sealed class WorkflowCompletionReasonResolverTests
{
    [Fact]
    public void Resolve_ReturnsRetryBudgetExhausted_WhenLowConfidenceAndRetryGateBlockedByRetryBudget()
    {
        var resolver = new CompletionReasonResolver();
        var state = CreateState();
        state.LastRetryGateDecision = new RetryGateDecision
        {
            IsAllowed = false,
            ReasonCode = "retry_budget_exhausted",
            ReasonMessage = "Retry budget exhausted."
        };

        var reason = resolver.Resolve(CreateSuccessResponse(), state, new ConfidenceSnapshot { Band = "Low" }, TimeSpan.FromSeconds(2));

        Assert.Equal(CompletionReason.RetryBudgetExhausted, reason);
    }

    [Fact]
    public void Resolve_ReturnsToolBudgetExhausted_WhenLowConfidenceAndRetryGateBlockedByToolBudget()
    {
        var resolver = new CompletionReasonResolver();
        var state = CreateState();
        state.LastRetryGateDecision = new RetryGateDecision
        {
            IsAllowed = false,
            ReasonCode = "tool_budget_exhausted",
            ReasonMessage = "Tool budget exhausted."
        };

        var reason = resolver.Resolve(CreateSuccessResponse(), state, new ConfidenceSnapshot { Band = "VeryLow" }, TimeSpan.FromSeconds(2));

        Assert.Equal(CompletionReason.ToolBudgetExhausted, reason);
    }

    [Fact]
    public void Resolve_ReturnsTimeout_WhenLowConfidenceAndRetryGateBlockedByTimeBudget()
    {
        var resolver = new CompletionReasonResolver();
        var state = CreateState();
        state.LastRetryGateDecision = new RetryGateDecision
        {
            IsAllowed = false,
            ReasonCode = "time_budget_exhausted",
            ReasonMessage = "Time budget exhausted."
        };

        var reason = resolver.Resolve(CreateSuccessResponse(), state, new ConfidenceSnapshot { Band = "Low" }, TimeSpan.FromSeconds(2));

        Assert.Equal(CompletionReason.Timeout, reason);
    }

    [Fact]
    public void Resolve_ReturnsSuccessHighConfidence_WhenBandHigh()
    {
        var resolver = new CompletionReasonResolver();
        var state = CreateState();

        var reason = resolver.Resolve(CreateSuccessResponse(), state, new ConfidenceSnapshot { Band = "High" }, TimeSpan.FromSeconds(2));

        Assert.Equal(CompletionReason.SuccessHighConfidence, reason);
    }

    private static TaskRunState CreateState()
    {
        return new TaskRunState
        {
            Envelope = new TaskEnvelope
            {
                UserRequest = "Find details",
                Complexity = TaskComplexity.MultiStepResearch,
                MaxRetries = 1,
                MaxToolCalls = 8,
                TimeBudget = TimeSpan.FromSeconds(20)
            },
            RetriesUsed = 0,
            ToolCallsUsed = 1
        };
    }

    private static AgentResponse CreateSuccessResponse()
    {
        return new AgentResponse
        {
            Text = "ok",
            Success = true
        };
    }
}
