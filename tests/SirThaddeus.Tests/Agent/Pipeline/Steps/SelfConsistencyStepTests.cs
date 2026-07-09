using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public sealed class SelfConsistencyStepTests
{
    [Fact]
    public async Task Strong_consensus_terminates_with_winning_answer()
    {
        var llm = new QueueLlm(
            "Final answer: 42",
            "Final answer: 42",
            "Final answer: 42");
        var step = new SelfConsistencyStep(llm, samples: 5, samplingTemperature: 0.9, minAgreement: 2.0 / 3.0);

        var result = await step.ExecuteAsync(StrictNumericContext(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("42", term.Response.Text);
        Assert.Equal(3, llm.CallCount);
    }

    [Fact]
    public async Task Vote_terminate_marks_response_FromConsensusVote()
    {
        // The flag is the coordinator's signal to skip its confidence-gated
        // retry. It must be TRUE on the CoT vote Terminate — this is a real
        // majority vote over N samples.
        var llm = new QueueLlm(
            "Final answer: 42",
            "Final answer: 42",
            "Final answer: 42");
        var step = new SelfConsistencyStep(llm, samples: 3, samplingTemperature: 0.9);

        var result = await step.ExecuteAsync(StrictNumericContext(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.True(term.Response.FromConsensusVote);
    }

    [Fact]
    public async Task Tool_aware_vote_terminate_marks_response_FromConsensusVote()
    {
        // Same signal on the tool-aware vote Terminate path.
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", "1");
        var toolLoop = new FakeToolLoop(
            ContinueDraft("111"),
            ContinueDraft("111"),
            ContinueDraft("13"));
        var step = new SelfConsistencyStep(new QueueLlm(), samples: 3, toolLoop: toolLoop);

        var result = await step.ExecuteAsync(StrictNumericContextWithTools(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("111", term.Response.Text);
        Assert.True(term.Response.FromConsensusVote);
    }

    [Fact]
    public void AgentResponse_FromConsensusVote_defaults_false()
    {
        // TRUTHFULNESS guard: the flag is set only by an actual vote Terminate,
        // so a plain AgentResponse (any non-voted turn) must default it false.
        var response = new AgentResponse { Text = "hello" };

        Assert.False(response.FromConsensusVote);
    }

    [Fact]
    public async Task Weak_plurality_continues_to_normal_pipeline()
    {
        var llm = new QueueLlm(
            "Final answer: 42",
            "Final answer: 7",
            "Final answer: 42",
            "Final answer: 11",
            "Final answer: 42");
        var step = new SelfConsistencyStep(llm, samples: 5, samplingTemperature: 0.9, minAgreement: 2.0 / 3.0);
        var context = StrictNumericContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(context, cont.Next);
        Assert.Equal(5, llm.CallCount);
    }

    // ── Tool-aware mode (ST_SELF_CONSISTENCY_TOOLS) ──────────────────────

    [Fact]
    public async Task Tool_aware_mode_votes_over_tool_loop_drafts()
    {
        // Three tool-loop runs draft "111", "13", "111" → majority "111".
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", "1");
        var toolLoop = new FakeToolLoop(
            ContinueDraft("111"),
            ContinueDraft("13"),
            ContinueDraft("111"));
        var step = new SelfConsistencyStep(new QueueLlm(), samples: 3, toolLoop: toolLoop);

        var result = await step.ExecuteAsync(StrictNumericContextWithTools(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("111", term.Response.Text);
    }

    [Fact]
    public async Task Tool_aware_ungrounded_verbose_drafts_abstain_and_fall_through()
    {
        // Live incident (probe_py_prime_sum): every sample's python calls timed
        // out, so drafts were verbose prose ending in the QUESTION's own number
        // ("...primes below 100"). The numeric extractor harvested "100" from
        // all three and the unanimous artifact out-voted five clean baseline
        // passes. Ungrounded (non-bare) drafts must ABSTAIN; when every run
        // abstains the step must Continue so the normal pipeline runs instead.
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", "1");
        var toolLoop = new FakeToolLoop(
            ContinueDraft("The sum of all prime numbers below 100"),
            ContinueDraft("I could not compute the sum of primes below 100"),
            ContinueDraft("It should be the total of primes under 100"));
        var step = new SelfConsistencyStep(new QueueLlm(), samples: 3, toolLoop: toolLoop);
        var ctx = StrictNumericContextWithTools();

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next); // untouched context — pipeline proceeds normally
        Assert.Equal(3, toolLoop.InvocationCount);
    }

    [Fact]
    public async Task Tool_aware_mixed_grounding_counts_only_grounded_votes()
    {
        // One ungrounded verbose draft between two grounded bare answers: the
        // verbose run abstains, the grounded pair decides the vote.
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", "1");
        var toolLoop = new FakeToolLoop(
            ContinueDraft("1060"),
            ContinueDraft("The sum of all prime numbers below 100"),
            ContinueDraft("1060"));
        var step = new SelfConsistencyStep(new QueueLlm(), samples: 3, toolLoop: toolLoop);

        var result = await step.ExecuteAsync(StrictNumericContextWithTools(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("1060", term.Response.Text);
    }

    [Fact]
    public async Task Tool_aware_mode_early_stops_once_majority_locked()
    {
        // N=3, first two runs both draft "42" — the leader can no longer be
        // caught, so the third run must never be invoked.
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", "1");
        var toolLoop = new FakeToolLoop(
            ContinueDraft("42"),
            ContinueDraft("42"),
            ContinueDraft("99"));
        var step = new SelfConsistencyStep(new QueueLlm(), samples: 3, toolLoop: toolLoop);

        var result = await step.ExecuteAsync(StrictNumericContextWithTools(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("42", term.Response.Text);
        Assert.Equal(2, toolLoop.InvocationCount);
    }

    [Fact]
    public async Task Tool_aware_mode_off_when_env_unset_uses_cot_path()
    {
        // ST_SELF_CONSISTENCY_TOOLS unset → the tool loop is never invoked and
        // the existing CoT sampling path (which calls the LLM) runs instead.
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", null);
        var toolLoop = new FakeToolLoop(ContinueDraft("111"));
        var llm = new QueueLlm("Final answer: 7", "Final answer: 7");
        var step = new SelfConsistencyStep(llm, samples: 2, toolLoop: toolLoop);

        var result = await step.ExecuteAsync(StrictNumericContextWithTools(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("7", term.Response.Text);
        Assert.Equal(0, toolLoop.InvocationCount);
        Assert.Equal(2, llm.CallCount);
    }

    [Fact]
    public async Task Tool_aware_mode_falls_back_when_tool_loop_null()
    {
        // Flag on but no collaborator → CoT path runs, no crash.
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", "1");
        var llm = new QueueLlm("Final answer: 5", "Final answer: 5");
        var step = new SelfConsistencyStep(llm, samples: 2, toolLoop: null);

        var result = await step.ExecuteAsync(StrictNumericContextWithTools(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("5", term.Response.Text);
        Assert.Equal(2, llm.CallCount);
    }

    [Fact]
    public async Task Tool_aware_mode_falls_back_when_tool_defs_empty()
    {
        // Flag on, collaborator wired, but the turn advertises no tools → CoT
        // path runs and the tool loop is never invoked.
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", "1");
        var toolLoop = new FakeToolLoop(ContinueDraft("111"));
        var llm = new QueueLlm("Final answer: 9", "Final answer: 9");
        var step = new SelfConsistencyStep(llm, samples: 2, toolLoop: toolLoop);

        // StrictNumericContext() has no ToolDefs.
        var result = await step.ExecuteAsync(StrictNumericContext(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("9", term.Response.Text);
        Assert.Equal(0, toolLoop.InvocationCount);
        Assert.Equal(2, llm.CallCount);
    }

    [Fact]
    public async Task Tool_aware_mode_all_runs_throw_continues_to_normal_pipeline()
    {
        // Every tool-loop run throws → no sample votes → fall through to
        // Continue(context) so the normal pipeline runs the turn once.
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", "1");
        var toolLoop = new FakeToolLoop(Throws(), Throws(), Throws());
        var step = new SelfConsistencyStep(new QueueLlm(), samples: 3, toolLoop: toolLoop);
        var context = StrictNumericContextWithTools();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(context, cont.Next);
        Assert.Equal(3, toolLoop.InvocationCount);
    }

    [Fact]
    public async Task Tool_aware_mode_gives_each_run_its_own_message_list()
    {
        // Each run must receive a distinct message-list instance so ToolLoopStep
        // (which appends to the list it's handed) can't leak one run's history
        // into the next.
        using var _ = new EnvScope("ST_SELF_CONSISTENCY_TOOLS", "1");
        var toolLoop = new FakeToolLoop(
            ContinueDraft("111"),
            ContinueDraft("13"),
            ContinueDraft("7"));
        var step = new SelfConsistencyStep(new QueueLlm(), samples: 3, toolLoop: toolLoop);
        var context = StrictNumericContextWithTools();

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(3, toolLoop.ReceivedMessageLists.Count);
        // Every run got a distinct list instance …
        var distinct = toolLoop.ReceivedMessageLists
            .Distinct(ReferenceEqualityComparer.Instance)
            .Count();
        Assert.Equal(3, distinct);
        // … and none of them is the original context's own list instance, so a
        // run appending to its list can never mutate the shared context.
        Assert.All(
            toolLoop.ReceivedMessageLists,
            list => Assert.False(ReferenceEquals(list, context.LlmMessages)));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static TurnContext StrictNumericContext() => new()
    {
        ThreadId = "t1",
        MessageId = "m1",
        UserText = "What is 40+2? Put the final answer on its own line.",
        LlmMessages =
        [
            ChatMessage.System("You are Sir Thaddeus."),
            ChatMessage.User("What is 40+2? Put the final answer on its own line."),
        ],
    };

    private static TurnContext StrictNumericContextWithTools() => StrictNumericContext() with
    {
        ToolDefs =
        [
            new ToolDefinition
            {
                Function = new FunctionDefinition { Name = "calculator", Description = "calc", Parameters = new { } },
            },
        ],
    };

    private static Func<TurnContext, StepResult> ContinueDraft(string draft)
        => ctx => new StepResult.Continue(ctx with { AssistantDraft = draft });

    private static Func<TurnContext, StepResult> Throws()
        => _ => throw new InvalidOperationException("run failed");

    /// <summary>Sets an env var for the test scope and restores the prior value
    /// on dispose. A null value clears the variable while scoped.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    /// <summary>Minimal fake ITurnStep standing in for ToolLoopStep: returns a
    /// scripted result per invocation, counts invocations, and records the
    /// message-list instance each run received so tests can assert isolation.</summary>
    private sealed class FakeToolLoop : ITurnStep
    {
        private readonly Queue<Func<TurnContext, StepResult>> _behaviors;

        public FakeToolLoop(params Func<TurnContext, StepResult>[] behaviors)
            => _behaviors = new Queue<Func<TurnContext, StepResult>>(behaviors);

        public int InvocationCount { get; private set; }
        public List<IReadOnlyList<ChatMessage>> ReceivedMessageLists { get; } = new();

        public string Name => "FakeToolLoop";

        public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            ReceivedMessageLists.Add(context.LlmMessages);
            var behavior = _behaviors.Count > 0
                ? _behaviors.Dequeue()
                : (Func<TurnContext, StepResult>)(ctx => new StepResult.Continue(ctx));
            return Task.FromResult(behavior(context));
        }
    }

    private sealed class QueueLlm : ILlmClient
    {
        private readonly Queue<string> _responses;

        public QueueLlm(params string[] responses) => _responses = new Queue<string>(responses);

        public int CallCount { get; private set; }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
            => ChatAsync(messages, tools, maxTokensOverride: 512, cancellationToken);

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var content = _responses.Count > 0 ? _responses.Dequeue() : "Final answer: 0";
            return Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = content,
                FinishReason = "stop",
            });
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake");
    }
}
