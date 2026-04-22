using System.Collections.Concurrent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class FootmanRouterStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("FootmanRouter", NewStep(null).Name);

    [Fact]
    public async Task Skips_when_footman_not_configured()
    {
        // Null footman = gatekeeper disabled. Step is a no-op: no event,
        // no filtering, original tool defs preserved.
        var sink = new CapturingSink();
        var step = NewStep(footman: null, sink: sink);
        var ctx = WithTools("hi", alsoAutomationRun: false);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx.ToolDefs, cont.Next.ToolDefs);
        Assert.Empty(sink.FootmanEvents);
    }

    [Fact]
    public async Task Skips_during_automation_run_even_with_footman_configured()
    {
        // Automations pre-pin an allowlist. Running the gatekeeper on top
        // would just add latency + thrash small models.
        var sink = new CapturingSink();
        var footman = new StubFootman(AgentState.Chat, 0.9, abstain: false, reasonCode: "heuristic_chat");
        var step = NewStep(footman: footman, sink: sink);
        var ctx = WithTools("do the thing", alsoAutomationRun: true);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(0, footman.CallCount);
        Assert.Empty(sink.FootmanEvents);
    }

    [Fact]
    public async Task Skips_when_no_tools_available()
    {
        // Nothing to filter — the decision event would also be misleading
        // (shows 0/0 tools). Better to just bypass.
        var sink = new CapturingSink();
        var footman = new StubFootman(AgentState.Chat, 0.9, false, "heuristic_chat");
        var step = NewStep(footman: footman, sink: sink);
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            Features = RoutingFeatures.Extract("hi"),
            // No ToolDefs.
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(0, footman.CallCount);
        Assert.Empty(sink.FootmanEvents);
    }

    [Fact]
    public async Task Skips_when_features_not_extracted_upstream()
    {
        // The step depends on FeatureExtractorStep running before it;
        // if features are missing, fall through rather than guess.
        var sink = new CapturingSink();
        var footman = new StubFootman(AgentState.Chat, 0.9, false, "heuristic_chat");
        var step = NewStep(footman: footman, sink: sink);
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            Features = null,
            ToolDefs = [Tool("web_search")],
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, footman.CallCount);
        Assert.Empty(sink.FootmanEvents);
    }

    [Fact]
    public async Task Emits_decision_event_and_narrows_tools_on_authoritative_chat_verdict()
    {
        // Authoritative Chat verdict → tool list should be filtered down
        // to the Chat family (no web / screen / file tools). The exact
        // kept set is owned by FootmanToolFilter; here we just verify
        // the step wires it up and emits the chip.
        var sink = new CapturingSink();
        var footman = new StubFootman(AgentState.Chat, 0.95, abstain: false, reasonCode: "heuristic_chat");
        var step = NewStep(footman: footman, sink: sink);
        var ctx = WithTools("hello world", alsoAutomationRun: false,
            "web_search", "file_read", "screen_capture");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(1, footman.CallCount);

        var evt = Assert.Single(sink.FootmanEvents);
        Assert.Equal("Chat", evt.NextState);
        Assert.Equal(0.95, evt.Confidence);
        Assert.Equal("heuristic_chat", evt.ReasonCode);
        Assert.Equal(3, evt.ToolsTotal);
        // Tools kept is decided by FootmanToolFilter, but it must not
        // exceed the original list and the event must mention both sides.
        Assert.True(evt.ToolsKept <= evt.ToolsTotal);
    }

    [Fact]
    public async Task Emits_footman_error_event_when_footman_throws()
    {
        // Fail-open: exception inside footman does not abort the turn.
        // The event fires with reasonCode=footman_error and the full
        // tool list passes through untouched.
        var sink = new CapturingSink();
        var footman = new ThrowingFootman(new InvalidOperationException("boom"));
        var step = NewStep(footman: footman, sink: sink);
        var ctx = WithTools("hi", alsoAutomationRun: false, "web_search", "file_read");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        // Tools unchanged.
        Assert.Equal(ctx.ToolDefs.Count, cont.Next.ToolDefs.Count);

        var evt = Assert.Single(sink.FootmanEvents);
        Assert.Equal("footman_error", evt.ReasonCode);
        Assert.True(evt.Abstain);
        Assert.Equal(evt.ToolsTotal, evt.ToolsKept);
    }

    [Fact]
    public async Task Cancellation_bubbles_up_even_when_footman_fails()
    {
        // Genuine cancellation should propagate, not be swallowed into a
        // "footman_error" event.
        var sink = new CapturingSink();
        var footman = new ThrowingFootman(new OperationCanceledException());
        var step = NewStep(footman: footman, sink: sink);
        var ctx = WithTools("hi", false, "web_search");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static FootmanRouterStep NewStep(
        IFootmanRouter? footman,
        IChatEventSink? sink = null,
        IReadOnlyList<string>? alwaysAllow = null)
        => new(footman, sink ?? NullChatEventSink.Instance, alwaysAllow);

    private static TurnContext WithTools(string userText, bool alsoAutomationRun, params string[] toolNames)
        => new()
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = userText,
            Features = RoutingFeatures.Extract(userText),
            IsAutomationRun = alsoAutomationRun,
            ToolDefs = toolNames.Select(Tool).ToArray(),
        };

    private static ToolDefinition Tool(string name) => new()
    {
        Function = new FunctionDefinition { Name = name, Description = name, Parameters = new { } },
    };

    private sealed class CapturingSink : IChatEventSink
    {
        public ConcurrentBag<FootmanEvent> FootmanEvents { get; } = new();

        public Task TurnStartedAsync(string t, string m, CancellationToken c = default) => Task.CompletedTask;
        public Task TurnDeltaAsync(string t, string m, string x, CancellationToken c = default) => Task.CompletedTask;
        public Task TurnCompleteAsync(string t, string m, string f, bool cx, CancellationToken c = default) => Task.CompletedTask;
        public Task ToolStartedAsync(string a, string t, string m, string tool, string g, string args, CancellationToken c = default) => Task.CompletedTask;
        public Task ToolCompletedAsync(string a, string t, string m, string tool, bool ok, long d, string? snip, string? err, CancellationToken c = default) => Task.CompletedTask;

        public Task FootmanDecisionAsync(
            string threadId, string messageId, string nextState, double confidence, bool abstain,
            string reasonCode, int toolsKept, int toolsTotal, long elapsedMs, CancellationToken cancellationToken = default)
        {
            FootmanEvents.Add(new FootmanEvent(nextState, confidence, abstain, reasonCode, toolsKept, toolsTotal, elapsedMs));
            return Task.CompletedTask;
        }
    }

    private sealed record FootmanEvent(
        string NextState, double Confidence, bool Abstain,
        string ReasonCode, int ToolsKept, int ToolsTotal, long ElapsedMs);

    private sealed class StubFootman : IFootmanRouter
    {
        private readonly RoutingDecision _decision;
        public int CallCount { get; private set; }

        public StubFootman(AgentState state, double confidence, bool abstain, string reasonCode)
        {
            _decision = new RoutingDecision
            {
                SchemaVersion = 1,
                RequestId = "stub",
                NextState = state,
                Confidence = confidence,
                Abstain = abstain,
                ReasonCode = reasonCode,
            };
        }

        public Task<RoutingDecision> RouteAsync(string userMessage, RoutingFeatures features, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_decision);
        }
    }

    private sealed class ThrowingFootman : IFootmanRouter
    {
        private readonly Exception _ex;
        public ThrowingFootman(Exception ex) => _ex = ex;

        public Task<RoutingDecision> RouteAsync(string u, RoutingFeatures f, CancellationToken c = default)
            => Task.FromException<RoutingDecision>(_ex);
    }
}
