namespace SirThaddeus.Agent.Workflow;

public sealed class RetryGateEvaluator : IRetryGateEvaluator
{
    public RetryGateDecision Evaluate(TaskRunState state, ConfidenceSnapshot confidence, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(confidence);

        var remainingRetries = Math.Max(0, state.Envelope.MaxRetries - state.RetriesUsed);
        var remainingToolCalls = Math.Max(0, state.Envelope.MaxToolCalls - state.ToolCallsUsed);
        var remainingTimeMs = Math.Max(0, (int)Math.Floor((state.Envelope.TimeBudget - elapsed).TotalMilliseconds));

        if (!confidence.ShouldRetry)
        {
            return BuildBlocked("confidence_not_retry", "Confidence does not require retry.");
        }

        if (!state.Envelope.NeedsTools)
        {
            return BuildBlocked(
                "search_retry_not_applicable",
                "The available retry strategies require live tools, but this request is direct-answer only.");
        }

        if (remainingRetries <= 0)
        {
            return BuildBlocked("retry_budget_exhausted", "Retry budget exhausted.");
        }

        if (remainingToolCalls <= 0)
        {
            return BuildBlocked("tool_budget_exhausted", "Tool-call budget exhausted.");
        }

        if (remainingTimeMs <= 0)
        {
            return BuildBlocked("time_budget_exhausted", "Time budget exhausted.");
        }

        return new RetryGateDecision
        {
            IsAllowed = true,
            ReasonCode = "allowed",
            ReasonMessage = "Retry allowed.",
            RemainingRetries = remainingRetries,
            RemainingToolCalls = remainingToolCalls,
            RemainingTimeMs = remainingTimeMs
        };

        RetryGateDecision BuildBlocked(string code, string message)
        {
            return new RetryGateDecision
            {
                IsAllowed = false,
                ReasonCode = code,
                ReasonMessage = message,
                RemainingRetries = remainingRetries,
                RemainingToolCalls = remainingToolCalls,
                RemainingTimeMs = remainingTimeMs
            };
        }
    }
}
