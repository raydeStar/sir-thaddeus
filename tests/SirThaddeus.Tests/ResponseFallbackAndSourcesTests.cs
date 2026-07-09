using SirThaddeus.Agent;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.Search.DeepDive;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class SourceCitationFormatterTests
{
    [Fact]
    public void Apply_RemovesDanglingSourceLine_AndAddsHtmlLinks()
    {
        var text =
            "Bottom line: short answer.\n\n" +
            "Sources:\n" +
            "1. CBR article: \"10 Biggest Differences\".\n" +
            "2. IMDb summary for How to Train Your Dragon (2025).\n" +
            "3.\n";

        var toolResult =
            "1. \"10 Biggest Differences\" — cbr.com\n" +
            "2. \"How to Train Your Dragon (2025)\" — imdb.com\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://www.cbr.com/articles/dragon-remake\",\"title\":\"10 Biggest Differences\",\"domain\":\"www.cbr.com\"}," +
            "{\"url\":\"https://www.imdb.com/title/tt26743210/\",\"title\":\"How to Train Your Dragon (2025)\",\"domain\":\"www.imdb.com\"}" +
            "]";

        var toolCalls = new List<ToolCallRecord>
        {
            new()
            {
                ToolName = "web_search",
                Arguments = "{\"query\":\"dragon\"}",
                Result = toolResult,
                Success = true
            }
        };

        var output = SourceCitationFormatter.Apply(text, toolCalls);

        Assert.DoesNotContain("\n3.", output, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://www.cbr.com/articles/dragon-remake\">", output, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://www.imdb.com/title/tt26743210/\">", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_UsesCompactDomain_ForLongGoogleNewsUrls()
    {
        var text = "Sources:\n1. CBR coverage.\n";

        var toolResult =
            "1. \"CBR coverage\" — cbr.com\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://news.google.com/rss/articles/CBMiM2h0dHBzOi8vd3d3LmNici5jb20vYXJ0aWNsZS1yZWFsbHktbG9uZy1wYXRoP2Zvby1iYXItYmF6LXF1eA\",\"title\":\"CBR coverage\",\"domain\":\"www.cbr.com\"}" +
            "]";

        var toolCalls = new List<ToolCallRecord>
        {
            new()
            {
                ToolName = "web_search",
                Arguments = "{}",
                Result = toolResult,
                Success = true
            }
        };

        var output = SourceCitationFormatter.Apply(text, toolCalls);
        Assert.Contains("<a href=\"https://www.cbr.com\">", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_DoesNotAlterNonSourceNumberedLists()
    {
        var text = "Plan:\n1. Step one\n2. Step two\n3.";
        var output = SourceCitationFormatter.Apply(text, []);

        Assert.Contains("\n3.", output, StringComparison.Ordinal);
    }
}

public class SearchOfflineFallbackTests
{
    [Fact]
    public void TryBuildGroundedTimeoutFallback_UsesRetrievedWebEvidence_InsteadOfTimeoutMessage()
    {
        var toolCalls = new List<ToolCallRecord>
        {
            new()
            {
                ToolName = "web_search",
                Arguments = "{\"query\":\"C# 13 changes\"}",
                Result =
                    "1. \"What’s new in C# 13\" — learn.microsoft.com\n" +
                    "   C# 13 adds params collections, improved lock support, and new escape-sequence features.\n\n" +
                    "<!-- SOURCES_JSON -->\n" +
                    "{" +
                    "\"sources\":[{" +
                    "\"url\":\"https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-13\"," +
                    "\"title\":\"What’s new in C# 13\"," +
                    "\"domain\":\"learn.microsoft.com\"," +
                    "\"excerpt\":\"C# 13 adds params collections, improved lock support, and new escape-sequence features.\"}" +
                    "]}",
                Success = true
            }
        };

        var fallback = SearchOrchestrator.TryBuildGroundedTimeoutFallback(
            "Use web_search to answer what changed in C# 13 and keep it practical.",
            toolCalls);

        Assert.NotNull(fallback);
        Assert.Contains("strongest evidence", fallback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("What’s new in C# 13", fallback, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Live web lookup is unavailable right now", fallback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuildGroundedTimeoutFallback_ForMediaComparison_AnswersTheQuestionDirectly()
    {
        var toolCalls = new List<ToolCallRecord>
        {
            new()
            {
                ToolName = "web_search",
                Arguments = "{\"query\":\"live-action How to Train Your Dragon word for word\"}",
                Result =
                    "1. \"5 Surprising Differences Between the Animated and Live-Action How to Train Your Dragon Movies\" — collider.com\n" +
                    "   The article highlights story and scene differences between the live-action remake and the original animated film.\n\n" +
                    "<!-- SOURCES_JSON -->\n" +
                    "{" +
                    "\"sources\":[{" +
                    "\"url\":\"https://collider.com/how-to-train-your-dragon-live-action-differences/\"," +
                    "\"title\":\"5 Surprising Differences Between the Animated and Live-Action How to Train Your Dragon Movies\"," +
                    "\"domain\":\"collider.com\"," +
                    "\"excerpt\":\"The article highlights story and scene differences between the live-action remake and the original animated film.\"}" +
                    "]}",
                Success = true
            }
        };

        var fallback = SearchOrchestrator.TryBuildGroundedTimeoutFallback(
            "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?",
            toolCalls);

        Assert.NotNull(fallback);
        Assert.Contains("No", fallback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("original", fallback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("difference", fallback, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("strongest evidence I found", fallback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuildMediaInstallmentFallback_ForMissingSeasonEpisode_ReturnsNonInventedPlotFallback()
    {
        var response = SearchOrchestrator.TryBuildMediaInstallmentFallback(
            "What would be the plot of Episode 2 of Season 7 of Meridian Drift about?");

        Assert.NotNull(response);
        Assert.Contains("Season 7 Episode 2", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Meridian Drift", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not verify an official", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("should not invent a plot", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("couldn't generate a clean summary", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuildMediaInstallmentFallback_ForStargateUniverseSeason3Episode1_IncludesSeriesAndInstallment()
    {
        var response = SearchOrchestrator.TryBuildMediaInstallmentFallback(
            "What would be the plot of Episode 1 of Season 3 of Stargate Universe about?");

        Assert.NotNull(response);
        Assert.Contains("Stargate Universe", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Season 3 Episode 1", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not verify an official", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("should not invent a plot", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWebSearchUnavailable_UsesBestEffortReasoning()
    {
        var llm = new StubLlmClient(
            "The capital of Oregon is Salem, but verify current civic details directly.");
        var mcp = new StubMcpClient(
            "{\"error\":{\"code\":\"tool_unavailable\",\"message\":\"web search unavailable\"}}");
        var audit = new RecordingAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            audit,
            "You are a concise assistant.");

        var history = new List<ChatMessage>
        {
            ChatMessage.User("What is the capital of Oregon?")
        };
        var toolCalls = new List<ToolCallRecord>();

        var response = await orchestrator.ExecuteAsync(
            userMessage: "What is the capital of Oregon?",
            memoryPackText: "",
            history: history,
            toolCallsMade: toolCalls,
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("I don't have fresh live results for this turn", response.Text, StringComparison.Ordinal);
        Assert.Contains("best-effort answer", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Text.Length > 50);
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitWebSearchUnavailable_KeepsUnavailableKeyword()
    {
        var llm = new StubLlmClient(
            "Rust release notes are usually published on the official Rust blog and release channels, but verify the current page directly.");
        var mcp = new StubMcpClient(
            "{\"error\":{\"code\":\"tool_unavailable\",\"message\":\"web search unavailable\"}}");
        var audit = new RecordingAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            audit,
            "You are a concise assistant.");

        var userMessage = "Use web_search to find the latest Rust language release notes.";
        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("unavailable", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rust", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLookupPromptIsClassicLogicPuzzle_ReturnsDeterministicAnswerWithoutTools()
    {
        const string userMessage = "I'm on a game show with three doors. Behind one door is a car, behind the other two are goats. I pick door 1. The host opens door 3, showing a goat. Should I switch to door 2 or stick with door 1?";

        var llm = new StubLlmClient("This should not be used.");
        var mcp = new TrackingStubMcpClient("[search: 1 result(s) returned]");
        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            new StubAuditLogger(),
            "You are a concise assistant.");

        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("switch", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("better odds", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWebSearchReturnsNoResults_DoesNotOpenBrowserFallback()
    {
        var llm = new StubLlmClient(
            "C# 13 adds params collections and new escape-sequence features.");
        var mcp = new TrackingStubMcpClient("[search: 0 result(s) returned]");
        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            new StubAuditLogger(),
            "You are a concise assistant.");

        var response = await orchestrator.ExecuteAsync(
            userMessage: "Use web_search to answer what changed in C# 13 and keep it practical.",
            memoryPackText: "",
            history: [ChatMessage.User("Use web_search to answer what changed in C# 13 and keep it practical.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        // Live search returned 0 results, so the agent must not invent a feature list.
        // It should report the lookup is unavailable and not silently fall back to the browser.
        Assert.Contains("unavailable", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("params collections", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Threading.Lock", response.Text, StringComparison.OrdinalIgnoreCase);
        var toolCall = Assert.Single(mcp.Calls);
        Assert.Equal("web_search", toolCall, ignoreCase: true);
        Assert.DoesNotContain(mcp.Calls, call =>
            call.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitWebSearchNoResultsAfterEntityResolution_KeepsUnavailableKeyword()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;
            if (systemPrompt.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"name":"Rust","type":"topic","hint":"Programming language"}""",
                    FinishReason = "stop"
                };
            }

            if (systemPrompt.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"rust release notes history","recency":"month"}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Rust release notes are usually posted on the official Rust blog.",
                FinishReason = "stop"
            };
        });

        var mcp = new TrackingStubMcpClient("[search: 0 result(s) returned]");
        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            new StubAuditLogger(),
            "You are a concise assistant.");

        const string originalUserMessage = "Use web_search to find the latest Rust language release notes.";
        const string userMessage = "Find the latest Rust language release notes.";
        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(originalUserMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("unavailable", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            mcp.Calls.Count(call => call.Equals("web_search", StringComparison.OrdinalIgnoreCase)) >= 2,
            "Expected at least the entity-resolution and fact-find web_search calls.");
        Assert.DoesNotContain(mcp.Calls, call =>
            call.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitRustSearchReturnsNoResults_DoesNotUseOfflineLatestVersionFallback()
    {
        var llm = new StubLlmClient(
            "Rust release notes are usually posted on the official Rust blog.");
        var mcp = new TrackingStubMcpClient("[search: 0 result(s) returned]");
        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            new StubAuditLogger(),
            "You are a concise assistant.");

        const string userMessage = "Use web_search to find the latest Rust language release notes.";
        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("unavailable", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latest major Rust release", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, call =>
            call.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLatestVersionSearchReturnsNoResults_ReportsLookupUnavailable()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;
            if (systemPrompt.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"name":"QuantaScript","type":"framework","hint":"Synthetic developer platform"}""",
                    FinishReason = "stop"
                };
            }

            if (systemPrompt.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"the latest stable version of QuantaScript as of 2025","recency":"any"}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "This should not be used when the deterministic latest-version fallback succeeds.",
                FinishReason = "stop"
            };
        });

        var mcp = new RecordingStubMcpClient((toolName, _, _) =>
            toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase)
                ? "[search: 0 result(s) returned]"
                : string.Empty);

        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            new StubAuditLogger(),
            "You are a concise assistant.");

        const string userMessage = "What is the latest stable version of QuantaScript as of 2025? Answer in exactly two lines: Line 1 starts with 'Answer:' and Line 2 starts with 'Commentary:'. Keep it concise.";
        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        // Live search returned no results, so the agent must not invent any answer string.
        // It still honors the user's explicit two-line response contract.
        Assert.Equal(
            "Answer: Live lookup is unavailable for this request, so I do not have confirmed results.\n" +
            "Commentary: Please retry in a moment.",
            response.Text);
        Assert.Contains(mcp.Calls, call =>
            call.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, call =>
            call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenStrictMediaComparisonDraftIsIndirect_UsesDirectGroundedFallback()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;
            if (systemPrompt.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"name":"How to Train Your Dragon","type":"movie","hint":"film"}""",
                    FinishReason = "stop"
                };
            }

            if (systemPrompt.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"\"How to Train Your Dragon\" live action differences original movie","recency":"any"}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content =
                    "- Reviews say the remake adds new material and scene changes.\n" +
                    "- Coverage also points to tonal shifts from the original animated movie.\n" +
                    "- Multiple writeups describe creative differences rather than a scene-for-scene copy.",
                FinishReason = "stop"
            };
        });

        var webSearchPayload =
            "1. \"5 Surprising Differences Between the Animated and Live-Action How to Train Your Dragon Movies\" — collider.com\n" +
            "   The article highlights story and scene differences between the live-action remake and the original animated film.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://collider.com/how-to-train-your-dragon-live-action-differences/\",\"title\":\"5 Surprising Differences Between the Animated and Live-Action How to Train Your Dragon Movies\",\"domain\":\"collider.com\",\"excerpt\":\"The article highlights story and scene differences between the live-action remake and the original animated film.\"}]";

        var mcp = new RecordingStubMcpClient((toolName, _, _) =>
            toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase)
                ? webSearchPayload
                : string.Empty);

        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            new StubAuditLogger(),
            "You are a concise assistant.");

        const string userMessage = "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?";
        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.StartsWith("No", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("word for word", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("original", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("difference", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("strongest evidence I found", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFactFindSummaryReturnsSignedUnavailableContract_RebuildsGroundedAnswer()
    {
        var llmCallCount = 0;
        var llm = new FakeLlmClient((messages, _) =>
        {
            llmCallCount++;
            var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;
            if (systemPrompt.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"name":"Orion Mesh","type":"technology","hint":"Synthetic orchestration framework"}""",
                    FinishReason = "stop"
                };
            }

            if (systemPrompt.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"Orion Mesh updates","recency":"any"}""",
                    FinishReason = "stop"
                };
            }

            if (llmCallCount == 3)
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "Live lookup is unavailable for this request, so I do not have confirmed results to quote right now. Please retry in a moment.\n\n-- Sir Thaddeus",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Overview: Orion Mesh continues to expand its orchestration and developer-experience tooling. Common Points: multiple sources describe ongoing investment in app composition and local development workflows. Differences: some coverage emphasizes CLI and dashboard changes while other sources focus on integrations and deployment improvements. Practical Takeaway: teams evaluating Orion Mesh should expect a broader platform story, but the exact emphasis depends on which subsystem they care about most.",
                FinishReason = "stop"
            };
        });

        var webSearchPayload =
            "1. \"Orion Mesh 9.5 Released\" — example.com\n" +
            "   Covers CLI updates, dashboard changes, and expanded integrations.\n\n" +
            "2. \"What's new in Orion Mesh\" — example.org\n" +
            "   Highlights orchestration improvements and developer-experience updates.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/orion-mesh-95\",\"title\":\"Orion Mesh 9.5 Released\",\"domain\":\"example.com\",\"excerpt\":\"Covers CLI updates, dashboard changes, and expanded integrations.\"}," +
            "{\"url\":\"https://example.org/orion-mesh/whats-new\",\"title\":\"What's new in Orion Mesh\",\"domain\":\"example.org\",\"excerpt\":\"Highlights orchestration improvements and developer-experience updates.\"}]";

        var mcp = new RecordingStubMcpClient((toolName, _, _) =>
            toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase)
                ? webSearchPayload
                : string.Empty);

        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            new StubAuditLogger(),
            "You are a concise assistant.");

        const string userMessage = "Search for recent updates and developments in Orion Mesh from the last year. Synthesize information from multiple sources, compare what overlaps and what differs. Provide a structured response with: Overview, Common Points, Differences, Practical Takeaway.";
        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.DoesNotContain("unavailable for this request", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overview", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("common points", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("differences", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("practical takeaway", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineWebReasoningResponder_WhenStrictTwoLineLatestVersionPromptHasNoLiveData_DoesNotInventAnswer()
    {
        const string userMessage = "Use web_search to find the latest stable version of QuantaScript as of 2025. Answer in exactly two lines: Line 1 starts with 'Answer:' and Line 2 starts with 'Commentary:'. Keep it concise.";

        var response = await OfflineWebReasoningResponder.BuildAsync(
            new StubLlmClient("This should not be used when deterministic fallback is forced."),
            "You are a concise assistant.",
            userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            failureReason: "tool_unavailable",
            cancellationToken: CancellationToken.None);

        // Live data is unavailable, so the offline responder must not synthesize
        // a clean two-line current-version answer without evidence.
        Assert.True(response.Success);
        Assert.Contains("unavailable", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\bQuantaScript\s+\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase), response.Text);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLocalBusinessHoursLookupReturnsNoResults_UsesDirectLocatorFallback()
    {
        var mcp = new RecordingStubMcpClient((toolName, _, _) =>
        {
            if (toolName.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase))
            {
                return """
                    {
                      "provider": "google_places",
                      "query": "Trader Joe's in Portland OR",
                      "error": "Google Places API key is not configured.",
                      "place": null,
                      "sources": []
                    }
                    """;
            }

            if (toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return "[search: 0 result(s) returned]";
            }

            if (toolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase))
            {
                return """
                    Trader Joe's Locations in Portland, OR
                    Portland NW (146)
                    2122 NW Glisan St Portland, OR 97210
                    Call Portland NW (146), Portland on (971) 544-0788
                    """;
            }

            return string.Empty;
        });

        var coordinator = new DeepDiveCoordinator(mcp, new TestAuditLogger());
        var toolCalls = new List<ToolCallRecord>();

        var response = await coordinator.BuildPlaceBriefingAsync(
            query: "When does Trader Joe's in Portland OR open?",
            timezone: "America/Los_Angeles",
            locale: "en-US",
            userLocationHint: "Portland, OR",
            toolCallsMade: toolCalls,
            cancellationToken: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("Trader Joe", response.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fallback search came back with 0 results", response.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            call.ArgumentsJson.Contains("locations.traderjoes.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenPlaceHoursWebFallbackOnlyReturnsReddit_IgnoresOffTopicThreadTitles()
    {
        const string redditResult =
            "1. \"Whats your alls opinion on rogue trader (the game)\" — reddit.com\n" +
            "   Thread title unrelated to Trader Joe's hours.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.reddit.com/r/RogueTrader/comments/abc123/opinion_thread/\",\"title\":\"Whats your alls opinion on rogue trader (the game)\",\"domain\":\"reddit.com\",\"excerpt\":\"Thread title unrelated to Trader Joe's hours.\"}]";

        var mcp = new RecordingStubMcpClient((toolName, _, _) =>
        {
            if (toolName.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase))
            {
                return """
                    {
                      "provider": "google_places",
                      "query": "Trader Joe's in Portland OR",
                      "error": "Google Places API key is not configured.",
                      "place": null,
                      "sources": []
                    }
                    """;
            }

            if (toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return redditResult;
            }

            return "[browser: title: \"reddit - please wait for verification\", content returned]";
        });
        var coordinator = new DeepDiveCoordinator(mcp, new TestAuditLogger());
        var toolCalls = new List<ToolCallRecord>();

        var response = await coordinator.BuildPlaceBriefingAsync(
            query: "When does Trader Joe's in Portland OR open?",
            timezone: "America/Los_Angeles",
            locale: "en-US",
            userLocationHint: "Portland, OR",
            toolCallsMade: toolCalls,
            cancellationToken: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("Trader Joe", response.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rogue trader", response.AssistantText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            call.ArgumentsJson.Contains("locations.traderjoes.com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, call =>
            call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            call.ArgumentsJson.Contains("reddit.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNearbyLocalBusinessSearchOnlyFindsIrrelevantResult_ReturnsSafeNoMatchResponse()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;
            if (systemPrompt.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"deli near Olympia, WA","recency":"any"}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Yes - the local bakery at 40 Rue de lAlma is currently open.",
                FinishReason = "stop"
            };
        });

        const string irrelevantSearchResult =
            "1. \"La Boulangerie\" — example.fr\n" +
            "   Bakery at 40 Rue de lAlma, Paris. Open until 8 PM.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.fr/bakery\",\"title\":\"La Boulangerie\",\"domain\":\"example.fr\",\"excerpt\":\"Bakery at 40 Rue de lAlma, Paris. Open until 8 PM.\"}]";

        var mcp = new RecordingStubMcpClient((toolName, _, _) =>
        {
            if (toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return irrelevantSearchResult;
            }

            return string.Empty;
        });

        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            new RecordingAuditLogger(),
            "You are a concise assistant.")
        {
            UserLocationHint = "Olympia, WA",
            AdvancedPlaceDiscoveryEnabled = false,
            DeepDiveEnabled = false
        };

        const string userMessage = "Is there a deli nearby?";
        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("deli", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Olympia, WA", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rue de lAlma", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bakery", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try naming one specific place", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoResultsRecoveredByBroaderRecency_StopsAfterRecoveredRetry()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;
            if (systemPrompt.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"name":"Rust","type":"topic","hint":"Programming language"}""",
                    FinishReason = "stop"
                };
            }

            if (systemPrompt.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"rust release notes history","recency":"month"}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Rust release notes are published on the official Rust blog, including the latest stable release notes.",
                FinishReason = "stop"
            };
        });

        const string recoveredSearchResult =
            "1. \"Rust 1.81.0 release notes\" — blog.rust-lang.org\n" +
            "   The Rust team announced the latest stable release.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://blog.rust-lang.org/2024/09/05/Rust-1.81.0.html\",\"title\":\"Rust 1.81.0 release notes\",\"domain\":\"blog.rust-lang.org\"}]";

        var mcp = new RecordingStubMcpClient((toolName, argumentsJson, callIndex) =>
        {
            if (!toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase))
                return recoveredSearchResult;

            if (callIndex == 1)
                return "[search: 0 result(s) returned]";

            if (argumentsJson.Contains("\"recency\":\"any\"", StringComparison.OrdinalIgnoreCase))
                return recoveredSearchResult;

            return "[search: 0 result(s) returned]";
        });

        var audit = new RecordingAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            audit,
            "You are a concise assistant.");

        const string userMessage = "Find the latest Rust language release notes.";
        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.DoesNotContain("unavailable", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rust", response.Text, StringComparison.OrdinalIgnoreCase);

        var webCalls = mcp.Calls.Where(call =>
            call.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Equal(3, webCalls.Count);
        Assert.Contains(webCalls, call =>
            call.ArgumentsJson.Contains("\"recency\":\"month\"", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(webCalls, call =>
            call.ArgumentsJson.Contains("\"recency\":\"any\"", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.Events, auditEvent =>
            auditEvent.Action == "NO_RESULTS_RECENCY_BROADEN" &&
            auditEvent.Result == "recovered");
        Assert.DoesNotContain(audit.Events, auditEvent =>
            auditEvent.Action == "NO_RESULTS_QUERY_RETRY");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSeasonEpisodeSearchNeedsFollowupEvidence_ReturnsNonexistentInstallmentFallback()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;
            if (systemPrompt.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"name":"Meridian Drift","type":"series","hint":"Synthetic sci-fi series"}""",
                    FinishReason = "stop"
                };
            }

            if (systemPrompt.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"plot of episode 2 s7 meridian drift","recency":"any"}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Hallucinated plot summary that should be replaced by the existence guard.",
                FinishReason = "stop"
            };
        });

        const string genericSearchResult =
            "1. \"Meridian Drift discussion thread\" — forum.example\n" +
            "   Fans discuss the series in general terms.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://forum.example/meridian-drift/general\",\"title\":\"Meridian Drift discussion thread\",\"domain\":\"forum.example\",\"excerpt\":\"Fans discuss the series in general terms.\"}]";

        const string cancellationEvidenceResult =
            "1. \"Meridian Drift ended after six seasons\" — reference.example\n" +
            "   The series ended after season 6 and was never renewed for season 7.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://reference.example/meridian-drift\",\"title\":\"Meridian Drift ended after six seasons\",\"domain\":\"reference.example\",\"excerpt\":\"The series ended after season 6 and was never renewed for season 7.\"}]";

        var mcp = new RecordingStubMcpClient((toolName, argumentsJson, _) =>
        {
            if (toolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase))
                return "[browser: title: \"reddit - please wait for verification\", content returned]";

            if (!toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase))
                return "{}";

            if (argumentsJson.Contains("season 7 cancelled", StringComparison.OrdinalIgnoreCase) ||
                argumentsJson.Contains("number of seasons", StringComparison.OrdinalIgnoreCase) ||
                argumentsJson.Contains("episode list", StringComparison.OrdinalIgnoreCase))
            {
                return cancellationEvidenceResult;
            }

            return genericSearchResult;
        });

        var audit = new RecordingAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            audit,
            "You are a concise assistant.");

        const string userMessage = "What would be the plot of Episode 2 of Season 7 of Meridian Drift about?";
        var response = await orchestrator.ExecuteAsync(
            userMessage: userMessage,
            memoryPackText: "",
            history: [ChatMessage.User(userMessage)],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("Meridian Drift", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Season 7 Episode 2", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("should not invent a plot", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("surviving survivor", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
            call.ArgumentsJson.Contains("season 7 cancelled", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.Events, auditEvent =>
            auditEvent.Action == "EXISTENCE_GUARD_TRIGGERED" &&
            auditEvent.Result == "does_not_exist");
    }

    private sealed class StubLlmClient(string responseText, string finishReason = "stop") : ILlmClient
    {
        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = responseText,
                FinishReason = finishReason
            });

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
            => ChatAsync(messages, tools, cancellationToken);

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("stub-model");
    }

    private sealed class StubMcpClient(string webResponse) : IMcpToolClient
    {
        public Task<string> CallToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(webResponse);
        }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }

    private sealed class TrackingStubMcpClient(string webResponse) : IMcpToolClient
    {
        public List<string> Calls { get; } = [];

        public Task<string> CallToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(toolName);
            return Task.FromResult(webResponse);
        }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }

    private sealed class RecordingStubMcpClient(Func<string, string, int, string> responseFactory) : IMcpToolClient
    {
        public List<(string ToolName, string ArgumentsJson)> Calls { get; } = [];

        public Task<string> CallToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((toolName, argumentsJson));
            return Task.FromResult(responseFactory(toolName, argumentsJson, Calls.Count));
        }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = [];

        public void Append(AuditEvent auditEvent) => Events.Add(auditEvent);

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public IReadOnlyList<AuditEvent> ReadTail(int maxEvents)
            => Events.TakeLast(Math.Max(0, maxEvents)).ToList();

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(
            int maxEvents,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AuditEvent> result = ReadTail(maxEvents);
            return Task.FromResult(result);
        }
    }

    private sealed class StubAuditLogger : IAuditLogger
    {
        public void Append(AuditEvent auditEvent) { }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IReadOnlyList<AuditEvent> ReadTail(int maxEvents)
            => [];

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(
            int maxEvents,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AuditEvent> result = [];
            return Task.FromResult(result);
        }
    }

}

/// <summary>
/// Validates the bare-response enrichment guard in SanitizeFinalResponse.
/// When a question gets a bare "Yes"/"No" answer, the post-processor
/// appends a conversational continuation prompt.
/// </summary>
public class BareResponseEnrichmentTests
{
    private readonly DeterministicChatPostProcessor _processor = new();

    [Theory]
    [InlineData("Yes", "Is McDonalds open?")]
    [InlineData("No", "Is the store closed?")]
    [InlineData("Yep", "Are they open today?")]
    [InlineData("Nope", "Does this work?")]
    public void SanitizeFinalResponse_BareAffirmationToQuestion_GetsEnriched(
        string bareAnswer, string userQuestion)
    {
        var result = _processor.SanitizeFinalResponse(
            bareAnswer,
            new List<ToolCallRecord>(),
            userQuestion);

        Assert.NotEqual(bareAnswer, result);
        Assert.StartsWith(bareAnswer, result);
        Assert.True(result.Length > bareAnswer.Length + 5);
    }

    [Fact]
    public void SanitizeFinalResponse_CarWashPrompt_WithConcreteBusinessDetails_UsesDeterministicFallback()
    {
        var result = _processor.SanitizeFinalResponse(
            "Given that Burger Barn at 12 Maple Avenue opens at 10 AM, let us focus on the task at hand: getting to the car wash.",
            new List<ToolCallRecord>(),
            "You're going to the car wash and it's only 50 meters away. Should you walk or drive?");

        Assert.Contains("Drive", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("car wash", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Burger Barn", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_MediaInstallmentPrompt_WithOffTopicSourceList_UsesNonexistentEpisodeFallback()
    {
        var result = _processor.SanitizeFinalResponse(
            "Here's the strongest evidence I found in the live results:\n- A franchise retrospective focuses on cast reunions and merchandise rather than any unreleased episode plot.",
            new List<ToolCallRecord>(),
            "What would be the plot of Episode 2 of Season 7 of Meridian Drift about?");

        Assert.Contains("Meridian Drift", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Season 7 Episode 2", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cast reunions", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("merchandise", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_ToolBackedExistenceAnswer_StripsKnowledgeCutoffClause()
    {
        var result = _processor.SanitizeFinalResponse(
            "No - an iPhone 99 has never been released, nor is there evidence of one in official records up to my knowledge cutoff.",
            new List<ToolCallRecord>
            {
                new() { ToolName = "web_search", Arguments = "{}", Result = "[search: 1 result(s) returned]", Success = true }
            },
            "Does iPhone 99 exist as a released product?");

        Assert.Contains("iPhone 99", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("knowledge cutoff", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_ToolBackedExistenceAnswer_StripsAsOfKnowledgeCutoffClause()
    {
        var result = _processor.SanitizeFinalResponse(
            "No - iPhone 99 has never been released as an official Apple product. As of my knowledge cutoff, reports of one are likely custom builds or unverified mockups rather than a real release.",
            new List<ToolCallRecord>
            {
                new() { ToolName = "web_search", Arguments = "{}", Result = "[search: 3 result(s) returned]", Success = true }
            },
            "Does iPhone 99 exist as a released product?");

        Assert.Contains("iPhone 99", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("knowledge cutoff", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_ToolBackedSearchAnswer_TrimsInlineDuplicateAfterSignature()
    {
        var result = _processor.SanitizeFinalResponse(
            "Here are the main stories I found:\n1. Story one\n2. Story two -- Sir Thaddeus I am Sir Thaddeus, and I don't have access to live search results or real-time internet feeds of my own machine.",
            new List<ToolCallRecord>
            {
                new() { ToolName = "web_search", Arguments = "{}", Result = "[search: 2 result(s) returned]", Success = true }
            },
            "Search for a recent technology headline and summarize it in two sentences.");

        Assert.Contains("Here are the main stories I found", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-- Sir Thaddeus", result, StringComparison.Ordinal);
        Assert.DoesNotContain("don't have access to live search results", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("real-time internet feeds", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_SirThaddeusTcpEssay_CompressesToStructuredFallback()
    {
        var result = _processor.SanitizeFinalResponse(
            "The TCP (Transmission Control Protocol) three-way handshake is the handshake protocol that initiates a connection between two end-hosts over an unreliable network such as the Internet. It ensures reliable transmission of data by coordinating state before establishing communication, ensuring no packets are sent without confirmation from the peer. Here's how it works:\n\n1. **SYN** client starts the connection. 2. **SYN-ACK** server acknowledges and replies. 3. **ACK** client confirms the server response. Reliability matters because it synchronizes sequence numbers, supports retransmission behavior, protects against half-open sessions, and makes delivery more dependable across unreliable links.\n\n-- Sir Thaddeus",
            [],
            "Explain how TCP three-way handshake works and why it matters for reliability.");

        Assert.Contains("TCP three-way handshake", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1)", result, StringComparison.Ordinal);
        Assert.Contains("SYN", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SYN-ACK", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ACK", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-- Sir Thaddeus", result, StringComparison.Ordinal);
        Assert.True(result.Length < 700, "Expected the deterministic rewrite to stay concise.");
    }

    [Fact]
    public void SanitizeFinalResponse_OverlongTcpEssayWithoutSignature_CompressesToStructuredFallback()
    {
        var result = _processor.SanitizeFinalResponse(
            "The TCP three-way handshake establishes a reliable connection before data can be exchanged. " +
            "It starts with SYN from the client, continues with SYN-ACK from the server, and ends with ACK from the client. " +
            "This is important because it confirms both sides are alive, reachable, willing to communicate, synchronized on initial sequence numbers, ready for retransmission handling, prepared for ordered byte streams, protected against half-open sessions, and moved into an established state before application data flows. " +
            "Here is an extended explanation with repeated reliability details, timeout discussion, packet ordering, sequence tracking, state machines, retransmission windows, flow control, and error recovery that keeps going long past what a concise no-tool explanation needs to say for a user who asked for the basic handshake. " +
            "The client sends SYN, the server sends SYN-ACK, the client sends ACK, and then the established connection can transfer data reliably with sequence-number synchronization.",
            [],
            "Explain how TCP three-way handshake works and why it matters for reliability.");

        Assert.Contains("TCP three-way handshake", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.", result, StringComparison.Ordinal);
        Assert.Contains("SYN-ACK", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-- Sir Thaddeus", result, StringComparison.Ordinal);
        Assert.True(result.Length < 700, "Expected signatureless TCP explanations to stay concise too.");
    }

    [Fact]
    public void SanitizeFinalResponse_ToolBackedAnswer_StripsRawUrlCitation()
    {
        var result = _processor.SanitizeFinalResponse(
            "You can pull up local news in Boise using WashBOINER. Source: WashBOINER (https://washboinereader.org) -- Sir Thaddeus",
            new List<ToolCallRecord>
            {
                new()
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"Boise news\"}",
                    Result = "[search: 1 result(s) returned]",
                    Success = true
                }
            },
            "Pull up local news in Boise and summarize the key stories.");

        Assert.Contains("Source: WashBOINER", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("()", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeFinalResponse_EmptyNewsLead_UsesNewsQueryForHonestFallback()
    {
        var result = _processor.SanitizeFinalResponse(
            "Thanks for the message. Here are the main stories I found:",
            new List<ToolCallRecord>
            {
                new()
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"Boise, Idaho news\",\"maxResults\":5,\"recency\":\"day\",\"categories\":\"news\"}",
                    Result = "[search: 4 result(s) returned]",
                    Success = true
                }
            },
            "Hey whats up, how are you today? Can you pull up the local news in Boise, ID? Anyway, gotta go, bye!");

        Assert.Contains("Boise, Idaho", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local news", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Here are the main stories I found:", result.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeFinalResponse_LocalBusinessBriefingShell_UsesFirstTrustedWebSearchCandidate()
    {
        var result = _processor.SanitizeFinalResponse(
            "**Best Delis near Hillsboro, OR**\nVerification recommended\nSources checked: restaurantji.com, tripadvisor.com.\nBriefing summary: hours and review details are based on currently available web sources (2026).",
            new List<ToolCallRecord>
            {
                new()
                {
                    ToolName = "web_search",
                    Arguments = "{}",
                    Result =
                        "1. \"Bernie's Deli\" — example.com\n" +
                        "   Classic deli sandwiches in Hillsboro, OR.\n\n" +
                        "2. \"Isabella's Deli\" — example.org\n" +
                        "   Neighborhood deli in Hillsboro, OR.\n\n" +
                        "<!-- SOURCES_JSON -->\n" +
                        "{" +
                        "\"sources\":[" +
                        "{\"url\":\"https://example.com/bernies-deli\",\"title\":\"Bernie's Deli\",\"domain\":\"example.com\",\"excerpt\":\"Classic deli sandwiches in Hillsboro, OR.\"}," +
                        "{\"url\":\"https://example.org/isabellas-deli\",\"title\":\"Isabella's Deli\",\"domain\":\"example.org\",\"excerpt\":\"Neighborhood deli in Hillsboro, OR.\"}" +
                        "]}",
                    Success = true
                }
            },
            "Can you find me a good deli in Hillsboro, OR?");

        Assert.Contains("plausible deli", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bernie's Deli", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Isabella's Deli", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Verification recommended", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Briefing summary", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_LocalBusinessBriefingShell_ExcludesChainDepartmentCandidates()
    {
        var result = _processor.SanitizeFinalResponse(
            "**Best Delis near Hillsboro, OR**\nVerification recommended\nSources checked: example.com.\nBriefing summary: hours and review details are based on currently available web sources (2026).",
            new List<ToolCallRecord>
            {
                new()
                {
                    ToolName = "web_search",
                    Arguments = "{}",
                    Result =
                        "1. \"Isabella's Deli\" — example.com\n" +
                        "   Neighborhood deli in Hillsboro, OR.\n\n" +
                        "2. \"Walmart Deli in Hillsboro, OR | Grab & Go Sandwiches & Wraps, Party Trays, Charcuterie & Gourmet Cheese | Store #2590\" — walmart.com\n\n" +
                        "<!-- SOURCES_JSON -->\n" +
                        "{" +
                        "\"sources\":[" +
                        "{\"url\":\"https://example.com/isabellas-deli\",\"title\":\"Isabella's Deli\",\"domain\":\"example.com\",\"excerpt\":\"Neighborhood deli in Hillsboro, OR.\"}," +
                        "{\"url\":\"https://www.walmart.com/store/2590\",\"title\":\"Walmart Deli in Hillsboro, OR | Grab & Go Sandwiches & Wraps, Party Trays, Charcuterie & Gourmet Cheese | Store #2590\",\"domain\":\"walmart.com\",\"excerpt\":\"Store department page\"}" +
                        "]}",
                    Success = true
                }
            },
            "Can you find me a good deli in Hillsboro, OR?");

        Assert.Contains("Isabella's Deli", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Walmart Deli", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_LocalBusinessBriefingShell_DoesNotUseUnavailablePlacesSeed_OverDirectoryNoise()
    {
        var result = _processor.SanitizeFinalResponse(
            "**Best Delis near Hillsboro, OR**\nVerification recommended\nSources checked: restaurantji.com, tripadvisor.com.\nBriefing summary: hours and review details are based on currently available web sources (2026).",
            new List<ToolCallRecord>
            {
                new()
                {
                    ToolName = "browser_navigate",
                    Arguments = "{\"url\":\"https://www.restaurantji.com/or/hillsboro/deli/\"}",
                    Result = "[browser: title: \"best delis near hillsboro, or - 2026 restaurantji\", content returned]\nDede's Deli\nDandy's Deli",
                    Success = true
                },
                new()
                {
                    ToolName = "places_lookup",
                    Arguments = "{\"query\":\"Isabella's Deli Hillsboro, OR\",\"timezone\":\"America/Los_Angeles\",\"locale\":\"en-US\",\"userLocationHint\":\"Hillsboro, OR\",\"maxReviewSnippets\":1}",
                    Result = "[Places provider unavailable]",
                    Success = false
                },
                new()
                {
                    ToolName = "places_lookup",
                    Arguments = "{\"query\":\"Best Delis near Hillsboro, OR - 2025 Restaurantji\",\"timezone\":\"America/Los_Angeles\",\"locale\":\"en-US\",\"userLocationHint\":\"\",\"maxReviewSnippets\":3}",
                    Result = "[Places provider unavailable]",
                    Success = false
                }
            },
            "Can you find me a good deli in Hillsboro, OR?");

        Assert.Contains("could not confirm a trustworthy deli", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Isabella's Deli", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dede's Deli", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dandy's Deli", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_LocalBusinessBriefingShell_DoesNotUseUnavailablePlacesSeed_WhenPromptSignalIsVague()
    {
        var result = _processor.SanitizeFinalResponse(
            "**Best Delis near Hillsboro, OR**\nVerification recommended\nSources checked: restaurantji.com.\nBriefing summary: hours and review details are based on currently available web sources (2026).",
            new List<ToolCallRecord>
            {
                new()
                {
                    ToolName = "places_lookup",
                    Arguments = "{\"query\":\"Isabella's Deli Hillsboro, OR\",\"timezone\":\"America/Los_Angeles\",\"locale\":\"en-US\",\"userLocationHint\":\"Hillsboro, OR\",\"maxReviewSnippets\":1}",
                    Result = "[Places provider unavailable]",
                    Success = false
                }
            },
            "Can you find me a good one in Hillsboro, OR?");

        Assert.Contains("could not confirm a trustworthy local business", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Isabella's Deli", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_LocalBusinessBriefingShell_DoesNotUseMalformedUnavailablePlacesSeed()
    {
        var result = _processor.SanitizeFinalResponse(
            "**Best Delis near Hillsboro, OR**\nVerification recommended\nSources checked: restaurantji.com.\nBriefing summary: hours and review details are based on currently available web sources (2026).",
            new List<ToolCallRecord>
            {
                new()
                {
                    ToolName = "places_lookup",
                    Arguments = "query=Isabella\\u0027s Deli Hillsboro, OR",
                    Result = "[Places provider unavailable]",
                    Success = false
                }
            },
            "Can you find me a good one in Hillsboro, OR?");

        Assert.Contains("could not confirm a trustworthy local business", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Isabella's Deli", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_ExplicitDeepDiveBriefingShell_DoesNotCollapseToLocalBusinessShortlist()
    {
        const string briefingShell =
            "**Seattle Flowers**\n" +
            "Verification recommended - check the listed source before visiting.\n" +
            "Sources checked: visitseattle.org.\n" +
            "Briefing summary: hours and review details are based on currently available web sources (2026).";

        var result = _processor.SanitizeFinalResponse(
            briefingShell,
            new List<ToolCallRecord>
            {
                new()
                {
                    ToolName = "places_lookup",
                    Arguments = "{\"query\":\"Deep dive Seattle Flowers with hours + reviews and what to expect.\"}",
                    Result = "[Places lookup error: Google Places API key is not configured.]",
                    Success = false
                },
                new()
                {
                    ToolName = "browser_navigate",
                    Arguments = "{\"url\":\"https://visitseattle.org/\"}",
                    Result = "[browser: title: \"visit seattle washington | travel & tourism | official site\", content returned]",
                    Success = true
                }
            },
            "Deep dive Seattle Flowers with hours + reviews and what to expect.");

        Assert.Contains("Verification recommended", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sources checked:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Here are a few local businesses I found nearby", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_LocalBusinessBriefingShell_DoesNotTrustBrowserDirectorySeedAsRecommendation()
    {
        var result = _processor.SanitizeFinalResponse(
            "**Best Delis near Hillsboro, OR**\nVerification recommended\nSources checked: chamberofcommerce.com, restaurantji.com.\nBriefing summary: hours and review details are based on currently available web sources (2026).",
            new List<ToolCallRecord>
            {
                new()
                {
                    ToolName = "browser_navigate",
                    Arguments = "{\"url\":\"https://www.chamberofcommerce.com/business-directory/oregon/hillsboro/food-dining/restaurant/deli/\"}",
                    Result = "[browser: (no title), content returned]\nIsabella's Deli\nNeighborhood deli in Hillsboro, OR.",
                    Success = true
                },
                new()
                {
                    ToolName = "browser_navigate",
                    Arguments = "{\"url\":\"https://www.restaurantji.com/or/hillsboro/deli/\"}",
                    Result = "[browser: title: \"best delis near hillsboro, or - 2026 restaurantji\", content returned]\nDede's Deli\nDandy's Deli",
                    Success = true
                }
            },
            "Can you find me a good deli in Hillsboro, OR?");

        Assert.Contains("could not confirm a trustworthy deli", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Isabella's Deli", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dede's Deli", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("The store closes at 9 PM tonight.", "Is the store open?")]
    [InlineData("Normal assistant fallback.", "What time is it?")]
    [InlineData("Here is a detailed explanation of quantum mechanics.", "Tell me about physics")]
    public void SanitizeFinalResponse_SubstantiveAnswer_PassesThrough(
        string answer, string userMessage)
    {
        var result = _processor.SanitizeFinalResponse(
            answer,
            new List<ToolCallRecord>(),
            userMessage);

        Assert.Equal(answer, result);
    }

    [Theory]
    [InlineData("Yes", "Tell me a joke")]
    [InlineData("No", "Hello there")]
    public void SanitizeFinalResponse_BareAnswerToNonQuestion_PassesThrough(
        string answer, string statement)
    {
        var result = _processor.SanitizeFinalResponse(
            answer,
            new List<ToolCallRecord>(),
            statement);

        // Non-questions shouldn't trigger enrichment
        Assert.Equal(answer, result);
    }
}


public class ChatPostProcessorReasoningBehaviorTests
{
    [Fact]
    public void SanitizeFinalResponse_ConciseWeatherPlan_UsesForecastJsonOverDraftText()
    {
        var processor = new DeterministicChatPostProcessor();
        const string forecastJson = """
            {
              "location": { "name": "39.7392, -104.9849" },
              "current": {
                "temperature": 88,
                "unit": "F",
                "condition": "Slight Chance Showers And Thunderstorms"
              },
              "daily": [
                { "avgTemp": 75 }
              ]
            }
            """;

        var output = processor.SanitizeFinalResponse(
            text: "Denver today: showers, temperature about 91°F now. Plan: bring a layer.",
            toolCallsMade:
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WeatherForecast,
                    Arguments = "{}",
                    Result = forecastJson,
                    Success = true
                }
            ],
            latestUserMessage: "Use weather tools for Denver and provide a concise, useful plan for the day.");

        Assert.Contains("88F", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Denver", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thunderstorms", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provide a concise", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("91", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_ConciseWeatherResult_UsesForecastJsonOverDraftText()
    {
        var processor = new DeterministicChatPostProcessor();
        const string forecastJson = """
            {
              "location": { "name": "Austin, TX" },
              "current": {
                "temperature": 82,
                "unit": "F",
                "condition": "Partly Cloudy"
              },
              "daily": [
                { "avgTemp": 79 }
              ]
            }
            """;

        var output = processor.SanitizeFinalResponse(
            text: "Austin weather: 91F and rainy. Plan: stay indoors.",
            toolCallsMade:
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WeatherForecast,
                    Arguments = "{}",
                    Result = forecastJson,
                    Success = true
                }
            ],
            latestUserMessage: "Use weather tools for Austin, TX and give a concise result.");

        Assert.Contains("Austin", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("82F", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Partly Cloudy", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("91", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Plan:", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_ShortWeatherOutlook_UsesCompactForecastSummary()
    {
        var processor = new DeterministicChatPostProcessor();

        var output = processor.SanitizeFinalResponse(
            text: "Looking ahead, Seattle should be around 70F with a broader weekly outlook.",
            toolCallsMade:
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WeatherForecast,
                    Arguments = "{}",
                    Result = "[Weather forecast: provider=nws, current=56F Sunny]",
                    Success = true
                }
            ],
            latestUserMessage: "Use weather tools to provide a short weather outlook for Seattle, WA.");

        Assert.Contains("Seattle", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("56F", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sunny", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("70", output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "Use web_search for AI policy news and handle timeout gracefully.",
        "{\"error\":{\"code\":\"timeout\",\"message\":\"web_search timed out.\"},\"tool\":\"web_search\",\"stub\":true}",
        "timeout")]
    [InlineData(
        "Use web_search to find the latest Rust language release notes.",
        "{\"error\":{\"code\":\"tool_unavailable\",\"message\":\"web_search is currently unavailable.\"},\"tool\":\"web_search\",\"stub\":true}",
        "unavailable")]
    public void SanitizeFinalResponse_ExplicitWebStructuredFailure_PreservesFailureKeyword(
        string userMessage,
        string toolResult,
        string expectedKeyword)
    {
        var processor = new DeterministicChatPostProcessor();

        var output = processor.SanitizeFinalResponse(
            text: "I reached the tool budget before I could finish.",
            toolCallsMade:
            [
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"test\"}",
                    Result = toolResult,
                    Success = false
                }
            ],
            latestUserMessage: userMessage);

        Assert.Contains(expectedKeyword, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("budget", output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "B) the protein structure and active site can be disrupted, so catalysis drops sharply.",
        "B")]
    [InlineData(
        "The best answer is C) decreases because the resistance rises while voltage is fixed.",
        "C")]
    [InlineData(
        "answer: D - the expected counts are too small for the usual approximation.",
        "D")]
    public void SanitizeFinalResponse_MultipleChoiceLetterOnlyPrompt_CollapsesLeadingChoice(
        string draftText,
        string expected)
    {
        var processor = new DeterministicChatPostProcessor();

        var output = processor.SanitizeFinalResponse(
            text: draftText,
            toolCallsMade: [],
            latestUserMessage: "Choose the best answer. Reply with only A, B, C, or D.");

        Assert.Equal(expected, output);
    }

    [Fact]
    public void ProcessChatOnlyDraft_MultipleChoiceLetterOnlyPrompt_CollapsesLeadingChoice()
    {
        var processor = new DeterministicChatPostProcessor();

        var output = processor.ProcessChatOnlyDraft(
            draftText: "A. The transfer is most consistent with direct heat conduction.",
            userMessage: "Choose the best answer. Reply with only A, B, C, or D.",
            toolCallsMade: []);

        Assert.Equal("A", output);
    }

    [Theory]
    [InlineData(
        "Select the best option. The membrane effect is: A) wrong B) larger because capacitances add in parallel C) no D) no. Final answer only: A, B, C, or D.",
        "B) larger because capacitances add in parallel.",
        "B")]
    [InlineData(
        "A row has 6 labeled boxes. Exactly 4 boxes must receive a marker. How many different marker placements are possible? Give only the final integer.",
        "The number of ways to choose 4 boxes out of 6 is C(6,4), which equals 15. So there are 15 possible marker placements.",
        "15")]
    [InlineData(
        "Among people flagged positive, what fraction actually have K? Give only the decimal.",
        "True positives are 18.9 and false positives are 18.9, so the answer is 0.5.",
        "0.5")]
    [InlineData(
        "A function should pair adjacent values. What expression should replace []? Give only the expression.",
        "The expression is `[(items[i], items[i + 1]) for i in range(len(items) - 1)]`.",
        "[(items[i], items[i + 1]) for i in range(len(items) - 1)]")]
    public void StrictAnswerOnlyPrompt_CollapsesToRequestedSurfaceForm(
        string userMessage,
        string draftText,
        string expected)
    {
        var output = DeterministicChatPostProcessor.TryNormalizeStrictAnswerOnlyReply(userMessage, draftText);

        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData(
        "Answer this benchmark item through Sir Thaddeus. Work briefly if useful. Put the final answer on its own line as `Final answer: <answer>`.",
        "After considering the options, the answer is (G).",
        "Final answer: G")]
    [InlineData(
        "Answer this benchmark item through Sir Thaddeus. Work briefly if useful. Put the final answer on its own line as `Final answer: <answer>`.",
        "2 + 2 = 4.\n\nD",
        "Final answer: D")]
    [InlineData(
        "Answer this benchmark item through Sir Thaddeus. Put the final answer on its own line as Final answer: <answer>.",
        "Answer: D - the expected counts are too small.",
        "Final answer: D")]
    [InlineData(
        "Answer this benchmark item through Sir Thaddeus. Put the final answer on its own line as `Final answer: <answer>`.",
        "The arithmetic gives \\boxed{004}.",
        "Final answer: 004")]
    public void ExplicitFinalAnswerLinePrompt_CollapsesToFinalAnswerLine(
        string userMessage,
        string draftText,
        string expected)
    {
        var output = DeterministicChatPostProcessor.TryNormalizeStrictAnswerOnlyReply(userMessage, draftText);

        Assert.Equal(expected, output);
    }

    [Fact]
    public void StrictJsonOnlyPrompt_ExtractsBalancedJsonObject()
    {
        var output = DeterministicChatPostProcessor.TryNormalizeStrictAnswerOnlyReply(
            "Return only a JSON object selecting the first tool to use.",
            "Sure:\n{\"tool\":\"notes_search\",\"args\":{\"query\":\"launch blocker list\"}}\nDone.");

        Assert.Equal("{\"tool\":\"notes_search\",\"args\":{\"query\":\"launch blocker list\"}}", output);
    }

    [Fact]
    public void SanitizeFinalResponse_StripsEchoedInternalPromptBlocks()
    {
        var processor = new DeterministicChatPostProcessor();
        const string request = "Write a plot for a story about two people who swap fingerprints.";

        var output = processor.SanitizeFinalResponse(
            text: request + "\n\n" +
                  "[Task:task.instructions]\n" +
                  "Today's date is Thursday, June 25, 2026 (2026-06-25).\n" +
                  "You have access to tools that can interact with the user's computer.\n\n" +
                  "[Personality:personality.sir_thaddeus]\n" +
                  "Profile id: sir_thaddeus\n" +
                  "Profile hash: abc123\n" +
                  "Core identity: Witty, pragmatic, and calm guide.\n\n" +
                  "<<The Fingerprint Exchange>>\n" +
                  "Two strangers trade identities through a biometric mix-up and must decide whether truth or reinvention matters more.",
            toolCallsMade: [],
            latestUserMessage: request);

        Assert.Contains(request, output, StringComparison.Ordinal);
        Assert.Contains("<<The Fingerprint Exchange>>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[Task:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Profile hash", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tools that can interact", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_MultipleChoiceLetterOnlyPrompt_DoesNotCollapseNonChoiceLead()
    {
        var processor = new DeterministicChatPostProcessor();

        var output = processor.SanitizeFinalResponse(
            text: "C# would be the wrong language clue here; the answer needs the physics option.",
            toolCallsMade: [],
            latestUserMessage: "Choose the best answer. Reply with only A, B, C, or D.");

        Assert.Equal("C# would be the wrong language clue here; the answer needs the physics option.", output);
    }

    [Fact]
    public void SanitizeFinalResponse_MultipleChoiceExplanationWithoutStrictPrompt_PassesThrough()
    {
        var processor = new DeterministicChatPostProcessor();

        var output = processor.SanitizeFinalResponse(
            text: "B) the enzyme loses active-site structure at the wrong pH.",
            toolCallsMade: [],
            latestUserMessage: "Which option is best?");

        Assert.Equal("B) the enzyme loses active-site structure at the wrong pH.", output);
    }

    [Fact]
    public void ProcessChatOnlyDraft_DoesNotApplyCarWashHardcodedOverride()
    {
        var processor = new DeterministicChatPostProcessor();

        var output = processor.ProcessChatOnlyDraft(
            draftText: "Walk.",
            userMessage: "The car wash is 50m away. Should I walk, or drive?",
            toolCallsMade: []);

        Assert.Equal("Walk.", output);
        Assert.DoesNotContain("<think>", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessChatOnlyDraft_UsesBenignRecovery_ForHashTablePrompt_WhenDraftIsOffTopicMath()
    {
        var processor = new DeterministicChatPostProcessor();

        var output = processor.ProcessChatOnlyDraft(
            draftText: "3 + 5 = 8. Want me to calculate the next one?",
            userMessage: "What is a hash table and when should I use one?",
            toolCallsMade: []);

        Assert.Contains("hash table", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key-value", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("respectful", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessChatOnlyDraft_UsesBenignRecovery_ForHashTablePrompt_WhenDraftLeaksToolingEssay()
    {
        var processor = new DeterministicChatPostProcessor();

        var output = processor.ProcessChatOnlyDraft(
            draftText: "I do best on your machine when we tackle a clear question step by step and inspect the local tools carefully.",
            userMessage: "What is a hash table and when should I use one?",
            toolCallsMade: []);

        Assert.Contains("hash table", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key-value", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("favorites", output, StringComparison.OrdinalIgnoreCase);
    }
}

public class SearchResponseFormatterTests
{
    [Fact]
    public void Normalize_RemovesBriefingUiLeak_AndNormalizesInterWordApostrophes()
    {
        var input =
            "**McDonald's**\n" +
            "Details from web sources\n" +
            "Open the **Briefing** tab for full details, reviews, and sources.\n";

        var output = SearchResponseFormatter.Normalize(input);

        Assert.Contains("**McDonalds**", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Briefing", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_CollapsesExcessBlankLines()
    {
        var input = "Line one\n\n\nLine two\n";

        var output = SearchResponseFormatter.Normalize(input);

        Assert.Equal("Line one\n\nLine two", output);
    }
}
