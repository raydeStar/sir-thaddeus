namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Runs an ordered list of <see cref="ITurnStep"/>s against a
/// <see cref="TurnContext"/>. On each step, the runner:
/// <list type="bullet">
///   <item>Threads the context through when the step returns
///         <see cref="StepResult.Continue"/>.</item>
///   <item>Stops and returns when a step returns
///         <see cref="StepResult.Terminate"/> — later steps do not run.</item>
/// </list>
///
/// <para>If every step returns <see cref="StepResult.Continue"/> and the
/// pipeline runs off the end, the runner returns an error response.
/// Well-formed pipelines always include a terminal step (a tool-loop step
/// or a response-composer step) that calls <see cref="StepResult.Terminate"/>.</para>
///
/// <para>The runner is deliberately tiny. Cross-cutting concerns (logging,
/// tracing, metrics) are threaded in via the optional <c>logEvent</c>
/// callback rather than middleware, so a step list reads top-to-bottom as
/// the literal execution order.</para>
/// </summary>
public sealed class ChatPipeline
{
    private readonly IReadOnlyList<ITurnStep> _steps;
    private readonly Action<string, string>? _logEvent;

    public ChatPipeline(
        IReadOnlyList<ITurnStep> steps,
        Action<string, string>? logEvent = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps;
        _logEvent = logEvent;
    }

    /// <summary>Snapshot of the pipeline's steps, in the order they will run.
    /// Useful for diagnostics and tests.</summary>
    public IReadOnlyList<ITurnStep> Steps => _steps;

    public async Task<AgentResponse> RunAsync(TurnContext initial, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initial);

        if (_steps.Count == 0)
        {
            _logEvent?.Invoke("PIPELINE_EMPTY", "no steps configured");
            return AgentResponse.FromError("Pipeline has no steps configured.");
        }

        var current = initial;
        for (var i = 0; i < _steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = _steps[i];
            _logEvent?.Invoke("PIPELINE_STEP_START", $"{i}:{step.Name}");

            var result = await step.ExecuteAsync(current, cancellationToken).ConfigureAwait(false);

            switch (result)
            {
                case StepResult.Continue cont:
                    current = cont.Next;
                    _logEvent?.Invoke("PIPELINE_STEP_CONTINUE", $"{i}:{step.Name}");
                    break;

                case StepResult.Terminate term:
                    _logEvent?.Invoke(
                        "PIPELINE_STEP_TERMINATE",
                        $"{i}:{step.Name} text_len={term.Response.Text?.Length ?? 0} success={term.Response.Success}");
                    return term.Response;
            }
        }

        // Fell off the end without a Terminate. This is a misconfigured
        // pipeline — surface it as a deterministic error rather than
        // silently returning empty text.
        _logEvent?.Invoke("PIPELINE_EXHAUSTED", $"steps={_steps.Count}");
        return AgentResponse.FromError(
            "Chat pipeline completed without producing a response. Check that the final step terminates.");
    }
}
