namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Discriminated return value from a pipeline step's execute method.
/// A step either hands a new <see cref="TurnContext"/> to the next step
/// (<see cref="Continue"/>) or finishes the turn with a final response
/// (<see cref="Terminate"/>).
///
/// <para>There is no explicit failure variant. A step that wants to fail
/// returns <c>Terminate(AgentResponse.FromError(...))</c> so failure paths
/// travel through the same event/audit emission as successful responses —
/// the UI never gets a silent drop.</para>
/// </summary>
public abstract record StepResult
{
    // Restricts the hierarchy to the two nested variants below. External
    // code can't subclass StepResult; all consumers can exhaustively
    // pattern-match on Continue / Terminate.
    private protected StepResult() { }

    /// <summary>The step produced an updated context; the pipeline should
    /// invoke the next step with <paramref name="Next"/>.</summary>
    public sealed record Continue(TurnContext Next) : StepResult;

    /// <summary>The step produced a final response; the pipeline should
    /// stop and return <paramref name="Response"/>. Used both for happy
    /// paths (e.g. deterministic utility answer) and for early-exit
    /// failure returns (e.g. permission denied).</summary>
    public sealed record Terminate(AgentResponse Response) : StepResult;
}
