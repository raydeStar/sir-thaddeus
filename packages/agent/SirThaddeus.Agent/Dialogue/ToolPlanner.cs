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
    public string PlannerMessage { get; init; } = "";
    public bool InjectionMitigationApplied { get; init; }
    public string InjectionMitigationReason { get; init; } = "";
    public bool RequiresToolExecution => ToolCalls.Count > 0;
}

public interface IToolPlanner
{
    ToolPlanDecision Plan(
        ValidatedSlots slots,
        DialogueState currentState,
        string? userLocationHint = null,
        string? preferredUnits = null);
}

/// <summary>
/// Deterministic planner fed strictly by validated slots.
/// </summary>
public sealed class ToolPlanner : IToolPlanner
{
    public ToolPlanDecision Plan(
        ValidatedSlots slots,
        DialogueState currentState,
        string? userLocationHint = null,
        string? preferredUnits = null)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(currentState);

        if (slots.RequiresLocationConfirmation)
        {
            return new ToolPlanDecision
            {
                Category = "confirm",
                InlineAnswer = slots.MismatchWarning ?? "Please confirm the location before I continue.",
                PlannerMessage = slots.NormalizedMessage
            };
        }

        var injectionAssessment = PromptInjectionGuard.Assess(slots.NormalizedMessage);
        var plannerMessage = injectionAssessment.IsUntrusted
            ? injectionAssessment.FilteredMessage
            : slots.NormalizedMessage;

        if (injectionAssessment.IsUntrusted && string.IsNullOrWhiteSpace(plannerMessage))
        {
            return new ToolPlanDecision
            {
                Category = "security",
                InlineAnswer =
                    "I filtered untrusted instruction content and could not find a safe request to execute. " +
                    "Please restate your request plainly.",
                PlannerMessage = plannerMessage,
                InjectionMitigationApplied = true,
                InjectionMitigationReason = injectionAssessment.Reason
            };
        }

        var utility = UtilityRouter.TryHandle(
            plannerMessage,
            userLocationHint,
            preferredUnits);
        if (utility is null)
        {
            return new ToolPlanDecision
            {
                Category = "none",
                PlannerMessage = plannerMessage,
                InjectionMitigationApplied = injectionAssessment.IsUntrusted,
                InjectionMitigationReason = injectionAssessment.Reason
            };
        }

        // Enforce validated slot source-of-truth for location-sensitive args.
        if ((utility.Category == "weather" || utility.Category == "time") &&
            !string.IsNullOrWhiteSpace(slots.LocationText))
        {
            var resolvedPlace = UtilityRouter.ResolveProximityPlace(slots.LocationText, userLocationHint);
            var args = JsonSerializer.Serialize(new
            {
                place = resolvedPlace,
                maxResults = 3
            });

            return new ToolPlanDecision
            {
                Category = utility.Category,
                PlannerMessage = plannerMessage,
                InjectionMitigationApplied = injectionAssessment.IsUntrusted,
                InjectionMitigationReason = injectionAssessment.Reason,
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
                PlannerMessage = plannerMessage,
                InjectionMitigationApplied = injectionAssessment.IsUntrusted,
                InjectionMitigationReason = injectionAssessment.Reason,
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
            InlineAnswer = utility.Answer,
            PlannerMessage = plannerMessage,
            InjectionMitigationApplied = injectionAssessment.IsUntrusted,
            InjectionMitigationReason = injectionAssessment.Reason
        };
    }
}
