using System.Text.Json;
using SirThaddeus.Agent.Planning;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    /// <summary>
    /// Builds a typed task plan from the lane result and available tools.
    /// Returns null if plan generation fails or is not applicable.
    /// </summary>
    private async Task<TaskPlan?> BuildTaskPlanAsync(
        string userMessage,
        LaneRoutingResult laneResult,
        IReadOnlyList<ToolDefinition> filteredTools,
        CancellationToken cancellationToken)
    {
        var toolNames = filteredTools.Select(t => t.Function.Name).ToList();

        try
        {
            var plan = await _planBuilder.BuildPlanAsync(
                userMessage,
                laneResult.Lane,
                toolNames,
                cancellationToken);

            if (plan is not null)
            {
                LogEvent("TASK_PLAN",
                    $"kind={plan.TaskKind}, lane={plan.Lane}, " +
                    $"steps={plan.Steps.Count}, tools=[{string.Join(", ", plan.RequiredTools)}], " +
                    $"stop={Truncate(plan.StopCondition, 80)}");
            }
            else
            {
                LogEvent("TASK_PLAN_SKIPPED", "Plan generation returned null — using unplanned execution.");
            }

            return plan;
        }
        catch (Exception ex)
        {
            LogEvent("TASK_PLAN_ERROR", $"Plan generation failed: {ex.Message} — falling back to unplanned execution.");
            return null;
        }
    }
}
