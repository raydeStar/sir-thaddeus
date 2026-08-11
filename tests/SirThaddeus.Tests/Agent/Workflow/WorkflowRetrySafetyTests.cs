using SirThaddeus.Agent;
using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests;

public sealed class WorkflowRetrySafetyTests
{
    [Fact]
    public void SuccessfulSystemExecutionMakesCrossAttemptRetryUnsafe()
    {
        var response = Response(Call("system_execute", success: true));

        Assert.True(WorkflowRetrySafety.HasSuccessfulMutation(response));
    }

    [Fact]
    public void SuccessfulReadOnlyEvidenceDoesNotDisableRetry()
    {
        var response = Response(Call("web_search", success: true));

        Assert.False(WorkflowRetrySafety.HasSuccessfulMutation(response));
    }

    [Fact]
    public void FailedMutationDoesNotClaimSuccessfulExternalChange()
    {
        var response = Response(Call("system_execute", success: false));

        Assert.False(WorkflowRetrySafety.HasSuccessfulMutation(response));
    }

    private static AgentResponse Response(ToolCallRecord call) => new()
    {
        Text = "attempt complete",
        ToolCallsMade = [call]
    };

    private static ToolCallRecord Call(string name, bool success) => new()
    {
        ToolName = name,
        Arguments = "{}",
        Result = success ? "ok" : "permission denied",
        Success = success
    };
}
