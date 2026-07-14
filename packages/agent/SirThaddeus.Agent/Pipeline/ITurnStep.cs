namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// One unit of chat-turn processing. Steps are stateless collaborators:
/// they take a <see cref="TurnContext"/>, inspect or enrich it, and return
/// a <see cref="StepResult"/> telling the pipeline runner what to do next.
///
/// <para>Steps hold no per-turn or cross-turn state. Anything that needs to
/// survive beyond the call lives on <see cref="TurnContext"/> (flows to the
/// next step) or in a long-lived dependency injected through the step's
/// constructor (an LLM client, a configuration provider, etc.).</para>
///
/// <para>Implementations must be safe to call concurrently — different
/// threads may run the same step instance against different contexts at
/// the same time.</para>
/// </summary>
public interface ITurnStep
{
    /// <summary>Short symbolic name used in logs and the event stream.
    /// Conventionally the step class name without the <c>Step</c> suffix
    /// (e.g. <c>"FeatureExtractor"</c>, <c>"FootmanRouter"</c>).</summary>
    string Name { get; }

    /// <summary>Run the step. Return <see cref="StepResult.Continue"/>
    /// with an updated context to let the pipeline advance; return
    /// <see cref="StepResult.Terminate"/> to finalise the turn with an
    /// <see cref="AgentResponse"/>. Must honour
    /// <paramref name="cancellationToken"/>.</summary>
    Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken);
}
