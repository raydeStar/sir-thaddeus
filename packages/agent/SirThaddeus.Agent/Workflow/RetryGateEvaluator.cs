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

        // A workflow retry re-enters the complete agent pipeline with its
        // mutation tools available. Once an attempt has successfully changed
        // external state, replaying the request is not a safe confidence
        // repair: it can duplicate or undo already-completed work. The active
        // tool loop remains free to continue and verify before it returns;
        // this guard applies only at the cross-attempt boundary.
        if (state.HasSuccessfulMutation)
        {
            return BuildBlocked(
                "mutating_attempt_not_retry_safe",
                "Automatic retry skipped because the completed attempt changed external state.");
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
