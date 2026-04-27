using SirThaddeus.Agent.Guardrails;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that runs the reasoning-guardrails path for questions
/// that benefit from a deliberate, first-principles breakdown before the
/// main tool loop answers. Delegates detection + synthesis to
/// <see cref="ReasoningGuardrailsPipeline"/> (which internally runs a
/// goal-inferencer, entity extractor, and constraint builder); when that
/// returns a result, the step terminates the turn with the guardrails
/// answer + rationale.
///
/// <para>The guardrails pipeline only fires for questions that match its
/// detector heuristics (plus an always-on mode for users who want every
/// turn walked through the first-principles scaffold). For everyday
/// queries the detector returns null and this step is a no-op.</para>
///
/// <para>Place this step <b>after</b> <see cref="FootmanRouterStep"/> so
/// routing + gate happen first, and <b>before</b> <see cref="ToolLoopStep"/>
/// so a guardrails answer bypasses the LLM tool loop entirely.</para>
///
/// <para>No-op when <c>pipeline</c> is null — runtimes that don't want
/// guardrails (older UI builds, smoke tests) compose without it.</para>
/// </summary>
public sealed class GuardrailsStep : ITurnStep
{
    private readonly ReasoningGuardrailsPipeline? _pipeline;
    private readonly string _mode;

    /// <param name="pipeline">The guardrails pipeline. Null = step no-op.</param>
    /// <param name="mode">Guardrails mode from settings
    /// (<c>off</c>/<c>auto</c>/<c>always</c>). Defaults to <c>"auto"</c>.</param>
    public GuardrailsStep(ReasoningGuardrailsPipeline? pipeline, string mode = "auto")
    {
        _pipeline = pipeline;
        _mode = mode ?? "auto";
    }

    public string Name => "Guardrails";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_pipeline is null)
            return new StepResult.Continue(context);

        GuardrailsPipelineResult? result;
        try
        {
            result = await _pipeline
                .TryRunAsync(context.UserText ?? string.Empty, _mode, extraContext: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Guardrails is opportunistic — a failure inside the pipeline
            // must not derail the turn. Fall through to the tool loop.
            return new StepResult.Continue(context);
        }

        if (result is null || string.IsNullOrWhiteSpace(result.AnswerText))
            return new StepResult.Continue(context);

        var response = new AgentResponse
        {
            Text = result.AnswerText,
            Success = true,
            ToolCallsMade = [],
            LlmRoundTrips = result.LlmRoundTrips,
            GuardrailsUsed = true,
            GuardrailsRationale = result.RationaleLines,
        };
        return new StepResult.Terminate(response);
    }
}
