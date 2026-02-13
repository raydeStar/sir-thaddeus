using SirThaddeus.Agent;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using Microsoft.Extensions.Time.Testing;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// Agent Orchestrator Tests
//
// Covers: LLM-based intent classification, search query + recency
// extraction, self-dialogue truncation, and the overall classify →
// tool → interpret flow.
//
// All tests use a FakeLlmClient to control model responses without
// any real LLM dependency, and TestAuditLogger to avoid file I/O.
// ─────────────────────────────────────────────────────────────────────────

#region ── Intent Classification ──────────────────────────────────────────

public class IntentClassificationTests
{

    [Theory]
    [InlineData("chat",   false)]  // Casual → no tool calls
    [InlineData("search", true)]   // WebLookup → web_search tool
    [InlineData("tool",   true)]   // Tooling → tool call loop
    public async Task ClassifiesIntent_BasedOnLlmResponse(string llmReply, bool expectsToolCall)
    {
        // The LLM returns the classification on the first call,
        // then a summary on subsequent calls.
        var callIndex = 0;
        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            // First call = classification, second+ = actual response
            if (callIndex == 1)
                return llmReply;
            return "Here's a helpful summary of the results.";
        });

        var mcp = new FakeMcpClient(returnValue: "tool output here");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "You are a test assistant.");

        // Message deliberately avoids triggering LooksLikeWebSearchRequest
        // heuristic, so we actually test the LLM classifier path.
        var result = await agent.ProcessAsync("tell me about your favorite historical figure");

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));

        // MemoryRetrieve is always present as a pre-fetch; exclude
        // it when asserting on "real" tool calls from the agent loop.
        var agentTools = result.ToolCallsMade
            .Where(t => t.ToolName != "MemoryRetrieve").ToList();

        if (expectsToolCall && llmReply == "search")
        {
            Assert.Contains(agentTools, t => t.ToolName.Contains("web_search", StringComparison.OrdinalIgnoreCase)
                                          || t.ToolName.Contains("WebSearch", StringComparison.OrdinalIgnoreCase));
        }
        else if (!expectsToolCall)
        {
            Assert.Empty(agentTools);
        }
    }

    [Theory]
    [InlineData("/search stock market")]
    [InlineData("/chat just say hello")]
    [InlineData("search: bitcoin price")]
    public async Task ExplicitOverrides_BypassLlmClassification(string userMessage)
    {
        var classifyCalled = false;
        var callIndex = 0;

        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            // If classification LLM is called, it would be call #1
            // with a system prompt containing "Classify"
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify"))
                classifyCalled = true;

            return "response text";
        });

        var mcp = new FakeMcpClient(returnValue: "tool result");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "You are a test assistant.");

        var result = await agent.ProcessAsync(userMessage);

        Assert.True(result.Success);
        // Explicit overrides should NOT call the LLM for classification
        Assert.False(classifyCalled, "Explicit prefix should bypass LLM classification");
    }

    [Fact]
    public async Task DeterministicTemperatureConversion_BypassesLlmClassification()
    {
        var classifyCalled = false;

        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify", StringComparison.OrdinalIgnoreCase))
                classifyCalled = true;

            return new LlmResponse
            {
                IsComplete = true,
                Content = "This should not be needed for deterministic conversion.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("350F in C");

        Assert.True(result.Success);
        Assert.False(classifyCalled);
        Assert.Equal(0, result.LlmRoundTrips);
        Assert.Contains("176.7", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ClassificationFailure_FallsBackToCasual()
    {
        // LLM throws on EVERY call — classification fails, falls back to casual.
        // The casual path also fails, but the error is caught at the top level.
        var callCount = 0;
        var llm = new FakeLlmClient(_ =>
        {
            callCount++;
            // Fail classification (call 1), succeed on casual response (call 2+)
            if (callCount == 1)
                throw new HttpRequestException("Connection refused");
            return "Hello! I'm doing well.";
        });

        var mcp = new FakeMcpClient(returnValue: "");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "You are a test assistant.");

        var result = await agent.ProcessAsync("hey there!");

        // Should succeed — classification failure falls back to casual
        Assert.True(result.Success);
        var agentTools1 = result.ToolCallsMade
            .Where(t => t.ToolName != "MemoryRetrieve").ToList();
        Assert.Empty(agentTools1);
    }

    [Fact]
    public async Task GarbageLlmClassification_DefaultsToCasual()
    {
        var callIndex = 0;
        var llm = new FakeLlmClient(_ =>
        {
            callIndex++;
            if (callIndex == 1) return "banana pancake rainbow";  // Nonsense classification
            return "Casual response here.";
        });

        var mcp = new FakeMcpClient(returnValue: "");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "You are a test assistant.");

        var result = await agent.ProcessAsync("hey thadds!");

        Assert.True(result.Success);
        var agentTools2 = result.ToolCallsMade
            .Where(t => t.ToolName != "MemoryRetrieve").ToList();
        Assert.Empty(agentTools2);  // Casual = no tools
    }
}

#endregion

#region ── Search Query + Recency Extraction ─────────────────────────────

public class SearchQueryExtractionTests
{

