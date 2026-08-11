using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Agent.Workflow;

/// <summary>
/// Guards the boundary between complete workflow attempts. The active tool
/// loop may perform and verify multiple effects, but an outer confidence retry
/// must not replay a request after the completed attempt changed external
/// state.
/// </summary>
public static class WorkflowRetrySafety
{
    public static bool HasSuccessfulMutation(AgentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.ToolCallsMade.Any(call =>
            call.Success && ToolEffectClassifier.Describe(call.ToolName, call.Arguments).Mutating);
    }
}
