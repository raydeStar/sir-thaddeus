using SirThaddeus.Agent.Lanes;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class ExplainLaneTests
{
    [Fact]
    public void ParseExplainRequest_ValidJson_ReturnsRequest()
    {
        var json = """
            {"Topic": "photosynthesis", "Goal": "explain", "Context": "how it works"}
            """;

        var result = ExplainLane.ParseExplainRequest(json);

        Assert.NotNull(result);
        Assert.Equal("photosynthesis", result.Topic);
        Assert.Equal("explain", result.Goal);
        Assert.Equal("how it works", result.Context);
    }

    [Fact]
    public void ParseExplainRequest_CamelCase_Works()
    {
        var json = """{"topic": "Rust ownership model", "goal": "summary", "context": null}""";

        var result = ExplainLane.ParseExplainRequest(json);

        Assert.NotNull(result);
        Assert.Equal("Rust ownership model", result.Topic);
        Assert.Equal("summarize", result.Goal);
        Assert.Null(result.Context);
    }

    [Fact]
    public void ParseExplainRequest_FencedJson_Works()
    {
        var json = """
            ```json
            {"Topic": "TCP three-way handshake", "Goal": "explain", "Context": null}
            ```
            """;

        var result = ExplainLane.ParseExplainRequest(json);

        Assert.NotNull(result);
        Assert.Equal("TCP three-way handshake", result.Topic);
    }

    [Fact]
    public void ParseExplainRequest_MissingTopic_ReturnsNull()
    {
        var json = """{"Topic": "", "Goal": "explain", "Context": null}""";

        var result = ExplainLane.ParseExplainRequest(json);

        Assert.Null(result);
    }

    [Fact]
    public void ParseExplainRequest_InvalidJson_ReturnsNull()
    {
        Assert.Null(ExplainLane.ParseExplainRequest("not json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseExplainRequest_BlankInput_ReturnsNull(string? input)
    {
        Assert.Null(ExplainLane.ParseExplainRequest(input));
    }

    [Fact]
    public void NeedsClarification_NullRequest_True()
    {
        Assert.True(ExplainLane.NeedsClarification(null));
    }

    [Fact]
    public void NeedsClarification_UnknownTopic_True()
    {
        var request = new ExplainRequest { Topic = "unknown" };
        Assert.True(ExplainLane.NeedsClarification(request));
    }

    [Fact]
    public void NeedsClarification_ReferentialTopic_True()
    {
        var request = new ExplainRequest { Topic = "this page" };
        Assert.True(ExplainLane.NeedsClarification(request));
    }

    [Fact]
    public void NeedsClarification_ConcreteTopic_False()
    {
        var request = new ExplainRequest { Topic = "photosynthesis", Goal = "explain" };
        Assert.False(ExplainLane.NeedsClarification(request));
    }

    [Theory]
    [InlineData("What is this PDF about?", "page or document")]
    [InlineData("Can you explain this?", "What specifically")]
    [InlineData("help", "What would you like me to explain")]
    public void BuildClarifyingQuestion_UsesHelpfulPrompt(string input, string expected)
    {
        var result = ExplainLane.BuildClarifyingQuestion(input);
        Assert.Contains(expected, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSearchQuery_TopicOnly()
    {
        var request = new ExplainRequest { Topic = "TCP three-way handshake", Goal = "explain" };

        var query = ExplainLane.BuildSearchQuery(request);

        Assert.Equal("TCP three-way handshake", query);
    }

    [Fact]
    public void BuildSearchQuery_WithContext()
    {
        var request = new ExplainRequest
        {
            Topic = "Rust ownership model",
            Goal = "summarize",
            Context = "beginner friendly"
        };

        var query = ExplainLane.BuildSearchQuery(request);

        Assert.Equal("Rust ownership model beginner friendly", query);
    }

    [Fact]
    public void BuildExplainPrompt_IncludesRequestDetails()
    {
        var request = new ExplainRequest
        {
            Topic = "photosynthesis",
            Goal = "explain",
            Context = "for beginners"
        };

        var prompt = ExplainLane.BuildExplainPrompt("Explain photosynthesis", request);

        Assert.Contains("photosynthesis", prompt);
        Assert.Contains("for beginners", prompt);
        Assert.Contains("Goal: explain", prompt);
    }

    [Fact]
    public void BuildSearchFormatPrompt_IncludesSummary()
    {
        var request = new ExplainRequest
        {
            Topic = "Nvidia earnings",
            Goal = "summarize"
        };

        var prompt = ExplainLane.BuildSearchFormatPrompt(
            "Summarize Nvidia earnings",
            request,
            "Nvidia reported revenue growth according to Reuters.");

        Assert.Contains("Nvidia earnings", prompt);
        Assert.Contains("Reuters", prompt);
    }

    [Fact]
    public async Task ExtractRequestAsync_ValidResponse_ReturnsRequest()
    {
        var llm = new FakeLlmClient("""{"Topic": "photosynthesis", "Goal": "explain", "Context": "how it works"}""");
        var lane = new ExplainLane(llm);

        var result = await lane.ExtractRequestAsync("Explain how photosynthesis works");

        Assert.NotNull(result);
        Assert.Equal("photosynthesis", result.Topic);
        Assert.Equal("explain", result.Goal);
    }

    [Fact]
    public async Task ExtractRequestAsync_LlmThrows_ReturnsNull()
    {
        var lane = new ExplainLane(new ThrowingFakeLlmClient());

        var result = await lane.ExtractRequestAsync("Explain TCP");

        Assert.Null(result);
    }

    [Fact]
    public async Task ExplainAsync_ReturnsLlmText()
    {
        var llm = new FakeLlmClient("Photosynthesis converts light into chemical energy.");
        var lane = new ExplainLane(llm);
        var request = new ExplainRequest { Topic = "photosynthesis", Goal = "explain" };

        var result = await lane.ExplainAsync("Explain photosynthesis", request);

        Assert.Contains("Photosynthesis", result);
    }

    [Fact]
    public async Task ExplainAsync_LlmThrows_ReturnsEmpty()
    {
        var lane = new ExplainLane(new ThrowingFakeLlmClient());
        var request = new ExplainRequest { Topic = "photosynthesis", Goal = "explain" };

        var result = await lane.ExplainAsync("Explain photosynthesis", request);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task FormatSearchSummaryAsync_ReturnsFormattedText()
    {
        var llm = new FakeLlmClient("Nvidia reported strong revenue growth, according to Reuters.");
        var lane = new ExplainLane(llm);
        var request = new ExplainRequest { Topic = "Nvidia earnings", Goal = "summarize" };

        var result = await lane.FormatSearchSummaryAsync(
            "Summarize Nvidia earnings",
            request,
            "Reuters says Nvidia reported strong revenue growth.");

        Assert.Contains("Reuters", result);
    }

    [Fact]
    public async Task FormatSearchSummaryAsync_LlmThrows_ReturnsFallback()
    {
        var lane = new ExplainLane(new ThrowingFakeLlmClient());
        var request = new ExplainRequest { Topic = "Nvidia earnings", Goal = "summarize" };

        var result = await lane.FormatSearchSummaryAsync(
            "Summarize Nvidia earnings",
            request,
            "Fallback summary.");

        Assert.Equal("Fallback summary.", result);
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly string _response;

        public FakeLlmClient(string response) => _response = response;

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse { IsComplete = true, Content = _response });

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
            => ChatAsync(messages, tools, cancellationToken);

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake-model");
    }

    private sealed class ThrowingFakeLlmClient : ILlmClient
    {
        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM unavailable");

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM unavailable");

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }
}
