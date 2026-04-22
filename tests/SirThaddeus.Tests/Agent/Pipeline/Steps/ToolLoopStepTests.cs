using System.Collections.Concurrent;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class ToolLoopStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("ToolLoop", BuildStep(new FakeLlm()).Name);

    [Fact]
    public async Task Continues_with_assistant_draft_when_model_returns_no_tool_calls()
    {
        // Simplest possible path: LLM replies with plain text on the
        // first round. The step hands a Continue with AssistantDraft set
        // to the downstream post-process + composer steps — it does NOT
        // terminate the pipeline on its own.
        var llm = new FakeLlm(LlmReply.Final("hello there"));
        var sink = new CapturingSink();
        var step = BuildStep(llm, sink: sink);
        var ctx = NewContext();

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("hello there", cont.Next.AssistantDraft);
        Assert.Empty(cont.Next.ToolCallsMade);
        Assert.Empty(sink.ToolStarted);
        Assert.Empty(sink.ToolCompleted);
    }

    [Fact]
    public async Task Executes_tool_calls_and_emits_start_complete_events()
    {
        // Round 1: LLM asks for web_search. Round 2: LLM produces final text.
        // Event pair should fire for the web_search call; context should
        // carry the call record + draft out to the next step.
        var llm = new FakeLlm(
            LlmReply.Tool("web_search", "{\"q\":\"cats\"}"),
            LlmReply.Final("cats are furry"));
        var mcp = new StubMcp(toolName => "search result for " + toolName);
        var sink = new CapturingSink();

        var step = BuildStep(llm, mcp: mcp, sink: sink);
        var ctx = NewContext();

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("cats are furry", cont.Next.AssistantDraft);
        Assert.Single(cont.Next.ToolCallsMade);
        Assert.Equal("web_search", cont.Next.ToolCallsMade[0].ToolName);
        Assert.True(cont.Next.ToolCallsMade[0].Success);

        // Paired events for the single tool call.
        var started = Assert.Single(sink.ToolStarted);
        var completed = Assert.Single(sink.ToolCompleted);
        Assert.Equal(started.ActivityId, completed.ActivityId);
        Assert.Equal("web_search", started.Tool);
        Assert.True(completed.Ok);
    }

    [Fact]
    public async Task Permission_denial_records_failure_and_skips_mcp()
    {
        // Gate denies; the step must not call MCP, must surface an error
        // completion event, and must feed a denial stub back into history
        // so the model can continue gracefully.
        var llm = new FakeLlm(
            LlmReply.Tool("web_search", "{}"),
            LlmReply.Final("okay, no search"));
        var mcp = new StubMcp(_ => throw new InvalidOperationException("should not be called"));
        var sink = new CapturingSink();
        var gate = new DeterministicGate(allow: false, reason: "blocked by policy");

        var step = BuildStep(llm, mcp: mcp, sink: sink, gate: gate);
        var ctx = NewContext();

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        var completed = Assert.Single(sink.ToolCompleted);
        Assert.False(completed.Ok);
        Assert.Contains("blocked by policy", completed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Interceptor_claims_call_and_MCP_is_not_invoked()
    {
        // An interceptor that owns the tool name short-circuits MCP —
        // this is how the runtime hooks propose_automation.
        var llm = new FakeLlm(
            LlmReply.Tool("propose_automation", "{\"name\":\"x\"}"),
            LlmReply.Final("draft sent"));
        var mcpCalled = false;
        var mcp = new StubMcp(_ => { mcpCalled = true; return "should not happen"; });
        var interceptor = new NamedInterceptor("propose_automation",
            new ToolCallOutcome("proposal accepted", Ok: true, Error: null));
        var sink = new CapturingSink();

        var step = BuildStep(llm, mcp: mcp, sink: sink, interceptors: new[] { interceptor });
        var ctx = NewContext();

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        Assert.False(mcpCalled);
        var completed = Assert.Single(sink.ToolCompleted);
        Assert.True(completed.Ok);
        Assert.Equal("propose_automation", completed.Tool);
    }

    [Fact]
    public async Task Args_rewriter_mutates_arguments_before_execution()
    {
        // The rewriter is our seam for runtime-specific tweaks (e.g. force
        // recency=week on web_search inside an automation run). It must
        // run before both the interceptor chain and MCP.
        var llm = new FakeLlm(
            LlmReply.Tool("web_search", "{\"q\":\"a\"}"),
            LlmReply.Final("done"));
        string? observedArgs = null;
        var mcp = new StubMcp((tool, args) => { observedArgs = args; return "ok"; });
        var rewriter = new InlineArgsRewriter((ctx, name, args) =>
            name == "web_search" ? "{\"q\":\"a\",\"recency\":\"week\"}" : args);

        var step = BuildStep(llm, mcp: mcp, argsRewriters: new[] { rewriter });

        await step.ExecuteAsync(NewContext(), CancellationToken.None);

        Assert.NotNull(observedArgs);
        Assert.Contains("recency", observedArgs);
    }

    [Fact]
    public async Task Mcp_exception_is_surfaced_as_failed_outcome_not_thrown()
    {
        // The step's contract is "run the loop to completion"; an MCP
        // tool that throws should produce a failed ToolCallRecord and a
        // completed event with ok=false — not bubble out of ExecuteAsync.
        var llm = new FakeLlm(
            LlmReply.Tool("flaky", "{}"),
            LlmReply.Final("handled"));
        var mcp = new StubMcp(_ => throw new InvalidOperationException("boom"));
        var sink = new CapturingSink();

        var step = BuildStep(llm, mcp: mcp, sink: sink);

        var result = await step.ExecuteAsync(NewContext(), CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        var completed = Assert.Single(sink.ToolCompleted);
        Assert.False(completed.Ok);
        Assert.Contains("boom", completed.Error);
    }

    [Fact]
    public async Task Hits_round_trip_cap_with_deterministic_message()
    {
        // LLM keeps asking for tools forever. After MaxRoundTrips we bail
        // with a fixed message so the UI doesn't spin.
        var llm = new FakeLlm(
            LlmReply.Tool("web_search", "{\"q\":\"a\"}"),
            LlmReply.Tool("web_search", "{\"q\":\"b\"}"),
            LlmReply.Tool("web_search", "{\"q\":\"c\"}"));
        var mcp = new StubMcp(_ => "ok");

        var step = BuildStep(llm, mcp: mcp, maxRoundTrips: 2);

        var result = await step.ExecuteAsync(NewContext(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Contains("round-trip cap", term.Response.Text);
        Assert.Equal(2, term.Response.LlmRoundTrips);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = BuildStep(new FakeLlm(LlmReply.Final("never runs")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(NewContext(), cts.Token));
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static ToolLoopStep BuildStep(
        FakeLlm llm,
        StubMcp? mcp = null,
        IChatEventSink? sink = null,
        IToolPermissionGate? gate = null,
        IEnumerable<IToolCallInterceptor>? interceptors = null,
        IEnumerable<IToolArgsRewriter>? argsRewriters = null,
        int maxRoundTrips = 6)
        => new(
            llm,
            mcp ?? new StubMcp(_ => ""),
            sink ?? NullChatEventSink.Instance,
            gate,
            groupClassifier: null,
            interceptors,
            argsRewriters,
            maxRoundTrips);

    private static TurnContext NewContext() => new()
    {
        ThreadId = "t1",
        MessageId = "m1",
        UserText = "hi",
        LlmMessages = new[] { ChatMessage.System("sys"), ChatMessage.User("hi") },
        ToolDefs = new[]
        {
            new ToolDefinition { Function = new FunctionDefinition { Name = "web_search", Description = "web", Parameters = new { } } },
            new ToolDefinition { Function = new FunctionDefinition { Name = "flaky", Description = "test", Parameters = new { } } },
            new ToolDefinition { Function = new FunctionDefinition { Name = "propose_automation", Description = "virtual", Parameters = new { } } },
        },
    };

    private sealed class FakeLlm : ILlmClient
    {
        private readonly Queue<LlmReply> _replies;
        public FakeLlm(params LlmReply[] replies) => _replies = new(replies);

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default) => Task.FromResult(
                _replies.Count > 0
                    ? _replies.Dequeue().ToResponse()
                    : new LlmResponse { IsComplete = true, Content = "(ran out)", FinishReason = "stop" });

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
            => ChatAsync(messages, tools, cancellationToken);

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake-model");
    }

    private abstract record LlmReply
    {
        public abstract LlmResponse ToResponse();

        public static LlmReply Final(string text) => new FinalReply(text);
        public static LlmReply Tool(string name, string args)
            => new ToolReply(name, args);
    }

    private sealed record FinalReply(string Text) : LlmReply
    {
        public override LlmResponse ToResponse()
            => new() { IsComplete = true, Content = Text, FinishReason = "stop" };
    }

    private sealed record ToolReply(string Name, string Args) : LlmReply
    {
        public override LlmResponse ToResponse() => new()
        {
            IsComplete = false,
            Content = null,
            FinishReason = "tool_calls",
            ToolCalls = new[]
            {
                new ToolCallRequest
                {
                    Id = "call_" + Guid.NewGuid().ToString("N")[..8],
                    Type = "function",
                    Function = new FunctionCallDetails { Name = Name, Arguments = Args },
                },
            },
        };
    }

    private sealed class StubMcp : IMcpToolClient
    {
        private readonly Func<string, string, string> _impl;
        public StubMcp(Func<string, string> impl) : this((tool, _) => impl(tool)) { }
        public StubMcp(Func<string, string, string> impl) { _impl = impl; }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>(Array.Empty<McpToolInfo>());

        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult(_impl(toolName, argumentsJson));
    }

    private sealed class DeterministicGate : IToolPermissionGate
    {
        private readonly bool _allow;
        private readonly string _reason;
        public DeterministicGate(bool allow, string reason) { _allow = allow; _reason = reason; }
        public Task<ToolPermissionResult> CheckAsync(string toolName, string argumentsJson, CancellationToken ct)
            => Task.FromResult(_allow ? ToolPermissionResult.Grant() : ToolPermissionResult.Deny(_reason));
    }

    private sealed class NamedInterceptor : IToolCallInterceptor
    {
        private readonly string _name;
        private readonly ToolCallOutcome _outcome;
        public NamedInterceptor(string name, ToolCallOutcome outcome) { _name = name; _outcome = outcome; }

        public Task<ToolCallOutcome?> TryInterceptAsync(
            TurnContext context, string toolName, string argumentsJson, string activityId, CancellationToken ct)
            => Task.FromResult(string.Equals(toolName, _name, StringComparison.OrdinalIgnoreCase) ? _outcome : null);
    }

    private sealed class InlineArgsRewriter : IToolArgsRewriter
    {
        private readonly Func<TurnContext, string, string, string> _impl;
        public InlineArgsRewriter(Func<TurnContext, string, string, string> impl) { _impl = impl; }
        public string Rewrite(TurnContext context, string toolName, string argumentsJson)
            => _impl(context, toolName, argumentsJson);
    }

    private sealed class CapturingSink : IChatEventSink
    {
        public ConcurrentBag<ToolStartedEvent> ToolStarted { get; } = new();
        public ConcurrentBag<ToolCompletedEvent> ToolCompleted { get; } = new();

        public Task TurnStartedAsync(string t, string m, CancellationToken c = default) => Task.CompletedTask;
        public Task TurnDeltaAsync(string t, string m, string x, CancellationToken c = default) => Task.CompletedTask;
        public Task TurnCompleteAsync(string t, string m, string f, bool cx, CancellationToken c = default) => Task.CompletedTask;
        public Task FootmanDecisionAsync(string t, string m, string s, double cf, bool ab, string r, int k, int to, long e, CancellationToken c = default) => Task.CompletedTask;

        public Task ToolStartedAsync(string activityId, string threadId, string messageId, string tool, string group, string argsPreview, CancellationToken c = default)
        {
            ToolStarted.Add(new ToolStartedEvent(activityId, tool, group));
            return Task.CompletedTask;
        }

        public Task ToolCompletedAsync(string activityId, string threadId, string messageId, string tool, bool ok, long durationMs, string? resultSnippet, string? error, CancellationToken c = default)
        {
            ToolCompleted.Add(new ToolCompletedEvent(activityId, tool, ok, error));
            return Task.CompletedTask;
        }
    }

    private sealed record ToolStartedEvent(string ActivityId, string Tool, string Group);
    private sealed record ToolCompletedEvent(string ActivityId, string Tool, bool Ok, string? Error);
}
