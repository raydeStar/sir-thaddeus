using SirThaddeus.Agent;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// Replaces redacted audit summaries with the exact tool results that the model
/// received. The source evidence is available only from the harness-only runtime
/// endpoint; normal runtime events and audit logs remain redacted.
/// </summary>
internal static class ToolEvidenceTraceEnricher
{
    public static (
        IReadOnlyList<ToolCallRecord> ToolCalls,
        IReadOnlyList<RecordedToolTurn> ToolTurns,
        IReadOnlyList<TraceStep> Steps)
        Enrich(
            (IReadOnlyList<ToolCallRecord> ToolCalls,
             IReadOnlyList<RecordedToolTurn> ToolTurns,
             IReadOnlyList<TraceStep> Steps) trace,
            IReadOnlyList<ToolCallRecord> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0)
            return trace;

        var byOccurrence = BuildOccurrenceMap(evidence);

        var toolCalls = ReplaceByOccurrence(
            trace.ToolCalls,
            call => call.ToolName,
            (call, replacement) => call with
            {
                Arguments = replacement.Arguments,
                Result = replacement.Result,
                Success = replacement.Success
            },
            byOccurrence);

        var toolTurns = ReplaceByOccurrence(
            trace.ToolTurns,
            turn => turn.ToolName,
            (turn, replacement) => turn with
            {
                ArgumentsJson = replacement.Arguments,
                ResultText = replacement.Result,
                Success = replacement.Success
            },
            byOccurrence);

        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var steps = trace.Steps.Select(step =>
        {
            if (!string.Equals(step.StepType, "tool_result", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(step.ToolName))
            {
                return step;
            }

            var key = NextOccurrenceKey(step.ToolName, occurrences);
            if (!byOccurrence.TryGetValue(key, out var replacement))
                return step;

            return step with
            {
                Arguments = replacement.Arguments,
                Result = replacement.Success
                    ? ToolResultPayloads.BuildSuccess(replacement.Result)
                    : ToolResultPayloads.BuildErrorJson("tool_error", replacement.Result, false),
                Error = replacement.Success
                    ? null
                    : new TraceError
                    {
                        Code = "tool_error",
                        Message = replacement.Result,
                        Retriable = false
                    }
            };
        }).ToArray();

        return (toolCalls, toolTurns, steps);
    }

    private static IReadOnlyList<T> ReplaceByOccurrence<T>(
        IReadOnlyList<T> values,
        Func<T, string> getToolName,
        Func<T, ToolCallRecord, T> replace,
        IReadOnlyDictionary<string, ToolCallRecord> byOccurrence)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return values.Select(value =>
        {
            var key = NextOccurrenceKey(getToolName(value), occurrences);
            return byOccurrence.TryGetValue(key, out var replacement)
                ? replace(value, replacement)
                : value;
        }).ToArray();
    }

    private static Dictionary<string, ToolCallRecord> BuildOccurrenceMap(
        IReadOnlyList<ToolCallRecord> evidence)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ToolCallRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in evidence)
            result[NextOccurrenceKey(call.ToolName, occurrences)] = call;
        return result;
    }

    private static string NextOccurrenceKey(
        string toolName,
        IDictionary<string, int> occurrences)
    {
        occurrences.TryGetValue(toolName, out var occurrence);
        occurrences[toolName] = occurrence + 1;
        return $"{toolName}\u001f{occurrence}";
    }
}
