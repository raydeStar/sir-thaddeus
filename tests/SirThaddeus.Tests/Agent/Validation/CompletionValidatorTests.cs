using SirThaddeus.Agent.Validation;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class CompletionValidatorTests
{
    [Fact]
    public void Heuristic_ExplicitFinalAnswerLineMissing_RequestsTargetedRepair()
    {
        var request =
            "Put the final answer on its own line as `Final answer: <answer>`.\n\n" +
            "What color results from mixing blue and yellow?";

        var result = CompletionValidator.TryValidateHeuristic(
            request,
            "Mixing blue and yellow produces green.");

        Assert.NotNull(result);
        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
        Assert.Contains("labeled final-answer line", result.MissingElement);
        Assert.Contains("Final answer: <answer>", result.SuggestedRepair);
        Assert.False(result.UsedLlm);
    }

    [Fact]
    public void Heuristic_ExplicitFinalAnswerLinePresent_AllowsNormalValidation()
    {
        var request =
            "Put the final answer on its own line as `Final answer: <answer>`.\n\n" +
            "What color results from mixing blue and yellow?";

        var result = CompletionValidator.TryValidateHeuristic(
            request,
            "Mixing blue and yellow produces green.\n\nFinal answer: green");

        Assert.Null(result);
    }

    [Fact]
    public void Heuristic_DoesNotApplyFinalAnswerFormatToOrdinaryRequests()
    {
        var result = CompletionValidator.TryValidateHeuristic(
            "Draft a three-step rollout plan.",
            "First test locally, then canary the change, then monitor production.");

        Assert.Null(result);
    }

    [Fact]
    public void Heuristic_RequestedOptionLetterRejectsProseValue()
    {
        var request =
            "Put the final answer on its own line as `Final answer: <answer>`.\n\n" +
            "Choose the correct letter choice: A. canary B. full deployment";

        var result = CompletionValidator.TryValidateHeuristic(
            request,
            "A canary limits risk.\n\nFinal answer: a canary release");

        Assert.NotNull(result);
        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
        Assert.Contains("exactly one requested option letter", result.MissingElement);
        Assert.Contains("Final answer: <letter>", result.SuggestedRepair);
    }

    [Fact]
    public void Heuristic_RequestedOptionLetterAcceptsSingleLetterForNormalValidation()
    {
        var request =
            "Put the final answer on its own line as `Final answer: <answer>`.\n\n" +
            "Choose the correct letter choice: A. canary B. full deployment";

        var result = CompletionValidator.TryValidateHeuristic(request, "Final answer: A");

        Assert.Null(result);
    }

    // ── Heuristic: Empty response ────────────────────────────────────

    [Fact]
    public void TryValidateHeuristic_EmptyResponse_Fails()
    {
        var result = CompletionValidator.TryValidateHeuristic("What time is it?", "");

        Assert.NotNull(result);
        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
        Assert.Contains("empty", result.MissingElement!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateHeuristic_WhitespaceResponse_Fails()
    {
        var result = CompletionValidator.TryValidateHeuristic("What time is it?", "   ");

        Assert.NotNull(result);
        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
    }

    // ── Heuristic: Question echo ─────────────────────────────────────

    [Fact]
    public void TryValidateHeuristic_QuestionEcho_Fails()
    {
        var result = CompletionValidator.TryValidateHeuristic(
            "What time does Target close?",
            "What time does Target close?");

        Assert.NotNull(result);
        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
        Assert.Contains("echo", result.MissingElement!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateHeuristic_SlightVariantEcho_IsInconclusive()
    {
        // When the echo variant is >30% longer, the heuristic can't decide — defer to LLM.
        var result = CompletionValidator.TryValidateHeuristic(
            "What time does Target close?",
            "What time does Target close? Let me check.");

        Assert.Null(result);
    }

    [Fact]
    public void TryValidateHeuristic_NearExactEcho_Fails()
    {
        // Adding just a period keeps it under the 1.3x threshold.
        var result = CompletionValidator.TryValidateHeuristic(
            "What time does Target close",
            "What time does Target close?");

        Assert.NotNull(result);
        Assert.False(result.Passed);
    }

    // ── Heuristic: Refusal patterns ──────────────────────────────────

    [Theory]
    [InlineData("I can't help with that.")]
    [InlineData("I cannot provide that information.")]
    [InlineData("I'm unable to answer that question.")]
    [InlineData("I am unable to access that data.")]
    [InlineData("Sorry, I can't do that.")]
    [InlineData("Sorry, I cannot help you.")]
    [InlineData("I don't have access to that.")]
    [InlineData("I do not have that information.")]
    public void TryValidateHeuristic_RefusalPatterns_Fail(string response)
    {
        var result = CompletionValidator.TryValidateHeuristic("Tell me the weather", response);

        Assert.NotNull(result);
        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
        Assert.Contains("refusal", result.MissingElement!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Heuristic: Good responses pass ───────────────────────────────

    [Fact]
    public void TryValidateHeuristic_FactualResponse_ReturnsNull()
    {
        var result = CompletionValidator.TryValidateHeuristic(
            "What time does Target close?",
            "Target typically closes at 10:00 PM. Store hours may vary by location.");

        // Null means heuristic is inconclusive — needs LLM validation.
        Assert.Null(result);
    }

    [Fact]
    public void TryValidateHeuristic_DetailedResponse_ReturnsNull()
    {
        var result = CompletionValidator.TryValidateHeuristic(
            "Explain photosynthesis",
            "Photosynthesis is the process by which plants convert light energy into chemical energy. "
            + "They absorb carbon dioxide from the air and water from the soil, using sunlight to "
            + "produce glucose and release oxygen as a byproduct.");

        Assert.Null(result);
    }

    // ── ParseValidationResponse: Valid JSON ──────────────────────────

    [Fact]
    public void ParseValidationResponse_PassedTrue_ReturnsPassed()
    {
        var json = """
            {
              "Passed": true,
              "RepairNeeded": false,
              "MissingElement": null,
              "SuggestedRepair": null
            }
            """;

        var result = CompletionValidator.ParseValidationResponse(json);

        Assert.True(result.Passed);
        Assert.False(result.RepairNeeded);
        Assert.Null(result.MissingElement);
    }

    [Fact]
    public void ParseValidationResponse_Failed_ReturnsFailure()
    {
        var json = """
            {
              "Passed": false,
              "RepairNeeded": true,
              "MissingElement": "Response does not contain actual store hours",
              "SuggestedRepair": "Search for Target store hours and include specific times"
            }
            """;

        var result = CompletionValidator.ParseValidationResponse(json);

        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
        Assert.Contains("store hours", result.MissingElement!);
        Assert.Contains("Search", result.SuggestedRepair!);
    }

    [Fact]
    public void ParseValidationResponse_CamelCase_Works()
    {
        var json = """
            {
              "passed": false,
              "repairNeeded": true,
              "missingElement": "fabricated data",
              "suggestedRepair": "use search results"
            }
            """;

        var result = CompletionValidator.ParseValidationResponse(json);

        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
        Assert.Equal("fabricated data", result.MissingElement);
    }

    [Fact]
    public void ParseValidationResponse_MarkdownFenced_Works()
    {
        var json = """
            ```json
            {
              "Passed": true,
              "RepairNeeded": false,
              "MissingElement": null,
              "SuggestedRepair": null
            }
            ```
            """;

        var result = CompletionValidator.ParseValidationResponse(json);

        Assert.True(result.Passed);
    }

    // ── ParseValidationResponse: Error cases ─────────────────────────

    [Fact]
    public void ParseValidationResponse_Null_FailsOpen()
    {
        var result = CompletionValidator.ParseValidationResponse(null);

        Assert.True(result.Passed);
    }

    [Fact]
    public void ParseValidationResponse_Empty_FailsOpen()
    {
        var result = CompletionValidator.ParseValidationResponse("");

        Assert.True(result.Passed);
    }

    [Fact]
    public void ParseValidationResponse_InvalidJson_FailsOpen()
    {
        var result = CompletionValidator.ParseValidationResponse("not json");

        Assert.True(result.Passed);
    }

    // ── Full ValidateAsync with FakeLlmClient ────────────────────────

    [Fact]
    public async Task ValidateAsync_EmptyResponse_FailsViaHeuristic()
    {
        var llm = new FakeLlmClient("""{"Passed": true}""");
        var validator = new CompletionValidator(llm);

        var result = await validator.ValidateAsync("Tell me the time", "", false);

        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
        Assert.True(result.ElapsedMs >= 0);
    }

    [Fact]
    public async Task ValidateAsync_GoodResponse_PassesViaLlm()
    {
        var llm = new FakeLlmClient("""{"Passed": true, "RepairNeeded": false}""");
        var validator = new CompletionValidator(llm);

        var result = await validator.ValidateAsync(
            "What time does Target open?",
            "Target opens at 8:00 AM and closes at 10:00 PM.",
            true);

        Assert.True(result.Passed);
        Assert.False(result.RepairNeeded);
        Assert.True(result.ElapsedMs >= 0);
    }

    [Fact]
    public async Task ValidateAsync_LlmSaysRepairNeeded_ReturnsRepairNeeded()
    {
        var llm = new FakeLlmClient("""
            {
              "Passed": false,
              "RepairNeeded": true,
              "MissingElement": "retrieved data not used",
              "SuggestedRepair": "include actual search results"
            }
            """);
        var validator = new CompletionValidator(llm);

        var result = await validator.ValidateAsync(
            "What time does Target open?",
            "Target is a retail store chain.",
            true);

        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
        Assert.Equal("retrieved data not used", result.MissingElement);
    }

    [Fact]
    public async Task ValidateAsync_LlmThrows_FailsOpen()
    {
        var llm = new ThrowingFakeLlmClient();
        var validator = new CompletionValidator(llm);

        var result = await validator.ValidateAsync(
            "Test question",
            "Some answer that the LLM would validate but throws instead.",
            false);

        // Fail-open: if validation itself fails, treat as passed.
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ValidateAsync_RefusalResponse_FailsViaHeuristic()
    {
        var llm = new FakeLlmClient("""{"Passed": true}""");
        var validator = new CompletionValidator(llm);

        var result = await validator.ValidateAsync(
            "What's the weather?",
            "I can't help with that.",
            false);

        Assert.False(result.Passed);
        Assert.True(result.RepairNeeded);
    }

    [Fact]
    public async Task ValidateAsync_MeasuresElapsedTime()
    {
        var llm = new FakeLlmClient("""{"Passed": true}""");
        var validator = new CompletionValidator(llm);

        var result = await validator.ValidateAsync(
            "What time is it?",
            "It's currently 3:45 PM.",
            false);

        // ElapsedMs should be non-negative.
        Assert.True(result.ElapsedMs >= 0);
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
            => throw new InvalidOperationException("LLM is unavailable");

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM is unavailable");

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }
}
