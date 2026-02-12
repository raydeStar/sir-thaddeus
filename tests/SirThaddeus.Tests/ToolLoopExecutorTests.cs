using SirThaddeus.Agent;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class ToolLoopExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ResolvesConflictsBeforeExecutingMcpCalls()
    {
        var requestedConflictingTools = false;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            if (!requestedConflictingTools && tools is { Count: > 0 })
            {
                requestedConflictingTools = true;
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_capture",
                            Function = new FunctionCallDetails
                            {
                                Name = "screen_capture",
                                Arguments = "{}"
                            }
                        },
                        new ToolCallRequest
                        {
                            Id = "call_window",
                            Function = new FunctionCallDetails
                            {
                                Name = "get_active_window",
                                Arguments = "{}"
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Window inspected.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "screen_capture" => "must not execute",
                "get_active_window" => """{"title":"IDE"}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var history = new List<ChatMessage>
        {
            ChatMessage.System("test"),
            ChatMessage.User("check my screen")
        };
        var records = new List<ToolCallRecord>();

        var response = await executor.ExecuteAsync(new ToolLoopExecutionRequest
        {
            History = history,
            Tools =
            [
                MakeToolDefinition("screen_capture"),
                MakeToolDefinition("get_active_window")
            ],
            ToolCallsMade = records,
            InitialRoundTrips = 0,
            MaxRoundTrips = 10,
            SanitizeAssistantText = static s => s
        });

        Assert.True(response.Success);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("get_active_window", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("screen_capture", StringComparison.OrdinalIgnoreCase));

        var skipped = response.ToolCallsMade.FirstOrDefault(t =>
            t.ToolName.Equals("screen_capture", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(skipped);
        Assert.False(skipped!.Success);
        Assert.Contains("tool_conflict_skipped", skipped.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotExpandToolAvailabilityBeyondFilteredSet()
    {
        var firstTurn = true;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            if (firstTurn)
            {
                firstTurn = false;
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_ping",
                            Function = new FunctionCallDetails
                            {
                                Name = "tool_ping",
                                Arguments = "{}"
                            }
                        },
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"x"}"""
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "done",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "tool_ping" => """{"ok":true}""",
                "web_search" => "must not execute",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var response = await executor.ExecuteAsync(new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("ping and maybe search")
            ],
            Tools = [MakeToolDefinition("tool_ping")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 10,
            SanitizeAssistantText = static s => s
        });

        Assert.True(response.Success);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("tool_ping", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));

        var blocked = response.ToolCallsMade.FirstOrDefault(t =>
            t.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(blocked);
        Assert.False(blocked!.Success);
        Assert.Contains("tool_not_permitted", blocked.Result, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolDefinition MakeToolDefinition(string name)
    {
        return new ToolDefinition
        {
            Function = new FunctionDefinition
            {
                Name = name,
                Description = name,
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = Array.Empty<string>()
                }
            }
        };
    }
}

