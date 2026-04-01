using SirThaddeus.Agent.Lanes;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class CheckLaneTests
{
    // ── Entity Extraction Parsing ────────────────────────────────────

    [Fact]
    public void ParseEntityExtraction_ValidJson_ReturnsExtraction()
    {
        var json = """
            {"Entity": "Target", "Attribute": "opening hours", "Qualifier": "on Sundays"}
            """;

        var result = CheckLane.ParseEntityExtraction(json);

        Assert.NotNull(result);
        Assert.Equal("Target", result.Entity);
        Assert.Equal("opening hours", result.Attribute);
        Assert.Equal("on Sundays", result.Qualifier);
    }

    [Fact]
    public void ParseEntityExtraction_CamelCase_Works()
    {
        var json = """{"entity": "Costco", "attribute": "return policy", "qualifier": null}""";

        var result = CheckLane.ParseEntityExtraction(json);

        Assert.NotNull(result);
        Assert.Equal("Costco", result.Entity);
        Assert.Equal("return policy", result.Attribute);
        Assert.Null(result.Qualifier);
    }

    [Fact]
    public void ParseEntityExtraction_MarkdownFenced_Works()
    {
        var json = """
            ```json
            {"Entity": "Big Mac", "Attribute": "price", "Qualifier": null}
            ```
            """;

        var result = CheckLane.ParseEntityExtraction(json);

        Assert.NotNull(result);
        Assert.Equal("Big Mac", result.Entity);
        Assert.Equal("price", result.Attribute);
    }

    [Fact]
    public void ParseEntityExtraction_MissingEntity_ReturnsNull()
    {
        var json = """{"Entity": "", "Attribute": "hours", "Qualifier": null}""";

        var result = CheckLane.ParseEntityExtraction(json);

        Assert.Null(result);
    }

    [Fact]
    public void ParseEntityExtraction_MissingAttribute_ReturnsNull()
    {
        var json = """{"Entity": "Target", "Attribute": "", "Qualifier": null}""";

        var result = CheckLane.ParseEntityExtraction(json);

        Assert.Null(result);
    }

    [Fact]
    public void ParseEntityExtraction_InvalidJson_ReturnsNull()
    {
        var result = CheckLane.ParseEntityExtraction("not json");
        Assert.Null(result);
    }

    [Fact]
    public void ParseEntityExtraction_NullInput_ReturnsNull()
    {
        var result = CheckLane.ParseEntityExtraction(null);
        Assert.Null(result);
    }

    [Fact]
    public void ParseEntityExtraction_EmptyInput_ReturnsNull()
    {
        var result = CheckLane.ParseEntityExtraction("");
        Assert.Null(result);
    }

    // ── NeedsClarification ───────────────────────────────────────────

    [Fact]
    public void NeedsClarification_NullExtraction_True()
    {
        Assert.True(CheckLane.NeedsClarification(null));
    }

    [Fact]
    public void NeedsClarification_UnknownEntity_True()
    {
        var extraction = new EntityExtraction
        {
            Entity = "unknown",
            Attribute = "hours"
        };

        Assert.True(CheckLane.NeedsClarification(extraction));
    }

    [Fact]
    public void NeedsClarification_EmptyEntity_True()
    {
        var extraction = new EntityExtraction
        {
            Entity = "",
            Attribute = "hours"
        };

        Assert.True(CheckLane.NeedsClarification(extraction));
    }

    [Fact]
    public void NeedsClarification_ValidExtraction_False()
    {
        var extraction = new EntityExtraction
        {
            Entity = "Target",
            Attribute = "hours"
        };

        Assert.False(CheckLane.NeedsClarification(extraction));
    }

    // ── BuildClarifyingQuestion ──────────────────────────────────────

    [Theory]
    [InlineData("When does that place open?", "Which place")]
    [InlineData("Is the store open?", "Which place")]
    [InlineData("How much is that thing?", "Which product")]
    [InlineData("Is the product available?", "Which product")]
    [InlineData("What about it?", "Could you be more specific")]
    public void BuildClarifyingQuestion_DetectsAmbiguity(string input, string expectedContains)
    {
        var question = CheckLane.BuildClarifyingQuestion(input);
        Assert.Contains(expectedContains, question, StringComparison.OrdinalIgnoreCase);
    }

    // ── BuildSearchQuery ─────────────────────────────────────────────

    [Fact]
    public void BuildSearchQuery_EntityAndAttribute()
    {
        var extraction = new EntityExtraction
        {
            Entity = "Target",
            Attribute = "opening hours"
        };

        var query = CheckLane.BuildSearchQuery(extraction);
        Assert.Equal("Target opening hours", query);
    }

    [Fact]
    public void BuildSearchQuery_WithQualifier()
    {
        var extraction = new EntityExtraction
        {
            Entity = "Target",
            Attribute = "opening hours",
            Qualifier = "on Sundays"
        };

        var query = CheckLane.BuildSearchQuery(extraction);
        Assert.Equal("Target opening hours on Sundays", query);
    }

    [Fact]
    public void BuildSearchQuery_NullQualifier_NoTrailingSpace()
    {
        var extraction = new EntityExtraction
        {
            Entity = "Costco",
            Attribute = "return policy",
            Qualifier = null
        };

        var query = CheckLane.BuildSearchQuery(extraction);
        Assert.Equal("Costco return policy", query);
    }

    // ── BuildFormatPrompt ────────────────────────────────────────────

    [Fact]
    public void BuildFormatPrompt_IncludesAllParts()
    {
        var extraction = new EntityExtraction
        {
            Entity = "Target",
            Attribute = "hours",
            Qualifier = "Sunday"
        };

        var prompt = CheckLane.BuildFormatPrompt(
            "When does Target open on Sunday?",
            extraction,
            "Target opens at 8 AM on Sundays.");

        Assert.Contains("Target", prompt);
        Assert.Contains("hours", prompt);
        Assert.Contains("Sunday", prompt);
        Assert.Contains("Target opens at 8 AM", prompt);
    }

    // ── ExtractEntityAsync with FakeLlmClient ────────────────────────

    [Fact]
    public async Task ExtractEntityAsync_ValidResponse_ReturnsExtraction()
    {
        var llm = new FakeLlmClient(
            """{"Entity": "Target", "Attribute": "opening hours", "Qualifier": "on Sundays"}""");
        var lane = new CheckLane(llm);

        var result = await lane.ExtractEntityAsync("When does Target open on Sundays?");

        Assert.NotNull(result);
        Assert.Equal("Target", result.Entity);
        Assert.Equal("opening hours", result.Attribute);
        Assert.Equal("on Sundays", result.Qualifier);
    }

    [Fact]
    public async Task ExtractEntityAsync_LlmThrows_ReturnsNull()
    {
        var llm = new ThrowingFakeLlmClient();
        var lane = new CheckLane(llm);

        var result = await lane.ExtractEntityAsync("When does Target open?");

        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractEntityAsync_EmptyResponse_ReturnsNull()
    {
        var llm = new FakeLlmClient("");
        var lane = new CheckLane(llm);

        var result = await lane.ExtractEntityAsync("test query");

        Assert.Null(result);
    }

    // ── FormatResponseAsync ──────────────────────────────────────────

    [Fact]
    public async Task FormatResponseAsync_ReturnsFormattedText()
    {
        var llm = new FakeLlmClient("Target opens at 8 AM on Sundays. Source: target.com.");
        var lane = new CheckLane(llm);

        var extraction = new EntityExtraction
        {
            Entity = "Target",
            Attribute = "opening hours",
            Qualifier = "on Sundays"
        };

        var result = await lane.FormatResponseAsync(
            "When does Target open on Sundays?",
            extraction,
            "Search result: Target hours are 8 AM - 10 PM on Sundays.");

        Assert.Contains("Target", result);
        Assert.Contains("Source", result);
    }

    [Fact]
    public async Task FormatResponseAsync_LlmThrows_ReturnsFallback()
    {
        var llm = new ThrowingFakeLlmClient();
        var lane = new CheckLane(llm);

        var extraction = new EntityExtraction
        {
            Entity = "Target",
            Attribute = "hours"
        };

        var result = await lane.FormatResponseAsync(
            "When does Target open?",
            extraction,
            "Fallback search summary.");

        // Should return the raw summary as fallback.
        Assert.Equal("Fallback search summary.", result);
    }

    // ── Test Helpers ─────────────────────────────────────────────────

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
