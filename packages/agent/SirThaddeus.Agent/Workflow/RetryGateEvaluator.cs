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

        var overrideReason = (state.Envelope.RetryGateOverrideReason ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(overrideReason))
        {
            if (string.Equals(overrideReason, "allowed", StringComparison.OrdinalIgnoreCase))
            {
                return new RetryGateDecision
                {
                    IsAllowed = true,
                    ReasonCode = "allowed",
                    ReasonMessage = "Retry allowed by test override.",
                    RemainingRetries = remainingRetries,
                    RemainingToolCalls = remainingToolCalls,
                    RemainingTimeMs = remainingTimeMs
                };
            }

            return BuildBlocked(overrideReason.ToLowerInvariant(), "Retry blocked by test override.");
        }

        if (!confidence.ShouldRetry)
        {
            return BuildBlocked("confidence_not_retry", "Confidence does not require retry.");
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
