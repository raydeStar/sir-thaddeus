using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline;

[Collection(RoutingLatencyEnvironmentCollection.Name)]
public class ChatPipelineTests
{
    [Fact]
    public async Task Runs_steps_in_declared_order_and_threads_context()
    {
        var calls = new List<string>();
        var step1 = new RecordingStep("one", calls, ctx =>
            new StepResult.Continue(ctx with { UserText = ctx.UserText + "+one" }));
        var step2 = new RecordingStep("two", calls, ctx =>
            new StepResult.Continue(ctx with { UserText = ctx.UserText + "+two" }));
        var terminal = new RecordingStep("final", calls, ctx =>
            new StepResult.Terminate(new AgentResponse { Text = ctx.UserText }));

        var pipeline = new ChatPipeline([step1, step2, terminal]);
        var response = await pipeline.RunAsync(NewContext("hi"), CancellationToken.None);

        Assert.Equal(new[] { "one", "two", "final" }, calls);
        Assert.Equal("hi+one+two", response.Text);
        Assert.True(response.Success);
    }

    [Fact]
    public async Task Terminate_short_circuits_later_steps()
    {
        var calls = new List<string>();
        var early = new RecordingStep("early", calls, _ =>
            new StepResult.Terminate(new AgentResponse { Text = "stop here" }));
        var late = new RecordingStep("late", calls, _ =>
            new StepResult.Terminate(new AgentResponse { Text = "should not run" }));

        var pipeline = new ChatPipeline([early, late]);
        var response = await pipeline.RunAsync(NewContext("hi"), CancellationToken.None);

        Assert.Equal(new[] { "early" }, calls); // late never ran
        Assert.Equal("stop here", response.Text);
    }

    [Fact]
    public async Task Running_off_the_end_returns_deterministic_error_response()
    {
        var calls = new List<string>();
        var step = new RecordingStep("only", calls, ctx => new StepResult.Continue(ctx));

        var pipeline = new ChatPipeline([step]);
        var response = await pipeline.RunAsync(NewContext("hi"), CancellationToken.None);

        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("without producing a response", response.Text);
    }

    [Fact]
    public async Task Empty_steps_list_returns_deterministic_error_response()
    {
        var pipeline = new ChatPipeline([]);

        var response = await pipeline.RunAsync(NewContext("hi"), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("no steps configured", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_is_honoured_before_first_step_runs()
    {
        var calls = new List<string>();
        var step = new RecordingStep("only", calls,
            _ => new StepResult.Terminate(new AgentResponse { Text = "shouldn't run" }));

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var pipeline = new ChatPipeline([step]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.RunAsync(NewContext("hi"), cts.Token));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task Log_callback_is_invoked_on_each_transition()
    {
        var events = new List<(string action, string message)>();
        var step1 = new RecordingStep("one", new(), ctx => new StepResult.Continue(ctx));
        var terminal = new RecordingStep("final", new(), _ =>
            new StepResult.Terminate(new AgentResponse { Text = "done" }));

        var pipeline = new ChatPipeline(
            [step1, terminal],
            (action, message) => events.Add((action, message)));

        await pipeline.RunAsync(NewContext("hi"), CancellationToken.None);

        // Minimum: one start + one continue for step1, one start + one
        // terminate for terminal.
        Assert.Contains(events, e => e.action == "PIPELINE_STEP_START" && e.message.Contains("one"));
        Assert.Contains(events, e => e.action == "PIPELINE_STEP_CONTINUE" && e.message.Contains("one"));
        Assert.Contains(events, e => e.action == "PIPELINE_STEP_START" && e.message.Contains("final"));
        Assert.Contains(events, e => e.action == "PIPELINE_STEP_TERMINATE" && e.message.Contains("final"));
    }

    [Fact]
    public void Steps_property_exposes_configured_list_in_order()
    {
        var a = new RecordingStep("a", new(), _ => new StepResult.Continue(NewContext("x")));
        var b = new RecordingStep("b", new(), _ => new StepResult.Continue(NewContext("x")));

        var pipeline = new ChatPipeline([a, b]);

        Assert.Equal(new ITurnStep[] { a, b }, pipeline.Steps);
    }

    [Fact]
    public async Task User_steering_is_injected_before_the_remaining_step_runs()
    {
        TurnContext? observed = null;
        var terminal = new RecordingStep("remaining", new(), context =>
        {
            observed = context;
            return new StepResult.Terminate(new AgentResponse { Text = "redirected" });
        });
        var control = new SteeringControl("Use the attached Wiki page instead.");
        var pipeline = new ChatPipeline([terminal], executionControl: control);

        await pipeline.RunAsync(NewContext("research this"), CancellationToken.None);

        Assert.NotNull(observed);
        var steering = Assert.Single(observed.LlmMessages);
        Assert.Equal("system", steering.Role);
        Assert.Contains("[USER STEERING]", steering.Content);
        Assert.Contains("attached Wiki page", steering.Content);
        Assert.Equal(["pipeline:remaining"], control.Checkpoints);
    }

    [Fact]
    public async Task Opt_in_latency_trace_records_correlated_step_and_pipeline_timings()
    {
        var previous = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        Environment.SetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE", "1");
        try
        {
            var events = new List<(string action, string message)>();
            var terminal = new RecordingStep("final", new(), _ =>
                new StepResult.Terminate(new AgentResponse { Text = "done" }));
            var pipeline = new ChatPipeline(
                [terminal],
                (action, message) => events.Add((action, message)));

            await pipeline.RunAsync(NewContext("hi"), CancellationToken.None);

            Assert.Contains(events, e =>
                e.action == "PIPELINE_STEP_TIMING" &&
                e.message.Contains("thread_id=t1", StringComparison.Ordinal) &&
                e.message.Contains("turn_id=m1", StringComparison.Ordinal) &&
                e.message.Contains("step=final", StringComparison.Ordinal));
            Assert.Contains(events, e =>
                e.action == "PIPELINE_TIMING" &&
                e.message.Contains("outcome=terminate", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE", previous);
        }
    }

    private static TurnContext NewContext(string userText) =>
        new() { ThreadId = "t1", MessageId = "m1", UserText = userText };

    private sealed class RecordingStep : ITurnStep
    {
        private readonly List<string> _calls;
        private readonly Func<TurnContext, StepResult> _body;

        public RecordingStep(string name, List<string> calls, Func<TurnContext, StepResult> body)
        {
            Name = name;
            _calls = calls;
            _body = body;
        }

        public string Name { get; }

        public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
        {
            _calls.Add(Name);
            return Task.FromResult(_body(context));
        }
    }

    private sealed class SteeringControl(string steering) : ITurnExecutionControl
    {
        public List<string> Checkpoints { get; } = [];

        public Task<string?> ReachCheckpointAsync(
            TurnContext context,
            string checkpoint,
            CancellationToken cancellationToken)
        {
            Checkpoints.Add(checkpoint);
            return Task.FromResult<string?>(steering);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RoutingLatencyEnvironmentCollection
{
    public const string Name = "RoutingLatencyEnvironmentVariables";
}
