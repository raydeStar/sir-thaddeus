using SirThaddeus.Agent.Validation;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    /// <summary>
    /// Transparent note prepended to responses that fail validation persistently.
    /// </summary>
    private const string PartialAnswerNote =
        "I wasn't fully able to answer this — here's what I found:";

    /// <summary>
    /// Transparent note when a repair attempt was made but also failed.
    /// </summary>
    private const string RepairFailedNote =
        "I tried to improve this answer but wasn't fully successful. Here's what I found:";

    /// <summary>
    /// Runs the completion validator on a response. If validation fails,
    /// executes a bounded repair loop. If repair also fails, returns a
    /// modified response with a transparent partial-answer note.
    /// </summary>
    private async Task<AgentResponse> ValidateAndMaybeRepairAsync(
        string userRequest,
        AgentResponse response,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        // Skip validation for error or unsuccessful responses.
        if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
            return response;

        var hasToolResults = toolCallsMade.Count > 0 &&
                             toolCallsMade.Any(t => t.Success);

        try
        {
            var result = await _completionValidator.ValidateAsync(
                userRequest,
                response.Text,
                hasToolResults,
                cancellationToken);

            LogEvent("COMPLETION_VALIDATION",
                $"passed={result.Passed}, repair={result.RepairNeeded}, " +
                $"elapsed_ms={result.ElapsedMs:F1}" +
                (result.MissingElement is not null ? $", missing={Truncate(result.MissingElement, 80)}" : ""));

            if (result.Passed)
                return response;

            // Attempt bounded repair.
            var repairResult = await _repairLoop.TryRepairAsync(
                userRequest, response.Text, result, toolCallsMade, cancellationToken);

            // Log every repair attempt.
            foreach (var attempt in repairResult.Attempts)
            {
                LogEvent("REPAIR_ATTEMPT",
                    $"attempt={attempt.AttemptNumber}, succeeded={attempt.RepairSucceeded}, " +
                    $"elapsed_ms={attempt.ElapsedMs:F1}, " +
                    $"failure_reason={Truncate(attempt.FailureReason, 80)}");
            }

            if (repairResult.Repaired)
            {
                LogEvent("REPAIR_SUCCEEDED", "Repaired response passed validation.");
                return response with { Text = repairResult.FinalText };
            }

            // Persistent failure: prepend transparent note.
            LogEvent("REPAIR_FAILED", "All repair attempts failed. Applying partial-answer note.");
            var annotatedText = $"{RepairFailedNote}\n\n{response.Text}";
            return response with { Text = annotatedText };
        }
        catch (Exception ex)
        {
            // Fail-open: if validation itself errors, return original response.
            LogEvent("COMPLETION_VALIDATION_ERROR",
                $"Validation error: {ex.Message} — returning unvalidated response.");
            return response;
        }
    }
}
