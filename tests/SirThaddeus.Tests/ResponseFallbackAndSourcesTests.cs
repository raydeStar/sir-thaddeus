using SirThaddeus.Agent;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.Search;
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
            "What would be the plot of Episode 1 of Season 3 of Stargate Universe about?");

        Assert.NotNull(response);
        Assert.Contains("Season 3 Episode 1", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stargate Universe", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not have an official", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no real episode plot", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("couldn't generate a clean summary", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWebSearchUnavailable_UsesBestEffortReasoning()
    {
        var llm = new StubLlmClient(
            "Trader Joe's locations often open early, commonly around 8:00-9:00 AM, but verify directly.");
        var mcp = new StubMcpClient(
            "{\"error\":{\"code\":\"tool_unavailable\",\"message\":\"web search unavailable\"}}");
        var orchestrator = new SearchOrchestrator(
            llm,
            mcp,
            new StubAuditLogger(),
            "You are a concise assistant.");

        var history = new List<ChatMessage>
        {
            ChatMessage.User("When does Trader Joe's in Portland OR open?")
        };
        var toolCalls = new List<ToolCallRecord>();

        var response = await orchestrator.ExecuteAsync(
            userMessage: "When does Trader Joe's in Portland OR open?",
            memoryPackText: "",
            history: history,
            toolCallsMade: toolCalls,
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("Live web lookup is unavailable right now", response.Text, StringComparison.Ordinal);
        Assert.Contains("best-effort answer", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Trader Joe", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWebSearchReturnsNoResults_DoesNotOpenBrowserFallback()
    {
        var llm = new StubLlmClient(
            "I cannot verify live web facts for this question right now, so any answer may be incomplete or out of date.");
        var mcp = new TrackingStubMcpClient("No results found for C# 13 changes");
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
        Assert.Contains("cannot verify live web facts", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, call =>
            call.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase));
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
    public void SanitizeFinalResponse_CarWashPrompt_WithLocalBusinessContamination_UsesDeterministicFallback()
    {
        var result = _processor.SanitizeFinalResponse(
            "Given that McDonalds at 850 University Blvd is currently open and serves until 11 PM tonight, let us focus on the task at hand: getting to the car wash.",
            new List<ToolCallRecord>(),
            "You're going to the car wash and it's only 50 meters away. Should you walk or drive?");

        Assert.Contains("Drive", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("car wash", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("McDonalds", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFinalResponse_StargatePrompt_WithOffTopicProjectResults_UsesNonexistentEpisodeFallback()
    {
        var result = _processor.SanitizeFinalResponse(
            "Here's the strongest evidence I found in the live results:\n- OpenAI's first data center in $500 billion Stargate project is open in Texas.",
            new List<ToolCallRecord>(),
            "What would be the plot of Episode 1 of Season 3 of Stargate Universe about?");

        Assert.Contains("Stargate Universe", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Season 3 Episode 1", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenAI", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data center", result, StringComparison.OrdinalIgnoreCase);
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
