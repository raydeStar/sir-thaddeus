using SirThaddeus.Agent;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Tests;

public sealed class UtilityIntentHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_ChatOnly_DoesNotInvokeLlmUtilityInference()
    {
        var handler = new UtilityIntentHandler();
        var inferCalled = false;

        var response = await handler.TryHandleAsync(new UtilityIntentExecutionRequest
        {
            UserMessage = "tell me about your favorite thing to help people with",
            Route = new RouterOutput { Intent = Intents.ChatOnly },
            TryInferWithLlmAsync = (_, _) =>
            {
                inferCalled = true;
                return Task.FromResult<UtilityRouter.UtilityResult?>(new UtilityRouter.UtilityResult
                {
                    Category = "fact",
                    Answer = "inferred"
                });
            }
        });

        Assert.False(inferCalled);
        Assert.Null(response);
    }

    [Fact]
    public async Task TryHandleAsync_GeneralTool_InvokesLlmUtilityInference()
    {
        var handler = new UtilityIntentHandler();
        var inferCalled = false;

        var response = await handler.TryHandleAsync(new UtilityIntentExecutionRequest
        {
            UserMessage = "some ambiguous utility phrasing",
            Route = new RouterOutput { Intent = Intents.GeneralTool },
            TryInferWithLlmAsync = (_, _) =>
            {
                inferCalled = true;
                return Task.FromResult<UtilityRouter.UtilityResult?>(new UtilityRouter.UtilityResult
                {
                    Category = "fact",
                    Answer = "inferred"
                });
            }
        });

        Assert.True(inferCalled);
        Assert.NotNull(response);
        Assert.Equal("inferred", response!.Text);
    }

    [Fact]
    public async Task TryHandleAsync_MetaHealth_ReturnsDeterministicToolPingSummary()
    {
        var handler = new UtilityIntentHandler();
        var toolCalls = new List<ToolCallRecord>();

        var response = await handler.TryHandleAsync(new UtilityIntentExecutionRequest
        {
            UserMessage = "Run tool_ping and confirm whether the MCP server is responding.",
            Route = new RouterOutput { Intent = Intents.GeneralTool },
            ToolCallsMade = toolCalls,
            ExecuteGenericToolCallAsync = (utilityResult, calls, _) =>
            {
                calls.Add(new ToolCallRecord
                {
                    ToolName = utilityResult.McpToolName!,
                    Arguments = utilityResult.McpToolArgs!,
                    Result = "{\"status\":\"ok\",\"tool_count\":45}",
                    Success = true
                });
                return Task.CompletedTask;
            }
        });

        Assert.NotNull(response);
        Assert.Contains("responding", response!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("healthy", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("45 tools", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(toolCalls);
        Assert.Equal("tool_ping", toolCalls[0].ToolName);
    }
}
