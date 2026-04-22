namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that augments the system prompt with the logic-puzzle
/// decomposition scaffold from <see cref="OrchestratorPrompts.LogicPuzzleDecompositionModeSuffix"/>
/// when the current turn looks like a reasoning trap. Matches the behaviour
/// the CLI + harness get through the agent orchestrator so the desktop UI
/// doesn't fall down on "car wash / walk or drive"-style trick questions.
///
/// <para>Reads <see cref="TurnContext.Features"/> (must be populated by a
/// prior <c>FeatureExtractorStep</c>). Does nothing unless
/// <see cref="Routing.RoutingFeatures.IsLogicPuzzle"/> is true.</para>
///
/// <para>The scaffold is appended to the <b>first</b> system message in
/// <see cref="TurnContext.LlmMessages"/>. Facades conventionally seed one
/// system message as the first entry; if no system message is present, the
/// step inserts one. Subsequent steps (automation-run suffix, onboarding,
/// memory-context) can continue to append to the same seed without knowing
/// about each other.</para>
/// </summary>
public sealed class LogicPuzzleScaffoldStep : ITurnStep
{
    public string Name => "LogicPuzzleScaffold";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Features must have been extracted upstream. Missing features =
        // the caller forgot to wire FeatureExtractorStep; prefer a
        // no-op over a silent wrong answer.
        var features = context.Features;
        if (features is null || !features.IsLogicPuzzle)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var updated = PromptSuffixAppender.Append(
            context.LlmMessages,
            OrchestratorPrompts.LogicPuzzleDecompositionModeSuffix);

        return Task.FromResult<StepResult>(
            new StepResult.Continue(context with { LlmMessages = updated }));
    }
}
