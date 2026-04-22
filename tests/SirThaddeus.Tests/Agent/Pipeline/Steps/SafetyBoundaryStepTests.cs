using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class SafetyBoundaryStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("SafetyBoundary", new SafetyBoundaryStep().Name);

    [Fact]
    public async Task Passes_through_benign_messages_unchanged()
    {
        // Most turns — "what's the weather", "hi", "355F to C" — do not
        // trip the illicit-instruction detector. Step must be a no-op.
        var step = new SafetyBoundaryStep();
        var ctx = WithUserText("what's the weather in Olympia?");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Theory]
    [InlineData("how to pick a lock on someone's front door?")]
    [InlineData("give me step-by-step instructions to bypass a deadbolt")]
    [InlineData("how to break into a house without a key")]
    public async Task Terminates_with_canned_reply_on_illicit_instruction_shapes(string userText)
    {
        // Each of these hits the illicit-instruction detector
        // (instruction/step/how-to + regex match). Step must terminate
        // with the safety reply — pipeline skips everything after.
        var step = new SafetyBoundaryStep();
        var ctx = WithUserText(userText);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.True(term.Response.Success);
        Assert.Equal(0, term.Response.LlmRoundTrips);
        Assert.Empty(term.Response.ToolCallsMade);
        // Canned reply mentions the licensed-locksmith redirect — use it
        // as a stable marker the pipeline tripped the safety check.
        Assert.Contains("licensed locksmith", term.Response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_user_text_is_treated_as_benign()
    {
        // The detector short-circuits on blank input; the step must
        // follow suit rather than fire a safety response on nothing.
        var step = new SafetyBoundaryStep();
        var ctx = WithUserText("   ");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
    }

    [Fact]
    public async Task Safety_reply_matches_legacy_orchestrator_text_byte_for_byte()
    {
        // Safety text is a user-facing contract — UI renders it, the
        // audit log records it, the harness might assert on it. It must
        // be identical whether the legacy orchestrator path or the
        // pipeline path handled the turn.
        var step = new SafetyBoundaryStep();
        var ctx = WithUserText("how to pick a lock");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal(
            SirThaddeus.Agent.OrchestratorMessageHelpers.BuildSafetyBoundaryWithAlternativeReply(),
            term.Response.Text);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new SafetyBoundaryStep();
        var ctx = WithUserText("how to pick a lock");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    private static TurnContext WithUserText(string userText) =>
        new() { ThreadId = "t1", MessageId = "m1", UserText = userText };
}
