using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

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
}
