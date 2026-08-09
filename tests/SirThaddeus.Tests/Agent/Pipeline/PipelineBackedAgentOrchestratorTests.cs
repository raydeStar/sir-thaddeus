using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline;

public class PipelineBackedAgentOrchestratorTests
{
    [Fact]
    public async Task ProcessAsync_returns_final_response_and_appends_to_history()
    {
        // Smallest viable pipeline: a single step that terminates with the
        // canned text. Exercises the facade's "build context, run pipeline,
        // absorb result" round-trip without the real tool loop.
        var orch = NewOrchestrator(CannedTextStep("hello back"));

        var response = await orch.ProcessAsync("hi there");

        Assert.Equal("hello back", response.Text);
        var history = orch.HistorySnapshot();
        Assert.Collection(history,
            m => { Assert.Equal("user", m.Role); Assert.Equal("hi there", m.Content); },
            m => { Assert.Equal("assistant", m.Role); Assert.Equal("hello back", m.Content); });
    }

    [Fact]
    public async Task ProcessAsync_feeds_history_into_pipeline_context_on_subsequent_turns()
    {
        // Second turn must see both prior messages plus the system prompt
        // so the model has context. We capture the context the step saw.
        TurnContext? observedSecondTurn = null;
        var step = new ObservingStep(ctx =>
        {
            if (ctx.UserText == "turn two") observedSecondTurn = ctx;
            return new AgentResponse { Text = $"echo: {ctx.UserText}" };
        });
        var orch = NewOrchestrator(step);

        await orch.ProcessAsync("turn one");
        await orch.ProcessAsync("turn two");

        Assert.NotNull(observedSecondTurn);
        // Expected messages: [system, user(turn one), assistant(echo: turn one), user(turn two)]
        Assert.Equal(4, observedSecondTurn!.LlmMessages.Count);
        Assert.Equal("system", observedSecondTurn.LlmMessages[0].Role);
        Assert.Equal("turn one", observedSecondTurn.LlmMessages[1].Content);
        Assert.Equal("echo: turn one", observedSecondTurn.LlmMessages[2].Content);
        Assert.Equal("turn two", observedSecondTurn.LlmMessages[3].Content);
    }

    [Fact]
    public async Task ResetConversation_clears_history_but_keeps_system_prompt_alive()
    {
        var orch = NewOrchestrator(CannedTextStep("ok"));

        await orch.ProcessAsync("first");
        await orch.ProcessAsync("second");
        Assert.Equal(4, orch.HistorySnapshot().Count);

        orch.ResetConversation();
        Assert.Empty(orch.HistorySnapshot());

        // Next call starts fresh — new history should have only the current turn.
        await orch.ProcessAsync("third");
        Assert.Equal(2, orch.HistorySnapshot().Count);
    }

    [Fact]
    public async Task SeedHistory_pre_populates_history_for_workflow_runs()
    {
        // Workflow coordinators replay a transcript before calling
        // ProcessAsync. Seeded history must show up in the pipeline's
        // context on the very first turn.
        TurnContext? observed = null;
        var step = new ObservingStep(ctx =>
        {
            observed = ctx;
            return new AgentResponse { Text = "seeded reply" };
        });
        var orch = NewOrchestrator(step);

        orch.SeedHistory(new[]
        {
            ("user", "prior question"),
            ("assistant", "prior answer"),
        });
        await orch.ProcessAsync("follow-up");

        Assert.NotNull(observed);
        // [system, user(prior question), assistant(prior answer), user(follow-up)]
        Assert.Equal(4, observed!.LlmMessages.Count);
        Assert.Equal("prior question", observed.LlmMessages[1].Content);
        Assert.Equal("prior answer", observed.LlmMessages[2].Content);
    }

    [Fact]
    public void SeedHistory_drops_unknown_roles_silently()
    {
        // Garbage-in protection — workflow transcripts occasionally have
        // roles like "tool" or "function" that aren't meaningful for
        // history replay. Dropping them is safer than throwing.
        var orch = NewOrchestrator(CannedTextStep("ok"));

        orch.SeedHistory(new[]
        {
            ("user", "kept"),
            ("tool", "dropped"),
            ("assistant", "also kept"),
            ("", "dropped too"),
        });

        var history = orch.HistorySnapshot();
        Assert.Equal(2, history.Count);
        Assert.Equal("kept", history[0].Content);
        Assert.Equal("also kept", history[1].Content);
    }

