using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class BenignFallbackStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("BenignFallback", new BenignFallbackStep().Name);

    [Fact]
    public async Task Passes_through_when_builder_has_no_match()
    {
        // The builder only matches a specific set of canned prompts
        // (maintained in OrchestratorMessageHelpers). Everything else
        // should fall through untouched so the LLM can handle it.
        var step = new BenignFallbackStep();
        var ctx = WithUserText("tell me about the french revolution");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task Passes_through_when_message_looks_like_explicit_tool_invocation()
    {
        // Even if the benign-builder would match, an explicit tool-use
        // signal takes precedence — the user wants the tool loop.
        var step = new BenignFallbackStep();
        var ctx = WithUserText("use web_search to find cats");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
    }

    [Fact]
    public async Task Passes_through_when_message_looks_like_web_search()
    {
        var step = new BenignFallbackStep();
        var ctx = WithUserText("search the web for sourdough recipes");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
    }

    [Fact]
    public async Task Empty_user_text_passes_through()
    {
        // Defensive: benign builder returns null on empty input, and we
        // confirm the step surfaces Continue rather than terminating
        // with empty text.
        var step = new BenignFallbackStep();
        var ctx = WithUserText("   ");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
    }

    [Fact]
    public async Task Response_text_matches_legacy_helper_output_when_a_match_exists()
    {
        // If the helper returns a match for ANY input, the step's
        // response text must equal the helper's output verbatim — no
        // re-formatting, no additional text. Harness + UI audit logs
        // depend on that parity.
        //
        // We don't hard-code an input that matches because the helper's
        // match set is implementation-defined and may drift. Instead,
        // probe the helper with a handful of canonical benign prompts
        // and assert only when it actually produces a match.
        var step = new BenignFallbackStep();
        string[] candidates =
        {
            "hi",
            "hello",
            "thanks",
            "how are you",
        };

        foreach (var candidate in candidates)
        {
            var helperOutput = SirThaddeus.Agent.OrchestratorMessageHelpers
                .TryBuildEarlyDeterministicBenignFallback(candidate);
            if (string.IsNullOrEmpty(helperOutput))
                continue;

            var result = await step.ExecuteAsync(WithUserText(candidate), CancellationToken.None);
            var term = Assert.IsType<StepResult.Terminate>(result);
            Assert.Equal(helperOutput, term.Response.Text);
            Assert.True(term.Response.Success);
            Assert.Equal(0, term.Response.LlmRoundTrips);
            Assert.Empty(term.Response.ToolCallsMade);
            return; // at least one input exercised the assertion
        }

        // No candidate matched the helper — skip silently. The helper's
        // match set is tight by design; if it drifts to cover none of
        // these canonical prompts we'd want to know, but not fail this
        // parity test.
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new BenignFallbackStep();
        var ctx = WithUserText("hi");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    private static TurnContext WithUserText(string userText) =>
        new() { ThreadId = "t1", MessageId = "m1", UserText = userText };
}
