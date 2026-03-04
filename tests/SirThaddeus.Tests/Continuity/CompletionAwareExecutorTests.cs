using SirThaddeus.Agent;
using SirThaddeus.Agent.Orchestration;
using SirThaddeus.Agent.Orchestration.Correlation;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Validation.Completion;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Continuity;

public sealed class CompletionAwareExecutorTests
{
    // ── Fake inner executor ──────────────────────────────────────────

    private sealed class FakeToolLoopExecutor : IToolLoopExecutor
    {
        private readonly Queue<AgentResponse> _responses = new();
        public int ExecutionCount { get; private set; }

        public void EnqueueResponse(AgentResponse response) => _responses.Enqueue(response);

        public Task<AgentResponse> ExecuteAsync(
            ToolLoopExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : new AgentResponse { Text = "default response" };
            return Task.FromResult(response);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static ToolLoopExecutionRequest MakeRequest(
        string intent = "chat_only",
        RunContext? ctx = null)
    {
        return new ToolLoopExecutionRequest
        {
            History = [ChatMessage.System("test"), ChatMessage.User("test")],
            Tools = [],
            ToolCallsMade = [],
            InitialRoundTrips = 0,
            Decision = new IntentDecisionV2 { Intent = intent, Confidence = 1.0 },
            SanitizeAssistantText = t => t,
            LogEvent = (_, _) => { },
            RunContext = ctx
        };
    }

    private static ToolCallRecord OkResult(string toolName, string json) => new()
    {
        ToolName = toolName,
        Arguments = "{}",
        Result = json,
        Success = true
    };

    // ── No RunContext: backward-compatible ────────────────────────────

    [Fact]
    public async Task NoRunContext_SkipsCompletionChecking()
    {
        var inner = new FakeToolLoopExecutor();
        inner.EnqueueResponse(new AgentResponse { Text = "hello" });
        var executor = new CompletionAwareToolLoopExecutor(inner);

        var response = await executor.ExecuteAsync(MakeRequest());

        Assert.Equal("hello", response.Text);
        Assert.False(response.IsPartial);
        Assert.Null(response.CorrelationId);
        Assert.Equal(1, inner.ExecutionCount);
    }

    // ── AlwaysSatisfied contract (chat_only): no checking ────────────

    [Fact]
    public async Task ChatOnly_SkipsCompletionChecking()
    {
        var inner = new FakeToolLoopExecutor();
        inner.EnqueueResponse(new AgentResponse { Text = "just chatting" });
        var ctx = RunContext.New();
        var executor = new CompletionAwareToolLoopExecutor(inner);

        var response = await executor.ExecuteAsync(MakeRequest("chat_only", ctx));

        Assert.Equal("just chatting", response.Text);
        Assert.False(response.IsPartial);
        Assert.Equal(ctx.CorrelationId.Value, response.CorrelationId);
        Assert.Equal(1, inner.ExecutionCount);
    }

    // ── Complete result: stamps correlation, no repair ────────────────

    [Fact]
    public async Task CompleteResult_StampsCorrelationNoRepair()
    {
        var inner = new FakeToolLoopExecutor();
        var toolResults = new List<ToolCallRecord>
        {
            OkResult("places_lookup", """{"name":"Joe's","address":"123 Main","url":"https://x.com"}""")
        };
        inner.EnqueueResponse(new AgentResponse
        {
            Text = "Found Joe's Bakery at 123 Main St.",
            ToolCallsMade = toolResults
        });

        var ctx = RunContext.New();
        ctx.Intent = Intents.LookupFact;
        var executor = new CompletionAwareToolLoopExecutor(inner);

        var response = await executor.ExecuteAsync(MakeRequest(Intents.LookupFact, ctx));

        Assert.False(response.IsPartial);
        Assert.Equal(ctx.CorrelationId.Value, response.CorrelationId);
        Assert.Equal(1.0, response.CompletionConfidence);
        Assert.Equal("complete", response.CompletionStopReason);
        Assert.Equal(1, inner.ExecutionCount);
        Assert.Equal(0, ctx.RepairCount);
    }

    // ── Incomplete result: triggers repair ────────────────────────────

    [Fact]
    public async Task IncompleteResult_TriggersRepair()
    {
        var inner = new FakeToolLoopExecutor();

        // First execution: missing "name" field
        inner.EnqueueResponse(new AgentResponse
        {
            Text = "Here are some results.",
            ToolCallsMade = [OkResult("places_lookup", """{"address":"123 Main","url":"https://x.com"}""")]
        });

        // Second execution (after repair): has "name"
        inner.EnqueueResponse(new AgentResponse
        {
            Text = "Joe's Bakery is at 123 Main St.",
            ToolCallsMade =
            [
                OkResult("places_lookup", """{"address":"123 Main","url":"https://x.com"}"""),
                OkResult("places_lookup", """{"name":"Joe's Bakery","url":"https://x.com"}""")
            ]
        });

        var ctx = RunContext.New(maxRepairs: 2);
        ctx.Intent = Intents.LookupFact;
        var executor = new CompletionAwareToolLoopExecutor(inner);

        var response = await executor.ExecuteAsync(MakeRequest(Intents.LookupFact, ctx));

        Assert.Equal(2, inner.ExecutionCount);
        Assert.Equal(1, ctx.RepairCount);
    }

    // ── Repair budget exhausted: returns partial ──────────────────────

    [Fact]
    public async Task RepairBudgetExhausted_ReturnsPartial()
    {
        var inner = new FakeToolLoopExecutor();

        // Always return incomplete results
        var incompleteResponse = new AgentResponse
        {
            Text = "Incomplete results.",
            ToolCallsMade = [OkResult("places_lookup", """{"address":"123 Main","url":"https://x.com"}""")]
        };

        // Enqueue enough responses for initial + max repairs
        inner.EnqueueResponse(incompleteResponse);
        inner.EnqueueResponse(incompleteResponse);
        inner.EnqueueResponse(incompleteResponse);

        var ctx = RunContext.New(maxRepairs: 2);
        ctx.Intent = Intents.LookupFact;
        var executor = new CompletionAwareToolLoopExecutor(inner);

        var response = await executor.ExecuteAsync(MakeRequest(Intents.LookupFact, ctx));

        Assert.True(response.IsPartial);
        Assert.Contains("name", response.MissingFields);
        Assert.Equal(ctx.CorrelationId.Value, response.CorrelationId);
        Assert.Equal("repair_budget_exhausted", response.CompletionStopReason);
        Assert.Equal(2, ctx.RepairCount);
    }

    // ── Zero repair budget: reports partial immediately ───────────────

    [Fact]
    public async Task ZeroRepairBudget_ReportsPartialImmediately()
    {
        var inner = new FakeToolLoopExecutor();
        inner.EnqueueResponse(new AgentResponse
        {
            Text = "Results",
            ToolCallsMade = [OkResult("places_lookup", """{"address":"123 Main","url":"https://x.com"}""")]
        });

        var ctx = RunContext.New(maxRepairs: 0);
        ctx.Intent = Intents.LookupFact;
        var executor = new CompletionAwareToolLoopExecutor(inner);

        var response = await executor.ExecuteAsync(MakeRequest(Intents.LookupFact, ctx));

        Assert.True(response.IsPartial);
        Assert.Equal("repair_budget_exhausted", response.CompletionStopReason);
        Assert.Equal(1, inner.ExecutionCount);
        Assert.Equal(0, ctx.RepairCount);
    }

    // ── Repair prompt injected into history ───────────────────────────

    [Fact]
    public async Task RepairPrompt_InjectedIntoHistory()
    {
        var inner = new FakeToolLoopExecutor();
        inner.EnqueueResponse(new AgentResponse
        {
            Text = "Partial",
            ToolCallsMade = [OkResult("places_lookup", """{"address":"123 Main","url":"https://x.com"}""")]
        });
        inner.EnqueueResponse(new AgentResponse
        {
            Text = "Complete now",
            ToolCallsMade =
            [
                OkResult("places_lookup", """{"name":"Joe's","address":"123 Main","url":"https://x.com"}""")
            ]
        });

        var ctx = RunContext.New(maxRepairs: 1);
        ctx.Intent = Intents.LookupFact;
        var request = MakeRequest(Intents.LookupFact, ctx);
        var executor = new CompletionAwareToolLoopExecutor(inner);

        await executor.ExecuteAsync(request);

        // The repair prompt should have been added to history
        Assert.Contains(request.History, m =>
            m.Role == "user" && (m.Content ?? "").Contains("[REPAIR"));
    }

    // ── Cancellation is respected ────────────────────────────────────

    [Fact]
    public async Task Cancellation_ThrowsDuringRepair()
    {
        var inner = new FakeToolLoopExecutor();
        inner.EnqueueResponse(new AgentResponse
        {
            Text = "Partial",
            ToolCallsMade = [OkResult("places_lookup", """{"address":"123 Main","url":"https://x.com"}""")]
        });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var ctx = RunContext.New(maxRepairs: 2);
        ctx.Intent = Intents.LookupFact;
        var executor = new CompletionAwareToolLoopExecutor(inner);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(MakeRequest(Intents.LookupFact, ctx), cts.Token));
    }
}
