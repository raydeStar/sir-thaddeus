using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Planning;

/// <summary>
/// Validates a <see cref="TaskPlan"/> before execution begins.
/// Returns a list of validation errors (empty = valid).
/// </summary>
public static class PlanValidator
{
    /// <summary>
    /// Validates the plan. Returns an empty list when valid,
    /// otherwise a list of human-readable error strings.
    /// </summary>
    /// <param name="plan">The plan to validate.</param>
    /// <param name="availableToolNames">
    /// Tool names available for the current route (from PolicyGate + ToolCapabilityRegistry).
    /// </param>
    public static IReadOnlyList<string> Validate(
        TaskPlan plan,
        IReadOnlyCollection<string> availableToolNames)
    {
        var errors = new List<string>();

        if (plan.Steps.Count == 0)
            errors.Add("Steps must contain at least 1 step.");

        if (string.IsNullOrWhiteSpace(plan.StopCondition))
            errors.Add("StopCondition must not be empty.");

        if (string.IsNullOrWhiteSpace(plan.SuccessCriteria))
            errors.Add("SuccessCriteria must not be empty.");

        if (string.IsNullOrWhiteSpace(plan.TaskKind))
            errors.Add("TaskKind must not be empty.");

        // Validate that all required tools exist in the available set.
        // An empty RequiredTools list is valid (e.g. chat-only or deterministic).
        var available = new HashSet<string>(availableToolNames, StringComparer.OrdinalIgnoreCase);
        foreach (var tool in plan.RequiredTools)
        {
            if (!available.Contains(tool))
                errors.Add($"RequiredTool '{tool}' is not available for this lane/route.");
        }

        return errors;
    }
}
