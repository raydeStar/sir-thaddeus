using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests;

public sealed class WorkflowRetryGateEvaluatorTests
{
    [Fact]
    public void Evaluate_AllowsRetry_WhenAllBudgetsRemain()
    {
        var evaluator = new RetryGateEvaluator();
        var state = CreateState(maxRetries: 2, retriesUsed: 0, maxToolCalls: 8, toolCallsUsed: 1, timeBudgetSec: 30);
        var confidence = new ConfidenceSnapshot { ShouldRetry = true };

        var decision = evaluator.Evaluate(state, confidence, TimeSpan.FromSeconds(5));

        Assert.True(decision.IsAllowed);
        Assert.Equal("allowed", decision.ReasonCode);
        Assert.Equal(2, decision.RemainingRetries);
        Assert.Equal(7, decision.RemainingToolCalls);
        Assert.True(decision.RemainingTimeMs > 0);
    }

    [Fact]
    public void Evaluate_BlocksRetry_WhenRetryBudgetExhausted()
    {
        var evaluator = new RetryGateEvaluator();
        var state = CreateState(maxRetries: 1, retriesUsed: 1, maxToolCalls: 8, toolCallsUsed: 1, timeBudgetSec: 30);
        var confidence = new ConfidenceSnapshot { ShouldRetry = true };

        var decision = evaluator.Evaluate(state, confidence, TimeSpan.FromSeconds(5));

        Assert.False(decision.IsAllowed);
        Assert.Equal("retry_budget_exhausted", decision.ReasonCode);
    }

    [Fact]
    public void Evaluate_BlocksRetry_WhenToolBudgetExhausted()
    {
        var evaluator = new RetryGateEvaluator();
        var state = CreateState(maxRetries: 2, retriesUsed: 0, maxToolCalls: 2, toolCallsUsed: 2, timeBudgetSec: 30);
        var confidence = new ConfidenceSnapshot { ShouldRetry = true };

        var decision = evaluator.Evaluate(state, confidence, TimeSpan.FromSeconds(5));

        Assert.False(decision.IsAllowed);
        Assert.Equal("tool_budget_exhausted", decision.ReasonCode);
    }

    [Fact]
    public void Evaluate_BlocksRetry_WhenTimeBudgetExhausted()
    {
        var evaluator = new RetryGateEvaluator();
        var state = CreateState(maxRetries: 2, retriesUsed: 0, maxToolCalls: 8, toolCallsUsed: 1, timeBudgetSec: 10);
        var confidence = new ConfidenceSnapshot { ShouldRetry = true };

        var decision = evaluator.Evaluate(state, confidence, TimeSpan.FromSeconds(10));

        Assert.False(decision.IsAllowed);
        Assert.Equal("time_budget_exhausted", decision.ReasonCode);
        Assert.Equal(0, decision.RemainingTimeMs);
    }

    [Fact]
    public void Evaluate_BlocksRetry_WhenConfidenceDoesNotRequireRetry()
    {
        var evaluator = new RetryGateEvaluator();
        var state = CreateState(maxRetries: 2, retriesUsed: 0, maxToolCalls: 8, toolCallsUsed: 1, timeBudgetSec: 30);
        var confidence = new ConfidenceSnapshot { ShouldRetry = false };

        var decision = evaluator.Evaluate(state, confidence, TimeSpan.FromSeconds(5));

        Assert.False(decision.IsAllowed);
        Assert.Equal("confidence_not_retry", decision.ReasonCode);
    }

    private static TaskRunState CreateState(int maxRetries, int retriesUsed, int maxToolCalls, int toolCallsUsed, int timeBudgetSec)
    {
        return new TaskRunState
        {
            Envelope = new TaskEnvelope
            {
                UserRequest = "Find details",
                Complexity = TaskComplexity.MultiStepResearch,
                MaxRetries = maxRetries,
                MaxToolCalls = maxToolCalls,
                TimeBudget = TimeSpan.FromSeconds(timeBudgetSec)
            },
            RetriesUsed = retriesUsed,
            ToolCallsUsed = toolCallsUsed
        };
    }
}
