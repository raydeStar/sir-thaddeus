using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public sealed class PolicyStateUtilityStepTests
{
    private const string StateJson = """
        {"ok":true,"panic_mode":true,"safe_mode":false,"budgets":{"enabled":true,"max_tool_calls_per_turn":7,"max_tool_calls_per_session":91,"max_web_pulls_per_turn":4,"max_file_ops_per_minute":13},"enabled_tool_groups":{"screen":"off","files":"ask","system":"ask","web":"ask","memory_read":"always","memory_write":"ask"}}
        """;

    [Theory]
    [InlineData("Is panic mode currently active? Reply only YES or NO.", "YES")]
    [InlineData("Read the live policy: is safe mode ACTIVE or INACTIVE?", "INACTIVE")]
    [InlineData("Are runtime budgets enabled right now? ENABLED or DISABLED.", "ENABLED")]
    [InlineData("What is the live maximum tool-call count per turn? Number only.", "7")]
    [InlineData("What is the current files permission?", "ask")]
    [InlineData("Return only the active permission value for screen.", "off")]
    public async Task Reads_and_projects_one_live_field(string prompt, string expected)
    {
        var mcp = new StubMcp(StateJson);
        var step = new PolicyStateUtilityStep(mcp);

        var result = await step.ExecuteAsync(Context(prompt), CancellationToken.None);

        var terminate = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal(expected, terminate.Response.Text);
        Assert.Equal(0, terminate.Response.LlmRoundTrips);
        var call = Assert.Single(terminate.Response.ToolCallsMade);
        Assert.Equal("policy.get_state", call.ToolName);
        Assert.True(call.Success);
        Assert.Equal(1, mcp.CallCount);
    }

    [Theory]
    [InlineData("Explain what safe mode means conceptually.")]
    [InlineData("Hypothetically, if panic mode were active, do not inspect it.")]
    [InlineData("Check the files permission tomorrow, not now.")]
    [InlineData("Disable runtime budgets.")]
    [InlineData("Give both the current safe mode and panic mode.")]
    [InlineData("Tell me the current safe mode and then calculate 2 plus 2.")]
    public async Task Leaves_non_current_or_non_single_field_requests_alone(string prompt)
    {
        var mcp = new StubMcp(StateJson);
        var result = await new PolicyStateUtilityStep(mcp).ExecuteAsync(Context(prompt), CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(0, mcp.CallCount);
    }

    [Fact]
    public async Task Fails_closed_when_typed_state_is_invalid()
    {
        var mcp = new StubMcp("{\"ok\":false,\"error\":\"unavailable\"}");
        var result = await new PolicyStateUtilityStep(mcp).ExecuteAsync(
            Context("What is the current files permission?"), CancellationToken.None);

        var terminate = Assert.IsType<StepResult.Terminate>(result);
        Assert.False(terminate.Response.Success);
        Assert.Contains("couldn't read", terminate.Response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(Assert.Single(terminate.Response.ToolCallsMade).Success);
    }

    private static TurnContext Context(string prompt) => new()
    {
        ThreadId = "thread",
        MessageId = "message",
        UserText = prompt,
        ToolDefs =
        [
            new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = "policy.get_state",
                    Description = "read state",
                    Parameters = new { },
                },
            },
        ],
    };

    private sealed class StubMcp(string result) : IMcpToolClient
    {
        public int CallCount { get; private set; }
        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }
}
