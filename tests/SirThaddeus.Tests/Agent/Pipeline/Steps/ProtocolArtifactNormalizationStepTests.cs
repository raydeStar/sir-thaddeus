using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public sealed class ProtocolArtifactNormalizationStepTests
{
    [Fact]
    public async Task Applies_normalization_and_emits_content_free_activation()
    {
        var prior = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        var logs = new List<(string Action, string Message)>();
        try
        {
            Environment.SetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE", "1");
            var step = new ProtocolArtifactNormalizationStep(
                log: (action, message) => logs.Add((action, message)));
            var context = new TurnContext
            {
                ThreadId = "thread",
                MessageId = "message",
                UserText = "Reply with the token.",
                AssistantDraft = "<|channel>thought\n<channel|>KITE-348",
            };

            var result = await step.ExecuteAsync(context, CancellationToken.None);

            var next = Assert.IsType<StepResult.Continue>(result).Next;
            Assert.Equal("KITE-348", next.AssistantDraft);
            var activation = Assert.Single(logs);
            Assert.Equal("EXPERIMENT_ACTIVATION", activation.Action);
            Assert.Contains("decision=activated", activation.Message, StringComparison.Ordinal);
            Assert.Contains("reason=channel-markers-stripped", activation.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("KITE-348", activation.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE", prior);
        }
    }
}
