using SirThaddeus.Agent;
using SirThaddeus.Agent.Validation;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class RepairLoopTests
{
    // ── BuildRepairPrompt ────────────────────────────────────────────

    [Fact]
    public void BuildRepairPrompt_IncludesAllComponents()
    {
        var validation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = "Response does not include store hours",
            SuggestedRepair = "Include actual store hours from search results"
        };

        var prompt = RepairLoop.BuildRepairPrompt(
            "What time does Target close?",
            "Target is a retail store.",
            validation);

        Assert.Contains("What time does Target close?", prompt);
        Assert.Contains("Target is a retail store.", prompt);
        Assert.Contains("store hours", prompt);
        Assert.Contains("Fix only this specific issue", prompt);
    }

    [Fact]
    public void BuildRepairPrompt_NullMissingElement_UsesFallback()
    {
        var validation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = null,
            SuggestedRepair = null
        };

        var prompt = RepairLoop.BuildRepairPrompt("question", "answer", validation);

        Assert.Contains("did not adequately answer", prompt);
    }

    [Fact]
    public void BuildMultipleChoiceLetterRepairPrompt_RequiresOneLabeledLetter()
    {
        var prompt = RepairLoop.BuildMultipleChoiceLetterRepairPrompt(
            "Choose the correct letter answer. A. canary B. full deployment",
            "A canary is safer.");

        Assert.Contains("A. canary B. full deployment", prompt);
        Assert.Contains("A canary is safer.", prompt);
        Assert.Contains("Final answer: <LETTER>", prompt);
        Assert.Contains("Output no explanation", prompt);
    }

    [Fact]
    public async Task TryRepairAsync_MultipleChoiceContract_AdoptsExactLetterWithoutGenericRevalidation()
    {
        var llm = new SequentialFakeLlmClient("Final answer: A");
        var validator = new CompletionValidator(llm);
        var loop = new RepairLoop(llm, validator) { MaxAttempts = 1 };
        var request =
            "Put the final answer on its own line as `Final answer: <answer>`.\n\n" +
            "Choose the correct letter answer. A. canary B. full deployment";
        var failedValidation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = "Response did not use one option letter."
        };

        var result = await loop.TryRepairAsync(
            request,
            "A canary is safer.",
            failedValidation,
            Array.Empty<ToolCallRecord>());

        Assert.True(result.Repaired);
        Assert.Equal("Final answer: A", result.FinalText);
        Assert.Single(result.Attempts);
    }

    // ── TryRepairAsync: Repair succeeds ──────────────────────────────

    [Fact]
    public async Task TryRepairAsync_RepairSucceeds_ReturnsRepairedText()
    {
        // Repair LLM returns a good answer, validation LLM says it passes.
        var llm = new SequentialFakeLlmClient(
            // Repair call
            "Target closes at 10:00 PM.",
            // Validation call for the repaired response
            """{"Passed": true, "RepairNeeded": false}"""
        );
        var validator = new CompletionValidator(llm);
        var loop = new RepairLoop(llm, validator) { MaxAttempts = 1 };

        var failedValidation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = "No store hours included",
            SuggestedRepair = "Include actual hours"
        };

        var result = await loop.TryRepairAsync(
            "What time does Target close?",
            "Target is a retail store.",
            failedValidation,
            Array.Empty<ToolCallRecord>());

        Assert.True(result.Repaired);
        Assert.Equal("Target closes at 10:00 PM.", result.FinalText);
        Assert.Single(result.Attempts);
        Assert.True(result.Attempts[0].RepairSucceeded);
    }

    // ── TryRepairAsync: Repair also fails ────────────────────────────

    [Fact]
    public async Task TryRepairAsync_RepairAlsoFails_ReturnsFalse()
    {
        var llm = new SequentialFakeLlmClient(
            // Repair call returns another bad answer
            "I can't help with that.",
            // Validation of repaired response (won't be needed — heuristic catches refusal)
            """{"Passed": false}"""
        );
        var validator = new CompletionValidator(llm);
        var loop = new RepairLoop(llm, validator) { MaxAttempts = 1 };

        var failedValidation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = "No answer provided"
        };

        var result = await loop.TryRepairAsync(
            "What time does Target close?",
            "Some inadequate response.",
            failedValidation,
            Array.Empty<ToolCallRecord>());

        Assert.False(result.Repaired);
        Assert.Equal("Some inadequate response.", result.FinalText);
        Assert.Single(result.Attempts);
        Assert.False(result.Attempts[0].RepairSucceeded);
    }

    // ── TryRepairAsync: Exactly 1 attempt enforced ───────────────────

    [Fact]
    public async Task TryRepairAsync_MaxAttempts1_NeverRetriesAfterFirstFailure()
    {
        var llm = new SequentialFakeLlmClient(
            // Repair call
            "Still not a good answer about store hours.",
            // Validation says still fails
            """{"Passed": false, "RepairNeeded": true, "MissingElement": "still missing"}"""
        );
        var validator = new CompletionValidator(llm);
        var loop = new RepairLoop(llm, validator) { MaxAttempts = 1 };

        var failedValidation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = "Missing hours"
        };

        var result = await loop.TryRepairAsync(
            "What time?",
            "Original bad response.",
            failedValidation,
            Array.Empty<ToolCallRecord>());

        // Exactly 1 attempt, no more.
        Assert.Single(result.Attempts);
        Assert.False(result.Repaired);
    }

    // ── TryRepairAsync: No repair when validator passes first try ────

    [Fact]
    public async Task TryRepairAsync_MaxAttemptsZero_NoRepairAttemptMade()
    {
        var llm = new FakeLlmClient("should not be called");
        var validator = new CompletionValidator(llm);
        var loop = new RepairLoop(llm, validator) { MaxAttempts = 0 };

        var failedValidation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = "test"
        };

        var result = await loop.TryRepairAsync(
            "Q", "A", failedValidation, Array.Empty<ToolCallRecord>());

        Assert.False(result.Repaired);
        Assert.Empty(result.Attempts);
    }

    // ── TryRepairAsync: LLM throws → fail-safe ──────────────────────

    [Fact]
    public async Task TryRepairAsync_LlmThrows_ReturnsFalseGracefully()
    {
        var llm = new ThrowingFakeLlmClient();
        var validator = new CompletionValidator(llm);
        var loop = new RepairLoop(llm, validator) { MaxAttempts = 1 };

        var failedValidation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = "test"
        };

        var result = await loop.TryRepairAsync(
            "Q", "Original.", failedValidation, Array.Empty<ToolCallRecord>());

        Assert.False(result.Repaired);
        Assert.Equal("Original.", result.FinalText);
        Assert.Single(result.Attempts);
        Assert.False(result.Attempts[0].RepairSucceeded);
    }

    // ── TryRepairAsync: Empty repair response → fail ─────────────────

    [Fact]
    public async Task TryRepairAsync_EmptyRepairResponse_Fails()
    {
        var llm = new FakeLlmClient("");
        var validator = new CompletionValidator(llm);
        var loop = new RepairLoop(llm, validator) { MaxAttempts = 1 };

        var failedValidation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = "empty"
        };

        var result = await loop.TryRepairAsync(
            "Q", "Original.", failedValidation, Array.Empty<ToolCallRecord>());

        Assert.False(result.Repaired);
        Assert.Single(result.Attempts);
        Assert.False(result.Attempts[0].RepairSucceeded);
        Assert.Null(result.Attempts[0].RepairedText);
    }

    // ── TryRepairAsync: Attempt tracking ─────────────────────────────

    [Fact]
    public async Task TryRepairAsync_LogsAttemptDetails()
    {
        var llm = new SequentialFakeLlmClient(
            "Repaired: Target closes at 10 PM.",
            """{"Passed": true}"""
        );
        var validator = new CompletionValidator(llm);
        var loop = new RepairLoop(llm, validator) { MaxAttempts = 1 };

        var failedValidation = new CompletionValidationResult
        {
            Passed = false,
            RepairNeeded = true,
            MissingElement = "Missing closing time"
        };

        var result = await loop.TryRepairAsync(
            "When does Target close?",
            "Target is a store.",
            failedValidation,
            Array.Empty<ToolCallRecord>());

        var attempt = Assert.Single(result.Attempts);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("Missing closing time", attempt.FailureReason);
        Assert.Contains("Target is a store.", attempt.RepairPrompt);
        Assert.Equal("Repaired: Target closes at 10 PM.", attempt.RepairedText);
        Assert.True(attempt.RepairSucceeded);
        Assert.True(attempt.ElapsedMs >= 0);
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

    private sealed class SequentialFakeLlmClient : ILlmClient
    {
        private readonly string[] _responses;
        private int _index;

        public SequentialFakeLlmClient(params string[] responses) => _responses = responses;

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken cancellationToken = default)
        {
            var content = _index < _responses.Length ? _responses[_index++] : "";
            return Task.FromResult(new LlmResponse { IsComplete = true, Content = content });
        }

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
