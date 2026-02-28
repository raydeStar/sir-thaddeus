using System.Text;

namespace SirThaddeus.Agent.Validation.Completion;

/// <summary>
/// Generates targeted repair directives from a <see cref="CompletionReport"/>.
/// When tool results are incomplete, the planner produces a focused prompt
/// that tells the LLM exactly what's missing so it can make targeted
/// follow-up tool calls.
///
/// Design rules:
///   • Never fabricates data — only asks the LLM to fetch what's absent.
///   • Repair prompts are short and specific (no open-ended instructions).
///   • Bounded by <see cref="Orchestration.Correlation.RunContext.MaxRepairs"/>.
///   • Returns null when no repair is possible or useful.
/// </summary>
public sealed class RepairPlanner
{
    /// <summary>
    /// Analyzes a completion report and produces a repair directive,
    /// or null if repair is not possible/useful.
    /// </summary>
    /// <param name="report">The completion report to analyze.</param>
    /// <param name="repairAttempt">Current repair attempt number (1-based).</param>
    /// <param name="maxRepairs">Maximum repair attempts allowed.</param>
    /// <returns>A repair directive, or null if repair should not be attempted.</returns>
    public RepairDirective? Plan(CompletionReport report, int repairAttempt, int maxRepairs)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Already complete — no repair needed
        if (report.IsComplete)
            return null;

        // Budget exhausted
        if (repairAttempt > maxRepairs)
            return null;

        // Nothing actionable to repair (no missing fields, only evidence issues)
        // Evidence issues are hard to repair with targeted tool calls
        if (report.MissingFields.Count == 0 && report.Issues.Count == 0)
            return null;

        var prompt = BuildRepairPrompt(report, repairAttempt, maxRepairs);
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        return new RepairDirective
        {
            RepairPrompt = prompt,
            MissingFields = report.MissingFields,
            Issues = report.Issues,
            AttemptNumber = repairAttempt,
            MaxAttempts = maxRepairs
        };
    }

    private static string BuildRepairPrompt(CompletionReport report, int attempt, int max)
    {
        var sb = new StringBuilder();
        sb.Append("[REPAIR ");
        sb.Append(attempt);
        sb.Append('/');
        sb.Append(max);
        sb.AppendLine("]");

        sb.AppendLine("The previous tool results are incomplete. Specifically:");

        if (report.MissingFields.Count > 0)
        {
            sb.Append("- Missing required fields: ");
            sb.AppendJoin(", ", report.MissingFields);
            sb.AppendLine();

            // Map missing fields to actionable instructions
            foreach (var field in report.MissingFields)
            {
                var fieldReq = report.Contract.Fields
                    .FirstOrDefault(f => f.FieldName.Equals(field, StringComparison.OrdinalIgnoreCase));

                if (fieldReq is not null && !string.IsNullOrEmpty(fieldReq.Description))
                {
                    sb.Append("  → Please find the ");
                    sb.Append(fieldReq.Description);
                    sb.AppendLine();
                }
            }
        }

        foreach (var issue in report.Issues)
        {
            sb.Append("- ");
            sb.AppendLine(issue);
        }

        if (report.Contract.MinItems > 0 && report.ItemCount < report.Contract.MinItems)
        {
            sb.Append("- Need at least ");
            sb.Append(report.Contract.MinItems);
            sb.Append(" result(s), only got ");
            sb.Append(report.ItemCount);
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Make targeted tool calls to fill ONLY the missing information.");
        sb.AppendLine("Do NOT repeat previous tool calls that already succeeded.");
        sb.AppendLine("Do NOT fabricate or guess missing data.");
        sb.AppendLine("[/REPAIR]");

        return sb.ToString();
    }
}

/// <summary>
/// A targeted repair directive produced by <see cref="RepairPlanner"/>.
/// Injected into the LLM conversation as a system/user message to
/// guide focused follow-up tool calls.
/// </summary>
public sealed record RepairDirective
{
    /// <summary>The prompt to inject into the conversation.</summary>
    public required string RepairPrompt { get; init; }

    /// <summary>Fields that are still missing.</summary>
    public IReadOnlyList<string> MissingFields { get; init; } = [];

    /// <summary>Issues found during completion checking.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>Which repair attempt this is (1-based).</summary>
    public int AttemptNumber { get; init; }

    /// <summary>Maximum repair attempts allowed.</summary>
    public int MaxAttempts { get; init; }
}
