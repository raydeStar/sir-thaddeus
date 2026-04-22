using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class PostProcessStepTests
{
    [Fact]
    public void Name_defaults_to_PostProcess()
    {
        var step = new PostProcessStep((_, s) => s);
        Assert.Equal("PostProcess", step.Name);
    }

    [Fact]
    public void Name_honours_custom_label_for_multi_stage_composition()
    {
        // Multiple post-process stages can be chained for clarity in
        // pipeline logs — e.g. "PostProcess:Sanitize" then
        // "PostProcess:RefusalCollapse".
        var step = new PostProcessStep((_, s) => s, "PostProcess:Sanitize");
        Assert.Equal("PostProcess:Sanitize", step.Name);
    }

    [Fact]
    public async Task Rewrites_AssistantDraft_using_supplied_sanitizer()
    {
        var step = new PostProcessStep((_, s) => s.Replace("<think>", "").Replace("</think>", ""));
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "x",
            AssistantDraft = "<think>scratchpad</think>final reply",
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("scratchpadfinal reply", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task No_op_when_AssistantDraft_is_null()
    {
        // Nothing to clean — the step forwards the context unchanged so
        // a later composer step can surface the deterministic empty-reply
        // message.
        var calls = 0;
        var step = new PostProcessStep((_, s) => { calls++; return s; });
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "x" };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.AssistantDraft);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Returns_same_context_reference_when_sanitizer_returns_same_string()
    {
        // When the sanitizer is a no-op (ReferenceEquals true), the step
        // avoids allocating a new context. Small but measurable when
        // several post-process stages run per turn.
        var draft = "already clean";
        var step = new PostProcessStep((_, s) => s);
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "x",
            AssistantDraft = draft,
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task Null_return_from_sanitizer_is_normalized_to_empty_string()
    {
        // Sanitizer contract says "return a non-null string", but if
        // someone returns null anyway we treat it as empty rather than
        // propagate NullReference downstream.
        var step = new PostProcessStep((_, _) => null!);
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "x",
            AssistantDraft = "anything",
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(string.Empty, cont.Next.AssistantDraft);
    }

    [Fact]
    public void Construction_rejects_null_sanitizer()
    {
        Assert.Throws<ArgumentNullException>(() => new PostProcessStep(null!));
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new PostProcessStep((_, s) => s);
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "x", AssistantDraft = "draft" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }
}
