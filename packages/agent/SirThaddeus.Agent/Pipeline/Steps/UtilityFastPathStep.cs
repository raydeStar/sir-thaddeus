using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that answers deterministic utility queries (unit
/// conversions, percent-of, simple arithmetic, temperature conversions,
/// classic reasoning tripwires) without ever calling the LLM. Wraps the
/// agent-package <see cref="IDeterministicUtilityEngine"/> — if the engine
/// matches, the step terminates the pipeline with the canned answer.
///
/// <para>Place this step <b>before</b> <c>FeatureExtractorStep</c> so
/// utility turns skip every downstream cost: feature extraction, footman
/// classification, LLM round-trip, tool loop. Turns that match answer in
/// microseconds regardless of gatekeeper latency or LM Studio model
/// swaps.</para>
///
/// <para>On a no-match the step returns <see cref="StepResult.Continue"/>
/// unchanged — the later steps handle anything the engine didn't claim.</para>
/// </summary>
public sealed class UtilityFastPathStep : ITurnStep
{
    private readonly IDeterministicUtilityEngine _engine;
    private readonly DeterministicMatchConfidence _minConfidence;

    /// <param name="engine">Utility engine. Defaults to the shared
    /// <see cref="DeterministicUtilityEngineAdapter"/> which wraps
    /// <c>DeterministicPreRouter</c>.</param>
    /// <param name="minConfidence">The lowest confidence the step will
    /// terminate on. Defaults to <see cref="DeterministicMatchConfidence.Medium"/>,
    /// which matches the orchestrator's current policy — high-confidence
    /// matches (strict regex hits) always fire, medium-confidence matches
    /// (conversational wrappers) still fire, only <see cref="DeterministicMatchConfidence.None"/>
    /// passes through.</param>
    public UtilityFastPathStep(
        IDeterministicUtilityEngine? engine = null,
        DeterministicMatchConfidence minConfidence = DeterministicMatchConfidence.Medium)
    {
        _engine = engine ?? new DeterministicUtilityEngineAdapter();
        _minConfidence = minConfidence;
    }

    public string Name => "UtilityFastPath";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.UserText))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var match = _engine.TryMatch(context.UserText);
        if (match is null || match.Confidence < _minConfidence)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        // Deterministic answer wins the turn. No tools, no LLM; surface
        // the canned answer and stop the pipeline.
        var response = new AgentResponse
        {
            Text = match.Result.Answer,
            Success = true,
        };
        return Task.FromResult<StepResult>(new StepResult.Terminate(response));
    }
}
