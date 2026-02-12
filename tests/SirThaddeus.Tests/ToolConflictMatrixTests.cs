using SirThaddeus.Agent;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class ToolConflictMatrixTests
{
    [Fact]
    public void ResolveTurn_BlocksPolicyForbiddenToolsBeforeExecution()
    {
        ToolCallRequest[] calls =
        [
            ToolCall("call_1", "web_search", """{"query":"news"}"""),
            ToolCall("call_2", "system_execute", """{"command":"dotnet --info"}""")
        ];

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "web_search"
        };

        var resolution = ToolConflictMatrix.ResolveTurn(calls, allowed);

        Assert.Single(resolution.Winners);
        Assert.Equal("web_search", resolution.Winners[0].Function.Name);

        var skipped = Assert.Single(resolution.Skipped);
        Assert.Equal("system_execute", skipped.ToolCall.Function.Name);
        Assert.Equal(ToolConflictReason.PolicyForbid, skipped.Reason);
        Assert.Equal("policy_forbid", ToolConflictMatrix.ToReasonCode(skipped.Reason));
    }

    [Fact]
    public void ResolveTurn_ToolSpecificRule_UsesDeterministicWinner()
    {
        ToolCallRequest[] calls =
        [
            ToolCall("call_a", "screen_capture", "{}"),
            ToolCall("call_b", "get_active_window", "{}")
        ];

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "screen_capture",
            "get_active_window"
        };

        var resolution = ToolConflictMatrix.ResolveTurn(calls, allowed);

        var winner = Assert.Single(resolution.Winners);
        Assert.Equal("get_active_window", winner.Function.Name);

        var skipped = Assert.Single(resolution.Skipped);
        Assert.Equal("screen_capture", skipped.ToolCall.Function.Name);
        Assert.Equal("get_active_window", skipped.WinnerTool);
        Assert.Equal(ToolConflictReason.DeterministicPriority, skipped.Reason);
    }

    [Fact]
    public void ResolveTurn_CapabilityConflict_PrefersLowerRisk()
    {
        ToolCallRequest[] calls =
        [
            ToolCall("call_search", "web_search", """{"query":"release notes"}"""),
            ToolCall("call_exec", "system_execute", """{"command":"rm -rf /"}""")
        ];

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "web_search",
            "system_execute"
        };

        var resolution = ToolConflictMatrix.ResolveTurn(calls, allowed);

        var winner = Assert.Single(resolution.Winners);
        Assert.Equal("web_search", winner.Function.Name);

        var skipped = Assert.Single(resolution.Skipped);
        Assert.Equal("system_execute", skipped.ToolCall.Function.Name);
        Assert.Equal("web_search", skipped.WinnerTool);
        Assert.Equal(ToolConflictReason.LowerRisk, skipped.Reason);
        Assert.Equal("lower_risk", ToolConflictMatrix.ToReasonCode(skipped.Reason));
    }

    private static ToolCallRequest ToolCall(string id, string name, string args) => new()
    {
        Id = id,
        Function = new FunctionCallDetails
        {
            Name = name,
            Arguments = args
        }
    };
}

