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

    private sealed class CountingLlm : ILlmClient
    {
        public int CallCount { get; private set; }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new LlmResponse { IsComplete = true, Content = "{\"passed\":true}" });
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new LlmResponse { IsComplete = true, Content = "{\"passed\":true}" });
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fake");
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
