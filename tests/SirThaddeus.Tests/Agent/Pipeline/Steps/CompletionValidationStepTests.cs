using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Validation;
using SirThaddeus.LlmClient;
using SirThaddeus.Tests.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

[Collection(RoutingLatencyEnvironmentCollection.Name)]
public class CompletionValidationStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("CompletionValidation", new CompletionValidationStep(null, null).Name);

    [Fact]
    public async Task No_op_when_validator_is_null()
    {
        // Runtimes that don't want the extra validation round-trip
        // simply omit the validator. Step must pass through untouched.
        var step = new CompletionValidationStep(null, null);
        var ctx = WithDraft("hi", "great question");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task No_op_when_draft_is_blank()
    {
        // Blank draft → ResponseComposer will surface its own "empty
        // reply" marker. Don't waste an LLM validation call on nothing.
        var step = new CompletionValidationStep(null, null);
        var ctx = WithDraft("hi", "   ");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new CompletionValidationStep(null, null);
        var ctx = WithDraft("hi", "reply");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    [Fact]
    public async Task Skips_llm_validation_for_deterministic_places_discover_draft()
    {
        var llm = new CountingLlm();
        var step = new CompletionValidationStep(new CompletionValidator(llm), null);
        var ctx = WithDraft(
            "Is there a florist nearby?",
            "I found these florists near Olympia, Washington, US via places_discover/osm_overpass:\n" +
            "- **Fleurae** - 123 Example St - 1.2 km away");
        ctx = ctx with
        {
            ToolCallsMade =
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesDiscover,
                    Arguments = "{}",
                    Result = "{\"results\":[{\"name\":\"Fleurae\"}]}",
                    Success = true
                }
            ]
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task Projects_successful_tool_evidence_and_skips_llm_validation()
    {
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var llm = new CountingLlm();
        var events = new List<(string Action, string Message)>();
        var step = new CompletionValidationStep(
            new CompletionValidator(llm),
            null,
            (action, message) => events.Add((action, message)));
        var ctx = WithDraft(
            "Use file_read. Reply with only the codename value.",
            "The codename is CIRRUS-284.") with
        {
            ToolCallsMade =
            [
                new ToolCallRecord
                {
                    ToolName = "file_read",
                    Arguments = "{}",
                    Result = "{\"textContent\":\"Codename: CIRRUS-284\"}",
                    Success = true
                }
            ]
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("CIRRUS-284", cont.Next.AssistantDraft);
        Assert.Equal(0, llm.CallCount);
        Assert.Contains(events, item =>
            item.Action == "EXPERIMENT_ACTIVATION" &&
            item.Message.Contains("event=answer_only_tool_evidence_projection", StringComparison.Ordinal) &&
            item.Message.Contains("decision=activated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inactive_projection_retains_normal_validator_path()
    {
        var llm = new CountingLlm();
        var step = new CompletionValidationStep(new CompletionValidator(llm), null);
        var ctx = WithDraft("Explain the result.", "The codename is CIRRUS-284.") with
        {
            ToolCallsMade =
            [
                new ToolCallRecord
                {
                    ToolName = "file_read",
                    Arguments = "{}",
                    Result = "{\"textContent\":\"Codename: CIRRUS-284\"}",
                    Success = true
                }
            ]
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(ctx.AssistantDraft, cont.Next.AssistantDraft);
        Assert.Equal(1, llm.CallCount);
    }

    [Fact]
    public async Task Valid_verified_file_effect_attestation_skips_llm_validation()
    {
        const string draft = "Done - I wrote and verified `notes.txt`.";
        var llm = new CountingLlm();
        var events = new List<(string Action, string Message)>();
        var step = new CompletionValidationStep(
            new CompletionValidator(llm),
            null,
            (action, message) => events.Add((action, message)));
        var ctx = WithDraft("Create `notes.txt` now with the supplied text.", draft) with
        {
            ToolCallsMade = [VerifiedWriteCall()],
            VerifiedFileEffectCompletion = new VerifiedFileEffectCompletionAttestation(draft),
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
        Assert.Equal(0, llm.CallCount);
        Assert.Contains(events, item =>
            item.Action == "EXPERIMENT_ACTIVATION" &&
            item.Message.Contains("event=verified_file_completion_attestation", StringComparison.Ordinal) &&
            item.Message.Contains("decision=validated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changed_attested_draft_keeps_normal_validator_path()
    {
        const string attested = "Done - I wrote and verified `notes.txt`.";
        var llm = new CountingLlm();
        var events = new List<(string Action, string Message)>();
        var step = new CompletionValidationStep(
            new CompletionValidator(llm),
            null,
            (action, message) => events.Add((action, message)));
        var ctx = WithDraft("Create `notes.txt` now with the supplied text.", "Changed draft") with
        {
            ToolCallsMade = [VerifiedWriteCall()],
            VerifiedFileEffectCompletion = new VerifiedFileEffectCompletionAttestation(attested),
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("Changed draft", cont.Next.AssistantDraft);
        Assert.Equal(1, llm.CallCount);
        Assert.Contains(events, item =>
            item.Action == "EXPERIMENT_ACTIVATION" &&
            item.Message.Contains("decision=rejected", StringComparison.Ordinal) &&
            item.Message.Contains("reason=draft_changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repair_trace_attributes_identical_generation_without_logging_content()
    {
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        const string draft = "Mixing blue and yellow produces green.";
        var llm = new CountingLlm(draft);
        var events = new List<(string Action, string Message)>();
        var validator = new CompletionValidator(llm);
        var step = new CompletionValidationStep(
            validator,
            new RepairLoop(llm, validator),
            (action, message) => events.Add((action, message)));
        var ctx = WithDraft(
            "Put the final answer on its own line as `Final answer: <answer>`. " +
            "What color results from mixing blue and yellow?",
            draft);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
        Assert.Equal(1, llm.CallCount);
        var repairEvent = Assert.Single(events, item =>
            item.Action == "COMPLETION_REPAIR_TIMING");
        Assert.Contains("changed=False", repairEvent.Message, StringComparison.Ordinal);
        Assert.Contains("attempts=1", repairEvent.Message, StringComparison.Ordinal);
        Assert.Contains("generated_nonempty=True", repairEvent.Message, StringComparison.Ordinal);
        Assert.Contains("generated_changed=False", repairEvent.Message, StringComparison.Ordinal);
        Assert.Contains("passed_revalidation=False", repairEvent.Message, StringComparison.Ordinal);
        Assert.Contains("adopted=False", repairEvent.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(draft, repairEvent.Message, StringComparison.Ordinal);
    }

    // Happy-path (validation + repair round-trip) requires a real
    // CompletionValidator / RepairLoop with a fake ILlmClient. Those
    // types are `sealed` and call `llm.ChatAsync` directly, so full
    // integration coverage lives in the existing `CompletionValidatorTests`
    // + `RepairLoopTests`. The step's own contract — null guards,
    // exception swallowing, draft rewriting on repair — is covered by
    // the null-case assertions above plus the integration pipeline.

    private static TurnContext WithDraft(string userText, string draft) =>
        new()
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = userText,
            AssistantDraft = draft,
        };

    private static ToolCallRecord VerifiedWriteCall() => new()
    {
        ToolName = "file_write",
        Arguments = "{\"path\":\"notes.txt\",\"content\":\"hello\\n\"}",
        Result = "{\"ok\":true,\"verified\":true,\"path\":\"C:/allowed/notes.txt\"," +
                 "\"bytes\":6,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}",
        Success = true,
    };

    private sealed class CountingLlm : ILlmClient
    {
        private readonly Queue<string> _responses;

        public CountingLlm(params string[] responses) => _responses = new Queue<string>(responses);

        public int CallCount { get; private set; }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Response());
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Response());
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fake");

        private LlmResponse Response() => new()
        {
            IsComplete = true,
            Content = _responses.Count > 0 ? _responses.Dequeue() : "{\"passed\":true}"
        };
    }

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
