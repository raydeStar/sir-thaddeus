namespace SirThaddeus.Agent.Workflow;

public sealed class CompletionReasonResolver : ICompletionReasonResolver
{
    public CompletionReason Resolve(AgentResponse response, TaskRunState workflowState, ConfidenceSnapshot? confidence, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(workflowState);

        if (!response.Success)
        {
            return CompletionReason.Failed;
        }

        if (elapsed > workflowState.Envelope.TimeBudget)
        {
            return CompletionReason.Timeout;
        }

        if (workflowState.ToolCallsUsed >= workflowState.Envelope.MaxToolCalls)
        {
            return CompletionReason.ToolBudgetExhausted;
        }

        if (confidence?.Band == "High")
        {
            return CompletionReason.SuccessHighConfidence;
        }

        if (confidence?.Band is "Low" or "VeryLow")
        {
            var retryGateReason = workflowState.LastRetryGateDecision?.ReasonCode;
            if (string.Equals(retryGateReason, "retry_budget_exhausted", StringComparison.Ordinal))
            {
                return CompletionReason.RetryBudgetExhausted;
            }

            if (string.Equals(retryGateReason, "tool_budget_exhausted", StringComparison.Ordinal))
            {
                return CompletionReason.ToolBudgetExhausted;
            }

            if (string.Equals(retryGateReason, "time_budget_exhausted", StringComparison.Ordinal))
            {
                return CompletionReason.Timeout;
            }

            if (workflowState.RetriesUsed >= workflowState.Envelope.MaxRetries)
            {
                return CompletionReason.RetryBudgetExhausted;
            }
        }

        return CompletionReason.SuccessMediumConfidence;
    }
}
