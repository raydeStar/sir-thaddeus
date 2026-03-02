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

        var request = new ToolLoopExecutionRequest
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
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "FileTask" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

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
        var turnCount = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            turnCount++;
            if (turnCount == 1)
            {
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
            else if (turnCount == 2)
            {
                // Respond to the PlanValidator's rejection by just calling the valid tool
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_ping_2",
                            Function = new FunctionCallDetails
                            {
                                Name = "tool_ping",
                                Arguments = "{}"
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
        var request = new ToolLoopExecutionRequest
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
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "SystemExecute" },
            SanitizeAssistantText = static s => s
        };
        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("tool_ping", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));

        // The PlanValidator should have intercepted the web_search tool and injected a System Error into the history.
        var systemErrorInHistory = request.History.Any(h => h.Content != null && h.Content.Contains("System Error", StringComparison.OrdinalIgnoreCase) && h.Content.Contains("web_search", StringComparison.OrdinalIgnoreCase));
        Assert.True(systemErrorInHistory, "Expected PlanValidator to inject a System Error for the unpermitted web_search tool.");
    }

    [Fact]
    public async Task ExecuteAsync_ExistenceGuard_RewritesSpeculativeSeasonEpisodeAnswer()
    {
        var turn = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            turn++;
            if (turn == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web_1",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"\"Unknown\" Plot summary not available","maxResults":3,"recency":"any"}"""
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Episode 1 of Season 3 would likely introduce new threats and probably focus on character growth.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (!tool.Equals("web_search", StringComparison.OrdinalIgnoreCase))
                return "{}";

            if (args.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return "1. Irrelevant\n<!-- SOURCES_JSON -->\n[{\"url\":\"https://example.com/unknown\",\"title\":\"Unknown item\",\"domain\":\"example.com\",\"excerpt\":\"No season data\"}]";
            }

            return "1. Stargate Universe cancelled after two seasons\n<!-- SOURCES_JSON -->\n[{\"url\":\"https://en.wikipedia.org/wiki/Stargate_Universe\",\"title\":\"Stargate Universe cancelled after two seasons\",\"domain\":\"wikipedia.org\",\"excerpt\":\"The series ended after season 2 and was never renewed for season 3.\"}]";
        }, FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("What would be the plot of Episode 1 of Season 3 of Stargate Universe about?")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 6,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "LookupFact" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Contains("does not exist", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("canceled", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("season 3", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.ToolCallsMade.Count >= 2);
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

