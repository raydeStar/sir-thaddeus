using SirThaddeus.DirectEval;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Harness;

public sealed class DirectToolLoopTests
{
    [Fact]
    public async Task Executes_only_allowlisted_tools_then_returns_model_synthesis()
    {
        var turn = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            Assert.Single(tools!);
            Assert.Equal("file_read", tools![0].Function.Name);
            if (turn++ == 0)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call-1",
                            Function = new FunctionCallDetails
                            {
                                Name = "file_read",
                                Arguments = "{\"path\":\"note.txt\"}"
                            }
                        }
                    ],
                    Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 3 }
                };
            }

            Assert.Contains(messages, message =>
                message.Role == "tool" && message.Content == "fabricated evidence");
            return new LlmResponse
            {
                IsComplete = true,
                Content = "fabricated answer",
                Usage = new TokenUsage { PromptTokens = 14, CompletionTokens = 2 }
            };
        });
        var mcp = new FakeMcpClient("fabricated evidence");
        var tools = new[]
        {
            Tool("file_read"),
            Tool("wiki_root_create")
        };

        var result = await new DirectToolLoop(llm, mcp).ExecuteAsync(
            [ChatMessage.System("same prompt"), ChatMessage.User("read it")],
            tools,
            ["file_read"],
            512,
            CancellationToken.None);

        Assert.Equal("fabricated answer", result.Text);
        Assert.Equal(2, result.CallCount);
        Assert.Equal(24, result.PromptTokens);
        Assert.Equal(5, result.CompletionTokens);
        Assert.Single(result.ToolCalls);
        Assert.Single(mcp.Calls);
    }

    [Fact]
    public async Task Stops_at_predeclared_round_limit_without_retry_or_repair()
    {
        var llm = new FakeLlmClient((_, _) => new LlmResponse
        {
            IsComplete = false,
            ToolCalls =
            [
                new ToolCallRequest
                {
                    Id = "loop",
                    Function = new FunctionCallDetails
                    {
                        Name = "file_read",
                        Arguments = "{}"
                    }
                }
            ]
        });
        var mcp = new FakeMcpClient("still looping");

        var result = await new DirectToolLoop(llm, mcp, maxRounds: 2).ExecuteAsync(
            [ChatMessage.System("same prompt"), ChatMessage.User("read it")],
            [Tool("file_read")],
            ["file_read"],
            512,
            CancellationToken.None);

        Assert.Equal("max_tool_rounds_exceeded", result.RuntimeError);
        Assert.Equal(2, result.ToolCalls.Count);
    }

    [Fact]
    public async Task Marks_structured_tool_failures_as_failed_execution()
    {
        var turn = 0;
        var llm = new FakeLlmClient((_, _) => turn++ == 0
            ? new LlmResponse
            {
                IsComplete = false,
                ToolCalls =
                [
                    new ToolCallRequest
                    {
                        Id = "failed",
                        Function = new FunctionCallDetails
                        {
                            Name = "file_read",
                            Arguments = "{}"
                        }
                    }
                ]
            }
            : new LlmResponse { IsComplete = true, Content = "could not read" });

        var result = await new DirectToolLoop(
            llm, new FakeMcpClient("{\"ok\":false,\"error\":\"missing\"}"))
            .ExecuteAsync(
                [ChatMessage.System("same prompt"), ChatMessage.User("read")],
                [Tool("file_read")],
                ["file_read"],
                128,
                CancellationToken.None);

        Assert.Equal("tool_returned_failure", result.ToolCalls[0].Error);
    }

    private static ToolDefinition Tool(string name) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = "test tool",
            Parameters = new { type = "object" }
        }
    };
}
