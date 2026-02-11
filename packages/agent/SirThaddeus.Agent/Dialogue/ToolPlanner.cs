using System.Text.Json;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Dialogue;

public sealed record PlannedToolCall
{
    public string ToolName { get; init; } = "";
    public string ArgumentsJson { get; init; } = "{}";
}

public sealed record ToolPlanDecision
{
    public string Category { get; init; } = "none";
    public string? InlineAnswer { get; init; }
    public IReadOnlyList<PlannedToolCall> ToolCalls { get; init; } = Array.Empty<PlannedToolCall>();
    public bool RequiresToolExecution => ToolCalls.Count > 0;
}

public interface IToolPlanner
{
    ToolPlanDecision Plan(ValidatedSlots slots, DialogueState currentState);
}

/// <summary>
/// Deterministic planner fed strictly by validated slots.
/// </summary>
public sealed class ToolPlanner : IToolPlanner
{
    public ToolPlanDecision Plan(ValidatedSlots slots, DialogueState currentState)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(currentState);

        if (slots.RequiresLocationConfirmation)
        {
            return new ToolPlanDecision
            {
                Category = "confirm",
                InlineAnswer = slots.MismatchWarning ?? "Please confirm the location before I continue."
            };
        }

        var utility = UtilityRouter.TryHandle(slots.NormalizedMessage);
        if (utility is null)
            return new ToolPlanDecision { Category = "none" };

        // Enforce validated slot source-of-truth for location-sensitive args.
        if ((utility.Category == "weather" || utility.Category == "time") &&
            !string.IsNullOrWhiteSpace(slots.LocationText))
        {
            var args = JsonSerializer.Serialize(new
            {
                place = slots.LocationText,
                maxResults = 3
            });

            return new ToolPlanDecision
            {
                Category = utility.Category,
                ToolCalls =
                [
                    new PlannedToolCall
                    {
                        ToolName = "weather_geocode",
                        ArgumentsJson = args
                    }
                ]
            };
        }

        if (!string.IsNullOrWhiteSpace(utility.McpToolName) &&
            !string.IsNullOrWhiteSpace(utility.McpToolArgs))
        {
            return new ToolPlanDecision
            {
                Category = utility.Category,
                ToolCalls =
                [
                    new PlannedToolCall
                    {
                        ToolName = utility.McpToolName,
                        ArgumentsJson = utility.McpToolArgs
                    }
                ]
            };
        }

        return new ToolPlanDecision
        {
            Category = utility.Category,
            InlineAnswer = utility.Answer
        };
    }
}