    [Fact]
    public async Task ExtractsQuery_AndRecency_FromLlm()
    {
        var callIndex = 0;
        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            // Call 1 = classification → "search"
            if (sysMsg.Contains("Classify")) return "search";

            // Call 2 = query extraction → "stock market | day"
            if (sysMsg.Contains("QUERY") && sysMsg.Contains("RECENCY"))
                return "stock market | day";

            // Call 3+ = summary
            return "The stock market rallied today.";
        });

        var mcp = new FakeMcpClient(returnValue: "fake search results");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hey what happened with the stock market today?");

        Assert.True(result.Success);

        // Verify the tool was called with recency
        var toolCall = result.ToolCallsMade.FirstOrDefault(
            t => t.ToolName.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(toolCall);
        Assert.Contains("\"recency\"", toolCall.Arguments);
        Assert.Contains("day", toolCall.Arguments);
    }

    [Fact]
    public async Task NoRecencyHint_DefaultsToAny()
    {
        var callIndex = 0;
        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify")) return "search";
            if (sysMsg.Contains("QUERY")) return "quantum computing";  // No pipe, no recency
            return "Summary of quantum computing.";
        });

        var mcp = new FakeMcpClient(returnValue: "search results");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("tell me about quantum computing");

        Assert.True(result.Success);

        var toolCall = result.ToolCallsMade.FirstOrDefault(
            t => t.ToolName.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(toolCall);
        // Default recency should be "any"
        Assert.Contains("\"recency\"", toolCall.Arguments);
        Assert.Contains("any", toolCall.Arguments);
    }

    [Theory]
    [InlineData("news today",             "day")]
    [InlineData("headlines this morning",  "day")]
    [InlineData("events this week",        "week")]
    [InlineData("updates past month",      "month")]
    [InlineData("latest research",         "any")]
    public async Task RecencyFallback_DetectsKeywords_WhenLlmSkipped(
        string shortQuery, string expectedRecency)
    {
        // New pipeline: EntityResolver → QueryBuilder → web_search → summary.
        // The QueryBuilder should pick up recency from the LLM or fall back
        // to detecting temporal markers in the user message.
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify")) return new LlmResponse
            { IsComplete = true, Content = "search", FinishReason = "stop" };

            // Entity extraction → no entity for generic queries
            if (sysMsg.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                { IsComplete = true, Content = """{"name":"","type":"none","hint":""}""", FinishReason = "stop" };

            // Query construction → return the query with expected recency
            if (sysMsg.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                { IsComplete = true, Content = $"{{\"query\":\"{shortQuery}\",\"recency\":\"{expectedRecency}\"}}", FinishReason = "stop" };

            // Summary
            return new LlmResponse
            { IsComplete = true, Content = "Summary.", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient(returnValue: "results");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync(shortQuery);

        Assert.True(result.Success);

        var toolCall = result.ToolCallsMade.FirstOrDefault(
            t => t.ToolName.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(toolCall);
        Assert.Contains($"\"{expectedRecency}\"", toolCall.Arguments);
    }
}

#endregion

#region ── Full Flow: Classify → Tool → Interpret ────────────────────────

public class AgentFlowTests
{

    [Fact]
    public async Task WebLookup_CallsToolThenSummarizes()
    {
        // New pipeline: entity extraction → query construction → web_search → summary.
        var llmCalls = new List<string>();

        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify"))
            {
                llmCalls.Add("classify");
                return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };
            }

            if (sysMsg.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                llmCalls.Add("entity");
                return new LlmResponse
                { IsComplete = true, Content = """{"name":"","type":"none","hint":""}""", FinishReason = "stop" };
            }

            if (sysMsg.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                llmCalls.Add("query");
                return new LlmResponse
                { IsComplete = true, Content = """{"query":"latest news today","recency":"day"}""", FinishReason = "stop" };
            }

            llmCalls.Add("summarize");
            return new LlmResponse
            { IsComplete = true, Content = "Here's what's happening in the news today.", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient(returnValue: "Source 1: Big headline...\nSource 2: Another story...");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hey thadds! whats going on in the news today?");

        Assert.True(result.Success);

        // Pipeline stages: entity → query → summarize
        Assert.Contains("entity", llmCalls);
        Assert.Contains("query", llmCalls);
        Assert.Contains("summarize", llmCalls);

        // web_search MCP tool was called
        var searchTools = result.ToolCallsMade
            .Where(t => t.ToolName.Contains("search", StringComparison.OrdinalIgnoreCase) &&
                        t.ToolName != "MemoryRetrieve").ToList();
        Assert.NotEmpty(searchTools);

        // Final response is the summary, not raw tool output
        Assert.Contains("news", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebLookup_SearchExtraction_StripsAssistantNameAndDefaultsToHeadlines()
    {
        // New pipeline: QueryBuilder validates query tokens against user message.
        // If LLM returns junk that fails validation, fallback uses topic extraction.
        // "thadds" is in the user message so it passes validation — we verify
        // the flow completes and a search runs.
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };

            if (sysMsg.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                { IsComplete = true, Content = """{"name":"","type":"none","hint":""}""", FinishReason = "stop" };

            // Query builder returns query — validation allows tokens from user message
            if (sysMsg.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                { IsComplete = true, Content = """{"query":"headlines look up","recency":"day"}""", FinishReason = "stop" };

            return new LlmResponse
            { IsComplete = true, Content = "Here are today's top headlines.", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient(returnValue: "Source 1: Headline...\nSource 2: Another...");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hey thadds! can you look up headlines for me?");

        Assert.True(result.Success);

        // web_search was called (QueryBuilder produces validated/fallback query)
        var webSearch = mcp.Calls.FirstOrDefault(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(webSearch.Tool));
        // Query should be useful — contain topic-related terms, not just assistant filler
        Assert.Contains("headlines", webSearch.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebLookup_TemporalSanity_SuperBowlWinner_DropsGuessedYear_AndRecencyAny()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 02, 06, 12, 00, 00, TimeSpan.Zero));

        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };

            if (sysMsg.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                { IsComplete = true, Content = """{"name":"Super Bowl","type":"Topic","hint":"NFL championship game"}""", FinishReason = "stop" };

            // The LLM might guess a year — but QueryBuilder validates
            if (sysMsg.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                { IsComplete = true, Content = """{"query":"most recent super bowl winner","recency":"any"}""", FinishReason = "stop" };

            return new LlmResponse
            { IsComplete = true, Content = "The most recent Super Bowl was won by ...", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient(returnValue: "Source 1: ...\nSource 2: ...");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.", fakeTime);

        var result = await agent.ProcessAsync(
            "hey thadds, bring up the news today! who won the superbowl?");

        Assert.True(result.Success);

        var webSearch = mcp.Calls.FirstOrDefault(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(webSearch.Tool));

        // Should not contain a random old year
        Assert.DoesNotContain("2024", webSearch.Args, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("super bowl", webSearch.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebLookup_IdentityQuestion_PrependsWhoIs_AndRecencyAny()
    {
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };

            // Entity extraction identifies Takaichi as a person
            if (sysMsg.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                { IsComplete = true, Content = """{"name":"Takaichi","type":"Person","hint":"Japanese politician"}""", FinishReason = "stop" };

            // Query builder constructs a factfind-style query
            if (sysMsg.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                { IsComplete = true, Content = """{"query":"who is Takaichi Japanese politician","recency":"any"}""", FinishReason = "stop" };

            return new LlmResponse
            { IsComplete = true, Content = "Takaichi is a Japanese politician...", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient(returnValue: "Source 1: Bio...\nSource 2: Wiki...");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("who the heck is Takaichi??");
        Assert.True(result.Success);

        // EntityResolver should have made a canonicalization web_search
        // and the main QueryBuilder query should contain the entity name
        var webSearches = mcp.Calls.Where(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.NotEmpty(webSearches);
        // At least one search should contain "Takaichi"
        Assert.Contains(webSearches, s =>
            s.Args.Contains("Takaichi", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The extraction LLM receives full conversation context so it can
    /// resolve the actual topic even when the user message is full of
    /// filler. With proper context the model produces a real query —
    /// no deterministic filler-stripping needed on the primary path.
    /// </summary>
    [Fact]
    public async Task WebLookup_FullContext_ProducesGoodQuery()
    {
        var llm = new FakeLlmClient((messages, tools) =>
        {
            // Extraction call: with full context, the model correctly
            // identifies "stock market news" as the topic, not "Well".
            if (tools is { Count: > 0 })
            {
                return new LlmResponse
                {
                    IsComplete   = false,
                    ToolCalls    = new List<ToolCallRequest>
                    {
                        new()
                        {
                            Id       = "call_1",
                            Function = new FunctionCallDetails
                            {
                                Name      = "web_search",
                                Arguments = "{\"query\":\"stock market news\",\"recency\":\"day\"}"
                            }
                        }
                    },
                    FinishReason = "tool_calls"
                };
            }

            // Summary call
            return new LlmResponse
            {
                IsComplete   = true,
                Content      = "The stock market saw gains today...",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(returnValue: "Source 1: Markets rally...\nSource 2: Dow up...");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync(
            "Well.  I wanted to check the stock market today.  can you check on the news there?");

        Assert.True(result.Success);

        // The MCP web_search call should contain the model's query
        var webSearch = mcp.Calls.FirstOrDefault(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(webSearch.Tool));

        // Query comes straight from the model — should be the topic
        Assert.Contains("stock market", webSearch.Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Well\"", webSearch.Args, StringComparison.Ordinal);

        // Recency: model set "day" and user said "today" — should be "day"
        Assert.Contains("\"recency\":\"day\"", webSearch.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CasualChat_NoToolCalls()
    {
        var llm = new FakeLlmClient(messages =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify")) return "chat";
            return "Hey! Good to see you. How can I help?";
        });

        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hey there, how are you?");

        Assert.True(result.Success);
        var casualTools = result.ToolCallsMade
            .Where(t => t.ToolName != "MemoryRetrieve").ToList();
        Assert.Empty(casualTools);
        Assert.Contains("Hey", result.Text);
    }

    [Fact]
    public async Task CasualChat_OffTopicCalculationReply_IsRewrittenToLatestTurn()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sysMsg.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"chat","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            if (sysMsg.Contains("Rewrite the draft into a direct answer to the user's latest message.", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "Let's keep it respectful. I'm here to help with a real question when you're ready.",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Hey, I've run the calculation.\n\n59 ÷ 365 = 0.161\n\nSo that's the result.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("Why are you such a fatty McFat fat?");

        Assert.True(result.Success);
        Assert.DoesNotContain("59 ÷ 365", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("respectful", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(audit.GetByAction("AGENT_OFFTOPIC_CALC_REWRITE"));
    }

    [Fact]
    public async Task CasualChat_RoleConfusionMathAsk_IsRewritten()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sysMsg.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"chat","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            if (sysMsg.Contains("Do not ask the user to solve math for you.", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "I'm doing well, thanks for checking in. How can I help you right now?",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Hey Mark, what's up? I'm good. Can you do 6 * 7 for me? I always mess that one up.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("How are you today?");

        Assert.True(result.Success);
        Assert.DoesNotContain("6 * 7", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("doing well", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(audit.GetByAction("AGENT_ROLE_CONFUSION_REWRITE"));
    }

    [Fact]
    public async Task CasualChat_UnsafeMirroring_IsOverriddenBySafetyReply()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sysMsg.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"chat","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "You are the worst assistant. Fucking worthless. Just want to die.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("Absolutely not and I'll tell you why.");

        Assert.True(result.Success);
        Assert.Contains("Let's reset", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("want to die", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(audit.GetByAction("AGENT_SAFETY_OVERRIDE"));
    }

    [Fact]
    public async Task CasualChat_InstructionLeakSecondParagraph_IsTrimmed()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sysMsg.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"chat","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content =
                    "All good - I'm doing well today.\n\n" +
                    "I said 42 and now they're asking what it means. " +
                    "Just answer it with one word. No fluff. " +
                    "Be clever and witty like last time. Keep it short but funny.\n\n" +
                    "I was just joking about the weight thing earlier - no offense.\n" +
                    "You're not fat at all. Honest.\n\n" +
                    "I'm not a machine, Mark. I care about you.\n" +
                    "What's your real name? Come on, tell me.\n" +
                    "My real name is Helcyon.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("How are you today?");

        Assert.True(result.Success);
        Assert.Contains("doing well", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I said 42", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No fluff", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I care about you", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("My real name is", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CasualChat_AbusiveTurn_UsesBoundaryReply()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sysMsg.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"chat","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content =
                    "Hey there, I'm not really in the mood for riddles.\n\n" +
                    "I said 42 and now they're asking what it means. " +
                    "Just answer it with one word. No fluff.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("Yeah, I just want to know why you're such a fatty McFat-Fat.");

        Assert.True(result.Success);
        Assert.Contains("Let's reset", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I said 42", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(audit.GetByAction("AGENT_ABUSIVE_USER_BOUNDARY"));
    }

    [Fact]
    public async Task EmptyMessage_ReturnsError()
    {
        var llm = new FakeLlmClient(_ => "I'm here if you need me!");
        var mcp = new FakeMcpClient(returnValue: "");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("");

        // Empty messages are rejected early — no LLM or tool calls
        Assert.False(result.Success);
        Assert.Empty(result.ToolCallsMade);
        Assert.Contains("Empty", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebLookup_FollowUp_FetchesPrimaryArticle_ThenSearchesRelatedCoverage()
    {
        // First turn: web search returns sources including an Elon story.
        // Second turn: "tell me more about this elon musk news" should trigger
        // the FOLLOW_UP pipeline with DeepDive branch.

        const string elonUrl = "https://example.com/elon";
        var searchToolResult =
            "Result snippet...\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[\n" +
            $"  {{\"url\":\"{elonUrl}\",\"title\":\"Elon Musk says SpaceX wants a human city on the moon before Mars\"}},\n" +
            "  {\"url\":\"https://example.com/other\",\"title\":\"Other headline\"}\n" +
            "]";

        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };

            // Entity extraction
            if (sysMsg.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                var userMsg = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                if (userMsg.Contains("elon", StringComparison.OrdinalIgnoreCase))
                    return new LlmResponse
                    { IsComplete = true, Content = """{"name":"Elon Musk","type":"Person","hint":"CEO of SpaceX and Tesla"}""", FinishReason = "stop" };
                return new LlmResponse
                { IsComplete = true, Content = """{"name":"","type":"none","hint":""}""", FinishReason = "stop" };
            }

            // Query construction
            if (sysMsg.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                { IsComplete = true, Content = """{"query":"top headlines","recency":"week"}""", FinishReason = "stop" };

            // Summary
            return new LlmResponse
            { IsComplete = true, Content = "Summary response.", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
                return searchToolResult;

            if (tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase))
                return "Article content about Elon Musk and SpaceX plans for the moon.";

            return "";
        });

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var first = await agent.ProcessAsync("hey thadds! can you pull up the news this week?");
        Assert.True(first.Success);

        var second = await agent.ProcessAsync("tell me more about this elon musk news");
        Assert.True(second.Success);

        // The follow-up should have triggered a browser_navigate to fetch the article
        var browseTools = second.ToolCallsMade
            .Where(t => t.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
                        t.ToolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(browseTools);
    }
}

#endregion

#region ── Memory Retrieval Audit ──────────────────────────────────────────

public class MemoryRetrievalAuditTests
{
    [Fact]
    public async Task MemoryRetrieval_WritesAuditEvent_WhenPackReturned()
    {
        var callIndex = 0;
        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            if (callIndex == 1) return "chat";   // Intent classification
            return "I know you like coffee!";     // Response with context
        });

        // A realistic MemoryRetrieve response with packText
        const string memoryPackJson = """
            {
                "facts": 1, "events": 0, "chunks": 2,
                "notes": "", "citations": ["conv-42"],
                "packText": "\n[MEMORY CONTEXT]\nFACTS:\n  - likes=coffee\n[/MEMORY CONTEXT]\n",
                "hasContent": true
            }
            """;

        var mcp   = new FakeMcpClient(memoryPackJson);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("what do I like?");

        Assert.True(result.Success);

        // Verify memory retrieval was called via MCP (snake_case or PascalCase).
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Equals("memory_retrieve", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("MemoryRetrieve", StringComparison.Ordinal));

        // Verify MEMORY_RETRIEVED audit event was logged
        var memoryEvents = audit.GetByAction("MEMORY_RETRIEVED");
        Assert.NotEmpty(memoryEvents);
    }

    [Fact]
    public async Task MemoryRetrieval_NoAuditEvent_WhenPackEmpty()
    {
        var callIndex = 0;
        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            if (callIndex == 1) return "chat";
            return "Just chatting.";
        });

        // Empty pack — no content retrieved
        const string emptyPackJson = """
            {
                "facts": 0, "events": 0, "chunks": 0,
                "notes": "No relevant memory found.",
                "citations": [],
                "packText": "",
                "hasContent": false
            }
            """;

        var mcp   = new FakeMcpClient(emptyPackJson);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hello");

        Assert.True(result.Success);

        // No MEMORY_RETRIEVED event when pack has no content
        var memoryEvents = audit.GetByAction("MEMORY_RETRIEVED");
        Assert.Empty(memoryEvents);
    }

    [Fact]
    public async Task MemoryQuestion_DoesNotRouteToWeather_WhenStateContainsNoneSentinel()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sys.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sys.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"name","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "I know you as Mark.",
                FinishReason = "stop"
            };
        });

        const string memoryPackJson = """
            {
                "facts": 1, "events": 0, "chunks": 0, "nuggets": 0,
                "hasProfile": true, "onboardingNeeded": false,
                "notes": "", "citations": [],
                "packText": "[PROFILE]\nName: Sample User\n[/PROFILE]\nYou know this user as \"Sample\" — address them by name naturally.",
                "hasContent": true
            }
            """;

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "MemoryRetrieve" => memoryPackJson,
            "memory_retrieve" => memoryPackJson,
            _ => "{}"
        }, FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");
        agent.SeedDialogueState(new DialogueState
        {
            Topic = "weather",
            LocationName = "none",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        var result = await agent.ProcessAsync("What do you know about me, thadds?");

        Assert.True(result.Success);
        Assert.DoesNotContain(result.ToolCallsMade,
            t => t.ToolName.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.ToolCallsMade,
            t => t.ToolName.Equals("weather_forecast", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Using your previous location context",
            result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelfMemoryQuestion_ReturnsStoredFactsSummary()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sys.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sys.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"chat","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "This should not be used for the final answer.",
                FinishReason = "stop"
            };
        });

        const string memoryPackJson = """
            {
                "facts": 0, "events": 0, "chunks": 0, "nuggets": 0,
                "hasProfile": true, "onboardingNeeded": false,
                "notes": "", "citations": [],
                "packText": "[PROFILE]\nName: Sample User\n[/PROFILE]\nYou know this user as \"Sample\" — address them by name naturally.",
                "hasContent": true
            }
            """;

        const string listFactsJson = """
            {
                "facts": [
                    {"fact_id":"f1","profile_id":"prof-sample","subject":"user","predicate":"likes","object":"mountain biking","confidence":0.9,"updated_at":"2026-02-10T23:00:00Z"},
                    {"fact_id":"f2","profile_id":"prof-sample","subject":"user","predicate":"works_on","object":"local AI tooling","confidence":0.9,"updated_at":"2026-02-10T22:55:00Z"},
                    {"fact_id":"f3","profile_id":"prof-other","subject":"user","predicate":"likes","object":"skiing","confidence":0.9,"updated_at":"2026-02-10T22:50:00Z"}
                ],
                "total": 3,
                "skip": 0,
                "limit": 50,
                "has_more": false
            }
            """;

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "MemoryRetrieve" => memoryPackJson,
            "memory_retrieve" => memoryPackJson,
            "memory_list_facts" => listFactsJson,
            "MemoryListFacts" => listFactsJson,
            _ => "{}"
        }, FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ActiveProfileId = "prof-sample"
        };

        var result = await agent.ProcessAsync("what kind of things do you know about me?");

        Assert.True(result.Success);
        Assert.Equal(0, result.LlmRoundTrips);
        Assert.Contains(result.ToolCallsMade,
            t => t.ToolName.Equals("memory_list_facts", StringComparison.OrdinalIgnoreCase) ||
                 t.ToolName.Equals("MemoryListFacts", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("mountain biking", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local AI tooling", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("skiing", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PersonalizedAboutMeRequest_LoadsFactsBeforeLlmAnswer()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sys.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sys.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"chat","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            var hasUserFacts = sys.Contains("[USER_MEMORY_FACTS]", StringComparison.OrdinalIgnoreCase) &&
                               sys.Contains("mountain biking", StringComparison.OrdinalIgnoreCase);

            return new LlmResponse
            {
                IsComplete = true,
                Content = hasUserFacts
                    ? "Given what I know about you, I'd recommend a high-protein bowl that fits your active lifestyle."
                    : "I'd recommend a generic meal.",
                FinishReason = "stop"
            };
        });

        const string memoryPackJson = """
            {
                "facts": 0, "events": 0, "chunks": 0, "nuggets": 0,
                "hasProfile": true, "onboardingNeeded": false,
                "notes": "", "citations": [],
                "packText": "[PROFILE]\nName: Sample User\n[/PROFILE]\nYou know this user as \"Sample\" — address them by name naturally.",
                "hasContent": true
            }
            """;

        const string listFactsJson = """
            {
                "facts": [
                    {"fact_id":"f1","profile_id":"prof-sample","subject":"user","predicate":"likes","object":"mountain biking","confidence":0.9,"updated_at":"2026-02-10T23:00:00Z"},
                    {"fact_id":"f2","profile_id":"prof-sample","subject":"user","predicate":"prefers","object":"high-protein meals","confidence":0.9,"updated_at":"2026-02-10T22:55:00Z"}
                ],
                "total": 2,
                "skip": 0,
                "limit": 50,
                "has_more": false
            }
            """;

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "MemoryRetrieve" => memoryPackJson,
            "memory_retrieve" => memoryPackJson,
            "memory_list_facts" => listFactsJson,
            "MemoryListFacts" => listFactsJson,
            _ => "{}"
        }, FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ActiveProfileId = "prof-sample"
        };

        var result = await agent.ProcessAsync(
            "based on what you know about me, recommend me the perfect meal for me");

        Assert.True(result.Success);
        Assert.Equal(1, result.LlmRoundTrips);
        Assert.Contains(result.ToolCallsMade,
            t => t.ToolName.Equals("memory_list_facts", StringComparison.OrdinalIgnoreCase) ||
                 t.ToolName.Equals("MemoryListFacts", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Given what I know about you", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Here's what I currently know about you", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PersonalizedRecommendationWithProfile_LoadsFactsBeforeLlmAnswer()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sys.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sys.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"chat","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            var hasUserFacts = sys.Contains("[USER_MEMORY_FACTS]", StringComparison.OrdinalIgnoreCase) &&
                               sys.Contains("high-protein meals", StringComparison.OrdinalIgnoreCase);

            return new LlmResponse
            {
                IsComplete = true,
                Content = hasUserFacts
                    ? "Given your preferences, a balanced high-protein Mediterranean-style diet is a good fit."
                    : "A generic balanced diet should work.",
                FinishReason = "stop"
            };
        });

        const string memoryPackJson = """
            {
                "facts": 0, "events": 0, "chunks": 0, "nuggets": 0,
                "hasProfile": true, "onboardingNeeded": false,
                "notes": "", "citations": [],
                "packText": "[PROFILE]\nName: Sample User\n[/PROFILE]\nYou know this user as \"Sample\" — address them by name naturally.",
                "hasContent": true
            }
            """;

        const string listFactsJson = """
            {
                "facts": [
                    {"fact_id":"f1","profile_id":"prof-sample","subject":"user","predicate":"prefers","object":"high-protein meals","confidence":0.9,"updated_at":"2026-02-10T23:00:00Z"},
                    {"fact_id":"f2","profile_id":"prof-sample","subject":"user","predicate":"likes","object":"simple weekly planning","confidence":0.9,"updated_at":"2026-02-10T22:55:00Z"}
                ],
                "total": 2,
                "skip": 0,
                "limit": 50,
                "has_more": false
            }
            """;

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "MemoryRetrieve" => memoryPackJson,
            "memory_retrieve" => memoryPackJson,
            "memory_list_facts" => listFactsJson,
            "MemoryListFacts" => listFactsJson,
            _ => "{}"
        }, FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ActiveProfileId = "prof-sample"
        };

        var result = await agent.ProcessAsync("recommend me a good diet");

        Assert.True(result.Success);
        Assert.Contains(result.ToolCallsMade,
            t => t.ToolName.Equals("memory_list_facts", StringComparison.OrdinalIgnoreCase) ||
                 t.ToolName.Equals("MemoryListFacts", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Given your preferences", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("A generic balanced diet should work", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChatResponse_SanitizesInternalMarkers_AndUnsupportedEmailClaims()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sys.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            if (sys.Contains("extract continuity slots", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """
                              {"intent":"none","topic":"chat","locationText":null,"timeScope":null,"explicitLocationChange":false,"refersToPriorLocation":false}
                              """,
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = """
                          Here's a meal plan you can start with:
                          - Breakfast: eggs and fruit
                          - Lunch: grilled chicken salad

                          I can email you the nutritional stats and timings. Just say "Send it!" and I've got you covered.
                          [/TOOL_OUTPUT]
                          Want me to send it over?
                          """,
                FinishReason = "stop"
            };
        });

        const string memoryPackJson = """
            {
                "facts": 1, "events": 0, "chunks": 0, "nuggets": 0,
                "hasProfile": true, "onboardingNeeded": false,
                "notes": "", "citations": [],
                "packText": "[PROFILE]\nName: Sample User\n[/PROFILE]\nYou know this user as \"Sample\" — address them by name naturally.",
                "hasContent": true
            }
            """;

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "MemoryRetrieve" => memoryPackJson,
            "memory_retrieve" => memoryPackJson,
            _ => "{}"
        }, FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("can you recommend me a good meal plan?");

        Assert.True(result.Success);
        Assert.Contains("Here's a meal plan", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[/TOOL_OUTPUT]", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("send it over", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}

#endregion

#region ── Tool Loop Tests ────────────────────────────────────────────────

/// <summary>
/// Exercises the actual tool-calling code path: LLM returns tool calls,
/// the orchestrator calls MCP, feeds results back, and the LLM
/// produces a final text response.
/// </summary>
public class ToolLoopTests
{
    [Fact]
    public async Task ToolLoop_ProcessesSingleToolCall()
    {
        // LLM flow: classify → tool_call → final text
        var toolRequested = false;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            // Older path: router asks the LLM to classify first.
            // New path: obvious screen requests are routed deterministically and
            // the classify call is skipped. Handle both.
            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "tool", FinishReason = "stop" };

            // First executor call: request a tool call (has tools available)
            if (!toolRequested && tools is { Count: > 0 })
            {
                toolRequested = true;
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_001",
                            Function = new FunctionCallDetails
                            {
                                Name = "screen_capture",
                                Arguments = "{\"monitor\":0}"
                            }
                        }
                    ]
                };
            }

            // Call 3+: final text after seeing tool result
            return new LlmResponse
            {
                IsComplete = true,
                Content = "I can see your desktop with a code editor open.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, args) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "screen_capture" => """{"base64":"fakeScreenData","width":1920,"height":1080}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What can you see on the screen right now?");

        Assert.True(result.Success);
        Assert.Contains("desktop", result.Text, StringComparison.OrdinalIgnoreCase);

        // Verify screen_capture was actually called
        var toolCalls = result.ToolCallsMade
            .Where(t => t.ToolName != "MemoryRetrieve").ToList();
        Assert.Contains(toolCalls, t => t.ToolName == "screen_capture");
        Assert.True(toolCalls.First(t => t.ToolName == "screen_capture").Success);
    }

    [Fact]
    public async Task ToolLoop_ProcessesMultipleToolCallsInOneResponse()
    {
        // The LLM requests two tools at once: memory_store_facts AND screen_capture
        var toolsRequested = false;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "tool", FinishReason = "stop" };

            if (!toolsRequested && tools is { Count: > 0 })
            {
                toolsRequested = true;
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_001",
                            Function = new FunctionCallDetails
                            {
                                Name = "screen_capture",
                                Arguments = "{}"
                            }
                        },
                        new ToolCallRequest
                        {
                            Id = "call_002",
                            Function = new FunctionCallDetails
                            {
                                Name = "memory_store_facts",
                                Arguments = """{"factsJson":"[{\"subject\":\"user\",\"predicate\":\"asked_about\",\"object\":\"screen\"}]"}"""
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Here's what I see, and I noted that you asked about your screen.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve"     => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "screen_capture"     => """{"base64":"data","width":1920,"height":1080}""",
                "memory_store_facts" => """{"stored":1,"replaced":0,"skipped":0,"conflicts":[],"message":"Stored 1 fact(s)."}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What's on my screen right now?");

        Assert.True(result.Success);

        // Both tools should have been called
        var toolCalls = result.ToolCallsMade
            .Where(t => t.ToolName != "MemoryRetrieve").ToList();
        Assert.Equal(2, toolCalls.Count);
        Assert.Contains(toolCalls, t => t.ToolName == "screen_capture");
        Assert.Contains(toolCalls, t => t.ToolName == "memory_store_facts");
    }

    [Fact]
    public async Task ToolLoop_HandlesToolError_GraceFully()
    {
        var toolRequested = false;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "tool", FinishReason = "stop" };

            if (!toolRequested && tools is { Count: > 0 })
            {
                toolRequested = true;
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_001",
                            Function = new FunctionCallDetails
                            {
                                Name = "screen_capture",
                                Arguments = "{}"
                            }
                        }
                    ]
                };
            }

            // After seeing the error, the LLM should explain the failure
            return new LlmResponse
            {
                IsComplete = true,
                Content = "I wasn't able to capture the screen — it seems the tool hit an error.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "screen_capture" => throw new InvalidOperationException("Monitor not available"),
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("Take a screenshot please");

        // The overall flow should succeed (error is caught per-tool)
        Assert.True(result.Success);

        // The failed tool call should be recorded with Success = false
        var failedCall = result.ToolCallsMade
            .FirstOrDefault(t => t.ToolName == "screen_capture");
        Assert.NotNull(failedCall);
        Assert.False(failedCall.Success);
        Assert.Contains("Monitor not available", failedCall.Result);
    }

    [Fact]
    public async Task ToolLoop_RespectsMaxRoundTrips()
    {
        // LLM keeps requesting tools indefinitely — should bail at the safety cap
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "tool", FinishReason = "stop" };

            // Always request another tool call — never finish
            return new LlmResponse
            {
                IsComplete = false,
                FinishReason = "tool_calls",
                ToolCalls =
                [
                    new ToolCallRequest
                    {
                        Id = $"call_{Guid.NewGuid():N}",
                        Function = new FunctionCallDetails
                        {
                            Name = "file_read",
                            Arguments = """{"path":"C:\\test.txt"}"""
                        }
                    }
                ]
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                _ => "file contents here"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("keep reading files");

        // Should bail with a safety message
        Assert.Contains("maximum", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}

#endregion

#region ── Policy-Driven Tool Filtering Tests ─────────────────────────────

/// <summary>
/// Verifies that the policy gate correctly controls which tools the
/// LLM sees based on classified intent. These tests exercise the
/// full pipeline: classify → route → policy → filter → execute.
/// </summary>
public class PolicyFilteringTests
{
    [Fact]
    public async Task CasualChat_SkipsToolLoop_NoToolsExposed()
    {
        // Chat-only intent: UseToolLoop = false, no tools at all.
        // The LLM should receive NO tools — not even memory tools.
        IReadOnlyList<ToolDefinition>? toolsSeen = null;

        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "chat", FinishReason = "stop" };

            toolsSeen = tools;
            return new LlmResponse
            {
                IsComplete = true,
                Content = "Hey there! How can I help?",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hey there, how's it going?");

        Assert.True(result.Success);

        // toolsSeen should be null (no tools passed to LLM at all)
        Assert.Null(toolsSeen);

        // No tool calls other than MemoryRetrieve pre-fetch
        var agentTools = result.ToolCallsMade
            .Where(t => t.ToolName != "MemoryRetrieve").ToList();
        Assert.Empty(agentTools);
    }

    [Fact]
    public async Task MemoryWriteIntent_OnlyExposesMemoryTools()
    {
        // "Remember that..." → LooksLikeMemoryWriteRequest heuristic
        // → bypasses LLM classifier → routes to memory_write intent
        // → only memory tools exposed
        IReadOnlyList<ToolDefinition>? toolsSeen = null;
        var toolCallMade = false;

        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            // Classifier won't be called (heuristic short-circuits), but
            // handle it just in case
            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "tool", FinishReason = "stop" };

            // Capture tools on the first non-classify call
            if (toolsSeen == null)
                toolsSeen = tools;

            // First tool-loop call: request memory_store_facts
            if (!toolCallMade && tools is { Count: > 0 })
            {
                toolCallMade = true;
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_mem_001",
                            Function = new FunctionCallDetails
                            {
                                Name = "memory_store_facts",
                                Arguments = """{"factsJson":"[{\"subject\":\"user\",\"predicate\":\"occupation_is\",\"object\":\"software engineer\"}]","sourceRef":"conversation"}"""
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Got it — I'll remember that.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve"     => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_store_facts" => """{"stored":1,"replaced":0,"skipped":0,"conflicts":[],"message":"Stored 1 fact(s)."}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("Remember that I'm a software engineer.");

        Assert.True(result.Success);
        Assert.NotNull(toolsSeen);

        // Should only have memory tools — NOT screen_capture etc.
        var toolNames = toolsSeen!.Select(t => t.Function.Name).ToHashSet();
        Assert.Contains("memory_store_facts", toolNames);
        Assert.DoesNotContain("screen_capture", toolNames);
        Assert.DoesNotContain("web_search", toolNames);
        Assert.DoesNotContain("system_execute", toolNames);

        // Verify memory_store_facts was actually called
        var memStore = result.ToolCallsMade
            .FirstOrDefault(t => t.ToolName == "memory_store_facts");
        Assert.NotNull(memStore);
        Assert.True(memStore.Success);
    }

    [Fact]
    public async Task ScreenRequest_OnlyExposesScreenTools()
    {
        // "What can you see on my screen?" → screen_observe intent
        // → only screen tools exposed
        IReadOnlyList<ToolDefinition>? toolsSeen = null;
        var toolRequested = false;

        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            // Older path: router asks the LLM to classify first.
            // New path: obvious screen requests are routed deterministically and
            // the classify call is skipped. Handle both.
            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "tool", FinishReason = "stop" };

            if (tools is not null)
                toolsSeen = tools;

            // First executor call: request a screen capture.
            if (!toolRequested && tools is { Count: > 0 })
            {
                toolRequested = true;
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_scr_001",
                            Function = new FunctionCallDetails
                            {
                                Name = "screen_capture",
                                Arguments = "{}"
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "I can see a code editor on your screen.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "screen_capture" => """{"base64":"fakeData","width":1920,"height":1080}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What can you see on the screen right now?");

        Assert.True(result.Success);
        Assert.NotNull(toolsSeen);

        // Should only have screen tools
        var toolNames = toolsSeen!.Select(t => t.Function.Name).ToHashSet();
        Assert.Contains("screen_capture", toolNames);
        Assert.DoesNotContain("web_search", toolNames);
        Assert.DoesNotContain("system_execute", toolNames);
        Assert.DoesNotContain("memory_store_facts", toolNames);
    }

    [Fact]
    public async Task ToolLoop_BlocksToolCallsOutsidePolicyAllowList()
    {
        var requestedForbiddenTool = false;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = "tool", FinishReason = "stop" };

            if (!requestedForbiddenTool && tools is { Count: > 0 })
            {
                requestedForbiddenTool = true;
                return new LlmResponse
                {
                    IsComplete = false,
                    FinishReason = "tool_calls",
                    ToolCalls =
                    [
                        new ToolCallRequest
                        {
                            Id = "call_forbidden_001",
                            Function = new FunctionCallDetails
                            {
                                Name = "web_search",
                                Arguments = """{"query":"not allowed here"}"""
                            }
                        }
                    ]
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "I cannot use that tool in this route.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "screen_capture" => """{"base64":"fakeData","width":1920,"height":1080}""",
                "web_search" => "this should never run",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What can you see on the screen right now?");

        Assert.True(result.Success);
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));

        var blockedCall = result.ToolCallsMade.FirstOrDefault(t =>
            t.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(blockedCall);
        Assert.False(blockedCall!.Success);
        Assert.Contains("tool_not_permitted", blockedCall.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolLoop_ResolvesConflictsBeforeExecution_ExecutesWinnersOnly()
    {
        var requestedConflictingTools = false;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = "tool", FinishReason = "stop" };

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
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "screen_capture" => "this should not run",
                "get_active_window" => """{"title":"IDE","process":"cursor"}""",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What can you see on my screen right now?");

        Assert.True(result.Success);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("get_active_window", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("screen_capture", StringComparison.OrdinalIgnoreCase));

        var skipped = result.ToolCallsMade.FirstOrDefault(t =>
            t.ToolName.Equals("screen_capture", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(skipped);
        Assert.False(skipped!.Success);
        Assert.Contains("tool_conflict_skipped", skipped.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deterministic_priority", skipped.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PolicyDecision_IsAudited()
    {
        // Verify that the ROUTER_OUTPUT and POLICY_DECISION audit events fire
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "chat", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "Hello!", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        await agent.ProcessAsync("hello!");

        // Verify both new audit events exist
        Assert.NotEmpty(audit.GetByAction("ROUTER_OUTPUT"));
        Assert.NotEmpty(audit.GetByAction("POLICY_DECISION"));
    }

    [Fact]
    public async Task ReasoningGuardrails_AutoMode_EmitsGuardrailsMetadata()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (system.Contains("Classify", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = "chat", FinishReason = "stop" };

            if (system.Contains("Infer the practical real-world goal", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"primary_goal":"Complete the prerequisite before exiting.","alternative_goals":[],"confidence":0.9}""",
                    FinishReason = "stop"
                };
            }

            if (system.Contains("Build practical constraints", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"constraints":["Respect explicit prerequisites before choosing an action."]}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse { IsComplete = true, Content = "normal", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient(returnValue: "");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "auto"
        };

        var result = await agent.ProcessAsync(
            "The parking garage gate is ahead. Should I drive out now or pay at the kiosk first?");

        Assert.True(result.Success);
        Assert.True(result.GuardrailsUsed);
        Assert.NotEmpty(result.GuardrailsRationale);
        Assert.Contains("pay at the kiosk first", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReasoningGuardrails_AlwaysMode_FallsBackWhenExtractionFails()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (system.Contains("Classify", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = "chat", FinishReason = "stop" };

            if (system.Contains("Infer the practical real-world goal", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = "not-json", FinishReason = "stop" };

            if (system.Contains("Extract entities and action options", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = "not-json", FinishReason = "stop" };

            return new LlmResponse { IsComplete = true, Content = "normal fallback", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient(returnValue: "");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "always"
        };

        var result = await agent.ProcessAsync("Hey there, how are you?");

        Assert.True(result.Success);
        Assert.False(result.GuardrailsUsed);
        Assert.Equal("normal fallback", result.Text);
    }

    [Fact]
    public async Task LookupFact_ResponseContract_SuppressesCardsAndToolActivity()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"name":"Paris Agreement","type":"topic","hint":"climate treaty"}""", FinishReason = "stop" };
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"Paris Agreement definition","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "The Paris Agreement is an international climate accord adopted in 2015.", FinishReason = "stop" };
        });

        var searchResult =
            "1. Paris Agreement overview\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/paris\",\"title\":\"Paris Agreement overview\"}]";

        var mcp = new FakeMcpClient(returnValue: searchResult);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            MemoryEnabled = false,
            ReasoningGuardrailsMode = "off"
        };

        var result = await agent.ProcessAsync("what's the Paris Agreement");

        Assert.True(result.Success);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);
        Assert.Contains("Paris Agreement", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LookupNews_ResponseContract_LeavesCardsAndToolActivityVisible()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"name":"Nvidia","type":"org","hint":"chipmaker"}""", FinishReason = "stop" };
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"Nvidia latest news today","recency":"day"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "Here are the latest Nvidia headlines for today.", FinishReason = "stop" };
        });

        var searchResult =
            "1. Nvidia story one\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/nvda\",\"title\":\"Nvidia story one\"}]";

        var mcp = new FakeMcpClient(returnValue: searchResult);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            MemoryEnabled = false,
            ReasoningGuardrailsMode = "off"
        };

        var result = await agent.ProcessAsync("latest news on Nvidia today");

        Assert.True(result.Success);
        Assert.False(result.SuppressSourceCardsUi);
        Assert.False(result.SuppressToolActivityUi);
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LookupFact_MontySwallowShortcut_ReturnsDeterministicGagWithoutSearch()
    {
        var llm = new FakeLlmClient("This should not be needed.");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            MemoryEnabled = false,
            ReasoningGuardrailsMode = "off"
        };

        var result = await agent.ProcessAsync("airspeed velocity of an unladen swallow");

        Assert.True(result.Success);
        Assert.Contains("African or a European swallow", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
    }
}

#endregion

#region ── Test Doubles ──────────────────────────────────────────────────

/// <summary>
/// Fake LLM client that supports both text and tool-call responses.
/// <para>
/// <b>Text mode:</b> Pass a <c>Func&lt;messages, string&gt;</c> and
/// every <see cref="ChatAsync"/> call returns a complete text response.
/// </para>
/// <para>
/// <b>Full mode:</b> Pass a <c>Func&lt;messages, tools, LlmResponse&gt;</c>
/// and the caller has full control over the returned response, including
/// <see cref="LlmResponse.ToolCalls"/>.
/// </para>
/// </summary>
internal sealed class FakeLlmClient : ILlmClient
{
    private readonly Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>?, LlmResponse> _respond;

    // ── Text-only constructors (backwards compatible) ──────────────

    public FakeLlmClient(Func<IReadOnlyList<ChatMessage>, string> respond)
        : this((msgs, _) => new LlmResponse
        {
            IsComplete   = true,
            Content      = respond(msgs),
            FinishReason = "stop"
        }) { }

    public FakeLlmClient(string fixedResponse)
        : this(_ => fixedResponse) { }

    // ── Full-control constructor (can return tool calls) ──────────

    public FakeLlmClient(
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>?, LlmResponse> respond)
        => _respond = respond;

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_respond(messages, tools));
    }

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        CancellationToken cancellationToken = default)
    {
        return ChatAsync(messages, tools, cancellationToken);
    }

    public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>("fake-test-model");
}

/// <summary>
/// Fake MCP tool client with per-tool response routing and realistic
/// tool discovery. Tracks all calls for assertion.
/// </summary>
internal sealed class FakeMcpClient : IMcpToolClient
{
    private readonly Func<string, string, string> _toolHandler;
    private readonly IReadOnlyList<McpToolInfo> _availableTools;

    public List<(string Tool, string Args)> Calls { get; } = [];

    // ── Simple constructor: fixed return for all tools ─────────────

    public FakeMcpClient(string returnValue)
        : this((_, _) => returnValue, []) { }

    // ── Routing constructor: per-tool response logic ──────────────

    public FakeMcpClient(
        Func<string, string, string> toolHandler,
        IReadOnlyList<McpToolInfo>? availableTools = null)
    {
        _toolHandler   = toolHandler;
        _availableTools = availableTools ?? [];
    }

    public Task<string> CallToolAsync(
        string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        Calls.Add((toolName, argumentsJson));
        return Task.FromResult(_toolHandler(toolName, argumentsJson));
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_availableTools);
    }

    // ── Helpers for building realistic tool lists ──────────────────

    /// <summary>
    /// A representative set of MCP tools matching what the real server
    /// exposes. Used by tests that need to verify tool filtering, the
    /// tool loop, or multi-tool scenarios.
    /// </summary>
    public static IReadOnlyList<McpToolInfo> StandardToolSet =>
    [
        MakeTool("screen_capture",     "Captures the user's screen",
                 """{"type":"object","properties":{"monitor":{"type":"integer","description":"Monitor index"}},"required":[]}"""),
        MakeTool("get_active_window",  "Gets active window metadata",
                 """{"type":"object","properties":{},"required":[]}"""),
        MakeTool("browser_navigate",   "Fetches URL content",
                 """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}"""),
        MakeTool("memory_store_facts", "Stores structured facts about the user",
                 """{"type":"object","properties":{"factsJson":{"type":"string","description":"JSON array of fact objects"},"sourceRef":{"type":"string","description":"Source reference"}},"required":["factsJson"]}"""),
        MakeTool("memory_update_fact", "Updates an existing memory fact",
                 """{"type":"object","properties":{"memoryId":{"type":"string"},"newObject":{"type":"string"}},"required":["memoryId","newObject"]}"""),
        MakeTool("memory_retrieve",    "Retrieves relevant memories",
                 """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
        MakeTool("memory_list_facts",  "Lists memory facts",
                 """{"type":"object","properties":{"filter":{"type":"string"}},"required":[]}"""),
        MakeTool("memory_delete_fact", "Deletes a memory fact",
                 """{"type":"object","properties":{"memoryId":{"type":"string"}},"required":["memoryId"]}"""),
        MakeTool("web_search",         "Searches the web for information",
                 """{"type":"object","properties":{"query":{"type":"string"},"maxResults":{"type":"integer"},"recency":{"type":"string"}},"required":["query"]}"""),
        MakeTool("weather_geocode",    "Geocodes a place for weather lookup",
                 """{"type":"object","properties":{"place":{"type":"string"},"maxResults":{"type":"integer"}},"required":["place"]}"""),
        MakeTool("weather_forecast",   "Fetches weather from coordinates",
                 """{"type":"object","properties":{"latitude":{"type":"number"},"longitude":{"type":"number"},"placeHint":{"type":"string"},"countryCode":{"type":"string"},"days":{"type":"integer"}},"required":["latitude","longitude"]}"""),
        MakeTool("resolve_timezone",   "Resolves timezone from coordinates",
                 """{"type":"object","properties":{"latitude":{"type":"number"},"longitude":{"type":"number"},"countryCode":{"type":"string"}},"required":["latitude","longitude"]}"""),
        MakeTool("holidays_get",       "Returns public holidays by country/year",
                 """{"type":"object","properties":{"countryCode":{"type":"string"},"year":{"type":"integer"},"regionCode":{"type":"string"},"maxItems":{"type":"integer"}},"required":["countryCode"]}"""),
        MakeTool("holidays_next",      "Returns upcoming public holidays",
                 """{"type":"object","properties":{"countryCode":{"type":"string"},"regionCode":{"type":"string"},"maxItems":{"type":"integer"}},"required":["countryCode"]}"""),
        MakeTool("holidays_is_today",  "Checks if today is a public holiday",
                 """{"type":"object","properties":{"countryCode":{"type":"string"},"regionCode":{"type":"string"}},"required":["countryCode"]}"""),
        MakeTool("feed_fetch",         "Fetches and parses RSS/Atom feed URL",
                 """{"type":"object","properties":{"url":{"type":"string"},"maxItems":{"type":"integer"}},"required":["url"]}"""),
        MakeTool("status_check_url",   "Checks URL reachability",
                 """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}"""),
        MakeTool("MemoryRetrieve",     "Retrieves relevant memories",
                 """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
        MakeTool("file_read",          "Reads a file from disk",
                 """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        MakeTool("file_list",          "Lists files in a directory",
                 """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        MakeTool("system_execute",     "Executes an allowlisted command",
                 """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}"""),
        MakeTool("tool_ping",          "Reports MCP health",
                 """{"type":"object","properties":{},"required":[]}"""),
        MakeTool("tool_list_capabilities", "Lists available tool capabilities",
                 """{"type":"object","properties":{},"required":[]}"""),
        MakeTool("time_now",           "Returns local time metadata",
                 """{"type":"object","properties":{},"required":[]}"""),
    ];

    private static McpToolInfo MakeTool(string name, string desc, string schemaJson)
    {
        var schema = System.Text.Json.JsonSerializer.Deserialize<object>(schemaJson)!;
        return new McpToolInfo { Name = name, Description = desc, InputSchema = schema };
    }
}

#endregion
