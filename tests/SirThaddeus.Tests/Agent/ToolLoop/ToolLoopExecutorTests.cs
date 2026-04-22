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

    // ExistenceGuard test removed — feature was intentionally removed for latency reasons.

    [Fact]
    public async Task ExecuteAsync_WhenWeatherToolDenied_ReturnsDeterministicWeatherPermissionMessage()
    {
        var llm = new FakeLlmClient((_, _) => new LlmResponse
        {
            IsComplete = false,
            FinishReason = "tool_calls",
            ToolCalls =
            [
                new ToolCallRequest
                {
                    Id = "call_weather",
                    Function = new FunctionCallDetails
                    {
                        Name = "weather_forecast",
                        Arguments = """{"latitude":47.6,"longitude":-122.3}"""
                    }
                }
            ]
        });

        var mcp = new FakeMcpClient(
            (_, _) => """{"error":"tool call blocked: denied by user"}""",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("what's the weather")
            ],
            Tools = [MakeToolDefinition("weather_forecast")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal("I don't have permission to look up the weather right now.", response.Text);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("weather_forecast", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLatestUserMessageIsRewrittenQuery_UsesEarlierExplicitTimeoutPromptForNoResultsFallback()
    {
        var llm = new FakeLlmClient((_, _) => new LlmResponse
        {
            IsComplete = false,
            FinishReason = "tool_calls",
            ToolCalls =
            [
                new ToolCallRequest
                {
                    Id = "call_web",
                    Function = new FunctionCallDetails
                    {
                        Name = "web_search",
                        Arguments = """{"query":"latest AI policy news"}"""
                    }
                }
            ]
        });

        var mcp = new FakeMcpClient(
            (_, _) => "[search: 0 result(s) returned]",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("Use web_search for AI policy news and handle timeout gracefully."),
                ChatMessage.User("latest AI policy news")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "GeneralTool", Confidence = 1.0 },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal(ExplicitWebNoResultsContractNormalizer.TimeoutMessage, response.Text);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWebToolDenied_RunsBestEffortFallbackWithoutTools()
    {
        var llmCallCount = 0;
        IReadOnlyList<ToolDefinition>? fallbackTools = null;
        IReadOnlyList<ChatMessage>? fallbackMessages = null;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCallCount++;
            if (llmCallCount == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"nvidia latest","recency":"day"}"""
                            }
                        }
                    ]
                };
            }

            fallbackTools = tools;
            fallbackMessages = messages;
            return new LlmResponse
            {
                IsComplete = true,
                Content = "Nvidia designs GPUs and AI hardware.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (_, _) => """{"error":"tool call blocked: denied by user"}""",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("what's new with nvidia?")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal("Nvidia designs GPUs and AI hardware.", response.Text);
        Assert.Null(fallbackTools);
        Assert.NotNull(fallbackMessages);
        Assert.Contains(
            fallbackMessages!,
            m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                 m.Content?.Contains("Do not mention permissions", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWebToolDeniedAndFallbackFails_ReturnsRealTimeFallbackMessage()
    {
        var llmCallCount = 0;
        var llm = new FakeLlmClient((_, _) =>
        {
            llmCallCount++;
            if (llmCallCount == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"current stock market","recency":"day"}"""
                            }
                        }
                    ]
                };
            }

            throw new HttpRequestException("fallback failed");
        });

        var mcp = new FakeMcpClient(
            (_, _) => """{"error":"tool call blocked: denied by user"}""",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("what happened in markets today")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal("I do not know about real-time events right now.", response.Text);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWebToolTransportDrops_UsesBestEffortOfflineFallback()
    {
        var llmCallCount = 0;
        IReadOnlyList<ToolDefinition>? fallbackTools = null;
        IReadOnlyList<ChatMessage>? fallbackMessages = null;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCallCount++;
            if (llmCallCount == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"latest weather","recency":"day"}"""
                            }
                        }
                    ]
                };
            }

            fallbackTools = tools;
            fallbackMessages = messages;
            return new LlmResponse
            {
                IsComplete = true,
                Content = "Based on built-in knowledge, weather patterns vary by region.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (_, _) => "Error: Tool execution failed — The pipe is being closed.",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("what is the weather right now?")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal("Based on built-in knowledge, weather patterns vary by region.", response.Text);
        Assert.Null(fallbackTools);
        Assert.NotNull(fallbackMessages);
        Assert.Contains(
            fallbackMessages!,
            m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                 m.Content?.Contains("tool-backed lookup is offline", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitToolInvocationFallsBackAfterUnavailable_KeepsUnavailableKeyword()
    {
        var llmCallCount = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCallCount++;
            if (llmCallCount == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"Rust language release notes","recency":"month"}"""
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Rust release notes are usually published on the official Rust blog.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (_, _) => """{"error":{"code":"tool_unavailable","message":"web_search unavailable"}}""",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("Use web_search to find the latest Rust language release notes.")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Contains("unavailable", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rust", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitWebSearchReturnsOnlyNoResults_UsesDeterministicUnavailableFallback()
    {
        var llmCallCount = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCallCount++;
            if (llmCallCount == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"latest Rust release notes","recency":"any"}"""
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "This should not run.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (_, _) => "[search: 0 result(s) returned]",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("Use web_search to find the latest Rust language release notes.")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal(1, llmCallCount);
        Assert.Contains("unavailable", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "The requested tool is unavailable for this request. Please retry in a moment.",
            response.Text);
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitTimeoutPromptReturnsOnlyNoResults_UsesDeterministicTimeoutFallback()
    {
        var llmCallCount = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCallCount++;
            if (llmCallCount == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"AI policy news","recency":"day"}"""
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "This should not run.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (_, _) => "[search: 0 result(s) returned]",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("Use web_search for AI policy news and handle timeout gracefully.")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal(1, llmCallCount);
        Assert.Equal(
            "I hit a timeout while running web tools, so I couldn't complete that request right now. Please retry in a moment or narrow the query.",
            response.Text);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSuccessfulWebResultsAreFollowedByUnavailableDraft_RetriesSynthesisWithoutTools()
    {
        var llmCallCount = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCallCount++;
            if (llmCallCount == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"microservices vs monolith"}"""
                            }
                        }
                    ]
                };
            }

            if (llmCallCount == 2)
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = ExplicitWebNoResultsContractNormalizer.UnavailableMessage,
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "For a 5-developer startup, a monolith is usually the better starting point because deployment complexity and debugging stay simpler, while microservices pay off later when independent scalability matters.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (_, _) => "[search: 3 result(s) returned]",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("Compare microservices vs monolithic architecture for a 5-developer startup.")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal(3, llmCallCount);
        Assert.DoesNotContain("unavailable", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("monolith", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("startup", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSuccessfulWebResultsAreFollowedBySignedUnavailableDraft_RetriesSynthesisWithoutTools()
    {
        var llmCallCount = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCallCount++;
            return llmCallCount switch
            {
                1 => new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = "{\"query\":\"recent .NET Aspire updates\"}"
                            }
                        }
                    ]
                },
                2 => new LlmResponse
                {
                    IsComplete = true,
                    Content = "The requested tool is unavailable for this request. Please retry in a moment.\n\n-- Sir Thaddeus"
                },
                3 => new LlmResponse
                {
                    IsComplete = true,
                    Content = "Overview: .NET Aspire keeps improving the app-model and dashboard story. Differences: some sources emphasize orchestration and deployment while others focus on developer experience. Practical takeaway: for a startup team, adopt Aspire when you want opinionated local orchestration without jumping straight to a full platform rewrite."
                },
                _ => throw new InvalidOperationException("Unexpected extra LLM call")
            };
        });

        var mcp = new FakeMcpClient(
            (_, _) => "[search: 3 result(s) returned]",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("Search for recent updates and developments in .NET Aspire from the last year. Synthesize information from multiple sources, compare what overlaps and what differs. Provide a structured response with: Overview, Common Points, Differences, Practical Takeaway.")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 4,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal(3, llmCallCount);
        Assert.DoesNotContain("unavailable for this request", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overview", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("differences", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("practical takeaway", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSuccessfulWebResultsAreFollowedByTimeoutDraft_RetriesSynthesisWithoutTools()
    {
        var llmCallCount = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCallCount++;
            if (llmCallCount == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"microservices vs monolith"}"""
                            }
                        }
                    ]
                };
            }

            if (llmCallCount == 2)
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = ExplicitWebNoResultsContractNormalizer.TimeoutMessage,
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "For a small startup, start with a monolith unless you already have strong operational need for independent service scaling, because deployment and debugging remain much simpler.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (_, _) => "[search: 3 result(s) returned]",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("Compare microservices vs monolithic architecture for a 5-developer startup.")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal(3, llmCallCount);
        Assert.DoesNotContain("timeout", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("monolith", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("startup", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitWebToolTimesOutWithPlainTextError_ReturnsDeterministicTimeoutMessage()
    {
        var llmCallCount = 0;
        var llm = new FakeLlmClient((_, _) =>
        {
            llmCallCount++;
            if (llmCallCount == 1)
            {
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_web",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"AI policy news","recency":"day"}"""
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "should not be used",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (_, _) => "Error: web_search timed out.",
            FakeMcpClient.StandardToolSet);

        var executor = new ToolLoopExecutor(llm, mcp);
        var request = new ToolLoopExecutionRequest
        {
            History =
            [
                ChatMessage.System("test"),
                ChatMessage.User("Use web_search for AI policy news and handle timeout gracefully.")
            ],
            Tools = [MakeToolDefinition("web_search")],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            MaxRoundTrips = 5,
            Decision = new SirThaddeus.Agent.Orchestration.IntentDecisionV2 { Intent = "WebLookup" },
            SanitizeAssistantText = static s => s
        };

        var response = await executor.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Equal(
            "I hit a timeout while running web tools, so I couldn't complete that request right now. Please retry in a moment or narrow the query.",
            response.Text);
        Assert.Equal(1, llmCallCount);
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

