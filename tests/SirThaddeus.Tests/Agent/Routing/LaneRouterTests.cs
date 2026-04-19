using SirThaddeus.Agent;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class LaneRouterTests
{
    // ── Heuristic: Deterministic ─────────────────────────────────────

    [Theory]
    [InlineData("What's 17% of 340?")]
    [InlineData("15 + 23")]
    [InlineData("convert 5 miles to km")]
    [InlineData("how many cm in a foot")]
    [InlineData("100 * 3.5")]
    public void TryClassifyHeuristic_Deterministic(string input)
    {
        var result = LaneRouter.TryClassifyHeuristic(input);
        Assert.NotNull(result);
        Assert.Equal(TaskLane.Deterministic, result.Lane);
        Assert.True(result.Confidence >= 0.9);
    }

    // ── Heuristic: Explain ───────────────────────────────────────────

    [Theory]
    [InlineData("What is this PDF about?")]
    [InlineData("Summarize this page")]
    [InlineData("Describe what happened")]
    [InlineData("explain how photosynthesis works")]
    [InlineData("is this legit or a scam")]
    public void TryClassifyHeuristic_Explain(string input)
    {
        var result = LaneRouter.TryClassifyHeuristic(input);
        Assert.NotNull(result);
        Assert.Equal(TaskLane.Explain, result.Lane);
        Assert.True(result.Confidence >= 0.9);
    }

    // ── Heuristic: Guide ─────────────────────────────────────────────

    [Theory]
    [InlineData("Walk me through filling out this form")]
    [InlineData("Help me fix this error: NullReferenceException")]
    [InlineData("how do i install Node.js")]
    [InlineData("show me how to bake cookies")]
    [InlineData("guide me through the setup")]
    public void TryClassifyHeuristic_Guide(string input)
    {
        var result = LaneRouter.TryClassifyHeuristic(input);
        Assert.NotNull(result);
        Assert.Equal(TaskLane.Guide, result.Lane);
        Assert.True(result.Confidence >= 0.9);
    }

    // ── Heuristic: Lookup ────────────────────────────────────────────

    [Theory]
    [InlineData("When does Target close tonight?")]
    [InlineData("is this laptop in stock")]
    [InlineData("what is the price of that monitor")]
    [InlineData("how much does a Tesla Model 3 cost")]
    [InlineData("what time does the library open today")]
    public void TryClassifyHeuristic_Lookup(string input)
    {
        var result = LaneRouter.TryClassifyHeuristic(input);
        Assert.NotNull(result);
        Assert.Equal(TaskLane.Lookup, result.Lane);
        Assert.True(result.Confidence >= 0.9);
    }

    // ── Heuristic: Compare ───────────────────────────────────────────

    [Theory]
    [InlineData("Compare these two laptops")]
    [InlineData("which is better, iPhone or Samsung")]
    [InlineData("Is this a good deal?")]
    [InlineData("macbook vs surface pro")]
    [InlineData("compare A vs B")]
    public void TryClassifyHeuristic_Compare(string input)
    {
        var result = LaneRouter.TryClassifyHeuristic(input);
        Assert.NotNull(result);
        Assert.Equal(TaskLane.Compare, result.Lane);
        Assert.True(result.Confidence >= 0.9);
    }

    // ── Heuristic: FileSystem ────────────────────────────────────────

    [Theory]
    [InlineData("Move all my PDFs to a Documents folder")]
    [InlineData("copy my files to the backup drive")]
    [InlineData("organize my files by date")]
    [InlineData("rename the file to report-final.docx")]
    [InlineData("delete the file temp.txt")]
    public void TryClassifyHeuristic_FileSystem(string input)
    {
        var result = LaneRouter.TryClassifyHeuristic(input);
        Assert.NotNull(result);
        Assert.Equal(TaskLane.FileSystem, result.Lane);
        Assert.True(result.Confidence >= 0.9);
    }

    // ── Heuristic: Conversation ──────────────────────────────────────

    [Theory]
    [InlineData("Hey, how are you?")]
    [InlineData("hello")]
    [InlineData("good morning")]
    [InlineData("what's up")]
    public void TryClassifyHeuristic_Conversation(string input)
    {
        var result = LaneRouter.TryClassifyHeuristic(input);
        Assert.NotNull(result);
        Assert.Equal(TaskLane.Conversation, result.Lane);
        Assert.True(result.Confidence >= 0.9);
    }

    [Fact]
    public void TryClassifyHeuristic_EmptyInput_DefaultsToConversation()
    {
        var result = LaneRouter.TryClassifyHeuristic("");
        Assert.NotNull(result);
        Assert.Equal(TaskLane.Conversation, result.Lane);
    }

    [Fact]
    public void TryClassifyHeuristic_WhitespaceInput_DefaultsToConversation()
    {
        var result = LaneRouter.TryClassifyHeuristic("   ");
        Assert.NotNull(result);
        Assert.Equal(TaskLane.Conversation, result.Lane);
    }

    // ── LLM Response Parsing ─────────────────────────────────────────

    [Fact]
    public void ParseLlmResponse_ValidJson_ReturnsCorrectLane()
    {
        var json = """{"lane":"Explain","confidence":0.9,"rationale":"User asked about a PDF"}""";
        var result = LaneRouter.ParseLlmResponse(json);

        Assert.Equal(TaskLane.Explain, result.Lane);
        Assert.Equal(0.9, result.Confidence);
        Assert.Equal("User asked about a PDF", result.Rationale);
    }

    [Fact]
    public void ParseLlmResponse_ValidJson_CaseInsensitiveLane()
    {
        var json = """{"lane":"guide","confidence":0.85,"rationale":"Step-by-step help requested"}""";
        var result = LaneRouter.ParseLlmResponse(json);

        Assert.Equal(TaskLane.Guide, result.Lane);
    }

    [Fact]
    public void ParseLlmResponse_InvalidJson_DefaultsToConversation()
    {
        var result = LaneRouter.ParseLlmResponse("not json at all");

        Assert.Equal(TaskLane.Conversation, result.Lane);
        Assert.Equal(0.5, result.Confidence);
        Assert.Contains("Invalid JSON", result.Rationale);
    }

    [Fact]
    public void ParseLlmResponse_EmptyContent_DefaultsToConversation()
    {
        var result = LaneRouter.ParseLlmResponse(null);

        Assert.Equal(TaskLane.Conversation, result.Lane);
        Assert.Equal(0.5, result.Confidence);
    }

    [Fact]
    public void ParseLlmResponse_LowConfidence_DefaultsToConversation()
    {
        var json = """{"lane":"Lookup","confidence":0.3,"rationale":"Not sure"}""";
        var result = LaneRouter.ParseLlmResponse(json);

        Assert.Equal(TaskLane.Conversation, result.Lane);
        Assert.Equal(0.3, result.Confidence);
        Assert.Contains("Low confidence", result.Rationale);
    }

    [Fact]
    public void ParseLlmResponse_UnknownLane_DefaultsToConversation()
    {
        var json = """{"lane":"InvalidLane","confidence":0.95,"rationale":"Made up lane"}""";
        var result = LaneRouter.ParseLlmResponse(json);

        Assert.Equal(TaskLane.Conversation, result.Lane);
        Assert.Contains("Unrecognised lane", result.Rationale);
    }

    [Fact]
    public void ParseLlmResponse_MarkdownFencedJson_ParsesCorrectly()
    {
        var markdown = """
            ```json
            {"lane":"Compare","confidence":0.88,"rationale":"Comparison requested"}
            ```
            """;
        var result = LaneRouter.ParseLlmResponse(markdown);

        Assert.Equal(TaskLane.Compare, result.Lane);
        Assert.Equal(0.88, result.Confidence);
    }

    [Fact]
    public void ParseLlmResponse_ConfidenceExactlyAtThreshold_ReturnsLane()
    {
        var json = """{"lane":"Lookup","confidence":0.6,"rationale":"Barely confident"}""";
        var result = LaneRouter.ParseLlmResponse(json);

        Assert.Equal(TaskLane.Lookup, result.Lane);
        Assert.Equal(0.6, result.Confidence);
    }

    [Fact]
    public void ParseLlmResponse_ConfidenceJustBelowThreshold_DefaultsToConversation()
    {
        var json = """{"lane":"Lookup","confidence":0.59,"rationale":"Not quite confident"}""";
        var result = LaneRouter.ParseLlmResponse(json);

        Assert.Equal(TaskLane.Conversation, result.Lane);
    }

    // ── Full ClassifyAsync with mock LLM ─────────────────────────────

    [Fact]
    public async Task ClassifyAsync_HeuristicMatch_DoesNotCallLlm()
    {
        var llmCalls = 0;
        var llm = new FakeLlmClient((msgs, _) =>
        {
            llmCalls++;
            return new LlmResponse { IsComplete = true, Content = "{}", FinishReason = "stop" };
        });

        var router = new LaneRouter(llm);
        var result = await router.ClassifyAsync("What's 17% of 340?", ConversationContext.Empty);

        Assert.Equal(TaskLane.Deterministic, result.Lane);
        Assert.Equal(0, llmCalls);
    }

    [Fact]
    public async Task ClassifyAsync_NoHeuristicMatch_FallsBackToLlm()
    {
        var llm = new FakeLlmClient((msgs, _) => new LlmResponse
        {
            IsComplete = true,
            Content = """{"lane":"Explain","confidence":0.92,"rationale":"Asking about a concept"}""",
            FinishReason = "stop"
        });

        var router = new LaneRouter(llm);
        // This input is ambiguous enough that heuristics won't catch it
        var result = await router.ClassifyAsync(
            "Can you break down the implications of quantum entanglement for everyday computing?",
            ConversationContext.Empty);

        Assert.Equal(TaskLane.Explain, result.Lane);
    }

    [Fact]
    public async Task ClassifyAsync_LlmReturnsInvalidJson_DefaultsToConversation()
    {
        var llm = new FakeLlmClient((msgs, _) => new LlmResponse
        {
            IsComplete = true,
            Content = "I don't understand the format you want",
            FinishReason = "stop"
        });

        var router = new LaneRouter(llm);
        var result = await router.ClassifyAsync(
            "blah blah undefined input",
            ConversationContext.Empty);

        Assert.Equal(TaskLane.Conversation, result.Lane);
    }

    [Fact]
    public async Task ClassifyAsync_LlmThrows_DefaultsToConversation()
    {
        var llm = new FakeLlmClient((msgs, _) =>
            throw new InvalidOperationException("LLM is down"));

        var router = new LaneRouter(llm);
        var result = await router.ClassifyAsync(
            "some input that needs LLM classification but LLM is broken",
            ConversationContext.Empty);

        Assert.Equal(TaskLane.Conversation, result.Lane);
        Assert.True(result.Confidence <= 0.5);
    }

    [Fact]
    public async Task ClassifyAsync_MeasuresElapsedMs()
    {
        var llm = new FakeLlmClient((msgs, _) => new LlmResponse
        {
            IsComplete = true,
            Content = """{"lane":"Guide","confidence":0.85,"rationale":"Help requested"}""",
            FinishReason = "stop"
        });

        var router = new LaneRouter(llm);
        var result = await router.ClassifyAsync("help me with X", ConversationContext.Empty);

        Assert.True(result.ElapsedMs >= 0);
    }

    // ── E2E test scenario table from the Notion task ─────────────────
    // These test the heuristic layer against the task's required mappings.

    [Theory]
    [InlineData("What is this PDF about?", TaskLane.Explain)]
    [InlineData("Walk me through filling out this form", TaskLane.Guide)]
    [InlineData("When does Target close tonight?", TaskLane.Lookup)]
    [InlineData("Compare these two laptops", TaskLane.Compare)]
    [InlineData("Move all my PDFs to a Documents folder", TaskLane.FileSystem)]
    [InlineData("What's 17% of 340?", TaskLane.Deterministic)]
    [InlineData("Hey, how are you?", TaskLane.Conversation)]
    [InlineData("Help me fix this error: NullReferenceException", TaskLane.Guide)]
    [InlineData("Is this a good deal?", TaskLane.Compare)]
    [InlineData("Summarize this page", TaskLane.Explain)]
    public void E2EScenarios_HeuristicsMatchExpectedLane(string input, TaskLane expected)
    {
        var result = LaneRouter.TryClassifyHeuristic(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Lane);
    }
}
