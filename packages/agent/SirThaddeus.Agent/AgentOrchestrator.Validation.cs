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
    /// Runs the completion validator on a response. If validation fails,
    /// returns a modified response with a transparent partial-answer note.
    /// When the Bounded Repair Loop is wired in, this will trigger repair
    /// instead of immediately applying the note.
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

            // TODO: When Bounded Repair Loop is implemented, trigger repair here
            // instead of immediately falling back to the transparent note.

            // Persistent failure path: prepend a transparent partial-answer note.
            var annotatedText = $"{PartialAnswerNote}\n\n{response.Text}";
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
