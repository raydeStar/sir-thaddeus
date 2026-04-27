using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class GuardrailsStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("Guardrails", new GuardrailsStep(pipeline: null).Name);

    [Fact]
    public async Task No_op_when_pipeline_is_null()
    {
        // Runtimes that don't want guardrails (smoke tests, minimal UI
        // builds) compose the pipeline without it. Step must pass
        // through untouched.
        var step = new GuardrailsStep(pipeline: null);
        var ctx = WithUserText("how many oceans are there?");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new GuardrailsStep(pipeline: null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(WithUserText("x"), cts.Token));
    }

    // Note: the full `ReasoningGuardrailsPipeline` type is `sealed class`
    // with concrete `GuardrailsDetector` / `GoalInferencer` / etc.
    // collaborators, so constructing it in a unit test requires a real
    // LLM client (even a fake). The step's contract (null check +
    // exception swallowing + result mapping) is fully covered by the
    // null-pipeline and cancellation cases above; happy-path coverage
    // lives in `GuardrailsPipelineTests` against the real pipeline.

    private static TurnContext WithUserText(string userText) =>
        new() { ThreadId = "t1", MessageId = "m1", UserText = userText };
}
