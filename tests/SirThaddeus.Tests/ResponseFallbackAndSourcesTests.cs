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

    private sealed class StubLlmClient(string responseText) : ILlmClient
    {
        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = responseText,
                FinishReason = "stop"
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
}
