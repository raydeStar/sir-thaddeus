using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Memory;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Tests;

public sealed class HelperModelTimeoutTests
{
    [Fact]
    public async Task SmartIntentClassifier_Timeout_ReturnsUnsure_AndAudits()
    {
        var audit = new TestAuditLogger();
        var classifier = new SmartIntentClassifier(
            new SlowFakeLlmClient(delayMs: 500),
            audit,
            timeout: TimeSpan.FromMilliseconds(20));

        var decision = await classifier.ClassifyAsync("hello world");

        Assert.Equal(MemoryIntentDecision.Unsure, decision);

        var timeoutEvent = Assert.Single(audit.GetByAction("MEMORY_CLASSIFIER_TIMEOUT"));
        Assert.Equal("error", timeoutEvent.Result);
        Assert.NotNull(timeoutEvent.Details);
        Assert.Equal(20, timeoutEvent.Details!["timeout_ms"]);
    }

    [Fact]
    public async Task SlotExtract_Timeout_FallsBackToHeuristic_AndAudits()
    {
        var audit = new TestAuditLogger();
        var extractor = new SlotExtract(
            new SlowFakeLlmClient(delayMs: 500),
            audit,
            timeout: TimeSpan.FromMilliseconds(20));

        var slots = await extractor.RunAsync("hello world", new DialogueState());

        Assert.Equal("none", slots.Intent);
        Assert.Equal("hello world", slots.RawMessage);

        var timeoutEvents = audit.GetByAction("DIALOGUE_SLOT_EXTRACT_FAIL");
        Assert.Equal(2, timeoutEvents.Count);
        Assert.All(timeoutEvents, timeoutEvent =>
        {
            Assert.Equal("error", timeoutEvent.Result);
            Assert.NotNull(timeoutEvent.Details);
            Assert.True(timeoutEvent.Details!.ContainsKey("timed_out"));
            Assert.Equal(true, timeoutEvent.Details["timed_out"]);
        });
    }
}