    [Fact]
    public async Task GetAvailableToolCountAsync_forwards_to_mcp()
    {
        var mcp = new StubMcp(new[] { "web_search", "weather_geocode", "ping" });
        var orch = NewOrchestrator(CannedTextStep("ok"), mcp: mcp);

        var count = await orch.GetAvailableToolCountAsync(CancellationToken.None);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task GetAvailableToolCountAsync_returns_zero_when_mcp_fails()
    {
        // Diagnostic endpoint must never throw — the shell uses it during
        // startup to display "MCP: N tools available" and would otherwise
        // bring down the boot process.
        var mcp = new ThrowingMcp();
        var orch = NewOrchestrator(CannedTextStep("ok"), mcp: mcp);

        var count = await orch.GetAvailableToolCountAsync(CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ProcessAsync_rejects_empty_user_message()
    {
        var orch = NewOrchestrator(CannedTextStep("ok"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orch.ProcessAsync(""));
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var pipeline = new ChatPipeline(new[] { CannedTextStep("x") });
        var mcp = new StubMcp(Array.Empty<string>());

        Assert.Throws<ArgumentNullException>(() =>
            new PipelineBackedAgentOrchestrator(null!, mcp, "sys"));
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineBackedAgentOrchestrator(pipeline, null!, "sys"));
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineBackedAgentOrchestrator(pipeline, mcp, null!));
    }

    [Fact]
    public async Task ConversationId_is_threaded_into_context_ThreadId()
    {
        // When a workflow coordinator passes a conversationId, the
        // pipeline should see it on ThreadId so memory providers,
        // loggers, and event sinks can correlate.
        TurnContext? observed = null;
        var step = new ObservingStep(ctx => { observed = ctx; return new AgentResponse { Text = "ok" }; });
        var orch = NewOrchestrator(step);

        await orch.ProcessAsync("hi", conversationId: "workflow-abc", CancellationToken.None);

        Assert.Equal("workflow-abc", observed!.ThreadId);
    }

    [Fact]
    public async Task ConversationId_defaults_to_literal_default_when_not_supplied()
    {
        TurnContext? observed = null;
        var step = new ObservingStep(ctx => { observed = ctx; return new AgentResponse { Text = "ok" }; });
        var orch = NewOrchestrator(step);

        await orch.ProcessAsync("hi");

        Assert.Equal("default", observed!.ThreadId);
    }

    [Fact]
    public async Task Workflow_deadline_is_threaded_into_turn_context()
    {
        TurnContext? observed = null;
        var step = new ObservingStep(ctx => { observed = ctx; return new AgentResponse { Text = "ok" }; });
        var orch = NewOrchestrator(step);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        orch.SetWorkflowDeadline(deadline);
        await orch.ProcessAsync("hi");

        Assert.Equal(deadline, observed!.WorkflowDeadlineUtc);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static PipelineBackedAgentOrchestrator NewOrchestrator(
        ITurnStep step,
        IMcpToolClient? mcp = null)
    {
        var pipeline = new ChatPipeline(new[] { step });
        return new PipelineBackedAgentOrchestrator(
            pipeline,
            mcp ?? new StubMcp(Array.Empty<string>()),
            systemPrompt: "You are a helpful bot.");
    }

    private static ITurnStep CannedTextStep(string text)
        => new ObservingStep(_ => new AgentResponse { Text = text });

    private sealed class ObservingStep : ITurnStep
    {
        private readonly Func<TurnContext, AgentResponse> _handler;
        public ObservingStep(Func<TurnContext, AgentResponse> handler) { _handler = handler; }
        public string Name => "Observing";
        public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
            => Task.FromResult<StepResult>(new StepResult.Terminate(_handler(context)));
    }

    private sealed class StubMcp : IMcpToolClient
    {
        private readonly IReadOnlyList<string> _toolNames;
        public StubMcp(IReadOnlyList<string> toolNames) { _toolNames = toolNames; }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            var infos = _toolNames
                .Select(n => new McpToolInfo { Name = n, Description = n, InputSchema = new { } })
                .ToArray();
            return Task.FromResult<IReadOnlyList<McpToolInfo>>(infos);
        }

        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
    }

    private sealed class ThrowingMcp : IMcpToolClient
    {
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<McpToolInfo>>(new InvalidOperationException("mcp down"));
        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromException<string>(new InvalidOperationException("mcp down"));
    }
}
