using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Memory;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using System.Reflection;

namespace SirThaddeus.Tests;

public sealed class HelperModelTimeoutTests
{
    [Fact]
    public async Task SmartIntentClassifier_Timeout_ReturnsUnsure_AndAudits()
    {
        var audit = new TestAuditLogger();
        var classifier = new SmartIntentClassifier(
            new TimeoutThrowingLlmClient(),
            audit,
            timeout: TimeSpan.FromMilliseconds(20));

        var decision = await classifier.ClassifyAsync("Explain how quicksort works");

        Assert.Equal(MemoryIntentDecision.Unsure, decision);

        var timeoutEvent = Assert.Single(audit.GetByAction("MEMORY_CLASSIFIER_TIMEOUT"));
        Assert.Equal("error", timeoutEvent.Result);
        Assert.NotNull(timeoutEvent.Details);
        Assert.Equal(20, timeoutEvent.Details!["timeout_ms"]);
    }

    [Fact]
    public async Task SmartIntentClassifier_CasualSmallTalk_ReturnsSuppress_WithoutCallingLlm()
    {
        var llm = new CountingLlmClient();
        var classifier = new SmartIntentClassifier(llm);

        var decision = await classifier.ClassifyAsync(
            "Hey, how are you doing today? Just wanted to say thanks for helping me out.");

        Assert.Equal(MemoryIntentDecision.Suppress, decision);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task SmartIntentClassifier_TimeUtility_ReturnsSuppress_WithoutCallingLlm()
    {
        var llm = new CountingLlmClient();
        var classifier = new SmartIntentClassifier(llm);

        var decision = await classifier.ClassifyAsync(
            "What time is it right now? Tell me in one sentence.");

        Assert.Equal(MemoryIntentDecision.Suppress, decision);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task SlotExtract_Timeout_FallsBackToHeuristic_AndAudits()
    {
        var audit = new TestAuditLogger();
        var extractor = new SlotExtract(
            new SlowFakeLlmClient(delayMs: 500),
            audit,
            timeout: TimeSpan.FromMilliseconds(20));

        var retryMethod = typeof(SlotExtract).GetMethod(
            "TryExtractWithRetryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(retryMethod);

        var retryTask = (Task<ExtractedSlots?>)retryMethod!.Invoke(
            extractor,
            ["hello world", new DialogueState(), CancellationToken.None])!;

        var helperResult = await retryTask;
        Assert.Null(helperResult);

        var slots = await extractor.RunAsync("hello world", new DialogueState());

        Assert.Equal("none", slots.Intent);
        Assert.Equal("hello world", slots.RawMessage);

        var timeoutEvents = audit.GetByAction("DIALOGUE_SLOT_EXTRACT_FAIL");
        Assert.NotEmpty(timeoutEvents);
        Assert.Contains(timeoutEvents, timeoutEvent =>
            timeoutEvent.Details is not null &&
            timeoutEvent.Details.TryGetValue("strict", out var strictValue) &&
            Equals(strictValue, false));
        Assert.All(timeoutEvents, timeoutEvent =>
        {
            Assert.Equal("error", timeoutEvent.Result);
            Assert.NotNull(timeoutEvent.Details);
            Assert.True(timeoutEvent.Details!.ContainsKey("timed_out"));
            Assert.Equal(true, timeoutEvent.Details["timed_out"]);
        });
    }

    private sealed class TimeoutThrowingLlmClient : ILlmClient
    {
        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
            => throw new OperationCanceledException("Simulated classifier timeout.");

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
            => throw new OperationCanceledException("Simulated classifier timeout.");

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("timeout-throwing-fake");
    }

    private sealed class CountingLlmClient : ILlmClient
    {
        public int CallCount { get; private set; }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = "{\"decision\":\"Unsure\"}",
                FinishReason = "stop"
            });
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = "{\"decision\":\"Unsure\"}",
                FinishReason = "stop"
            });
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("counting-fake");
    }
}
