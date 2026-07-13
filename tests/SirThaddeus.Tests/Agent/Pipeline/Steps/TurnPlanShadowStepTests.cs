using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Tests.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

[Collection(RoutingLatencyEnvironmentCollection.Name)]
public sealed class TurnPlanShadowStepTests
{
    [Fact]
    public async Task Disabled_step_is_a_noop_and_emits_nothing()
    {
        using var env = new EnvironmentScope("ST_TURN_PLAN_SHADOW", null);
        var events = new List<(string action, string message)>();
        var step = new TurnPlanShadowStep((action, message) => events.Add((action, message)));
        var context = Context("hello");

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        var next = Assert.IsType<StepResult.Continue>(result).Next;
        Assert.Same(context, next);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Enabled_step_logs_capabilities_without_prompt_contents()
    {
        using var env = new EnvironmentScope("ST_TURN_PLAN_SHADOW", "1");
        var events = new List<(string action, string message)>();
        var step = new TurnPlanShadowStep((action, message) => events.Add((action, message)));
        const string privateText = "What do you remember about my preferences secret-token-123?";

        await step.ExecuteAsync(Context(privateText), CancellationToken.None);

        var entry = Assert.Single(events);
        Assert.Equal("TURN_PLAN_SHADOW", entry.action);
        Assert.Contains("thread_id=thread-1", entry.message, StringComparison.Ordinal);
        Assert.Contains("turn_id=turn-1", entry.message, StringComparison.Ordinal);
        Assert.Contains("dynamic_memory", entry.message, StringComparison.Ordinal);
        Assert.DoesNotContain(privateText, entry.message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-123", entry.message, StringComparison.Ordinal);
    }

    private static TurnContext Context(string text) => new()
    {
        ThreadId = "thread-1",
        MessageId = "turn-1",
        UserText = text,
        Features = RoutingFeatures.Extract(text)
    };

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
