using System.Diagnostics;

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
    private readonly ITurnExecutionControl _executionControl;

    public ChatPipeline(
        IReadOnlyList<ITurnStep> steps,
        Action<string, string>? logEvent = null,
        ITurnExecutionControl? executionControl = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps;
        _logEvent = logEvent;
        _executionControl = executionControl ?? NullTurnExecutionControl.Instance;
    }

    /// <summary>Snapshot of the pipeline's steps, in the order they will run.
    /// Useful for diagnostics and tests.</summary>
    public IReadOnlyList<ITurnStep> Steps => _steps;

    public async Task<AgentResponse> RunAsync(TurnContext initial, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initial);

        var timingEnabled = IsLatencyTracingEnabled();
        var pipelineStarted = timingEnabled ? Stopwatch.GetTimestamp() : 0L;

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
            var steering = await _executionControl.ReachCheckpointAsync(
                current,
                $"pipeline:{step.Name}",
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(steering))
            {
                current = current with
                {
                    LlmMessages =
                    [
                        .. current.LlmMessages,
                        SirThaddeus.LlmClient.ChatMessage.System(
                            $"[USER STEERING]\n{steering.Trim()}\nFollow this correction for all remaining work."),
                    ],
                };
            }
            _logEvent?.Invoke("PIPELINE_STEP_START", $"{i}:{step.Name}");

            var stepStarted = timingEnabled ? Stopwatch.GetTimestamp() : 0L;
            StepResult result;
            try
            {
                result = await step.ExecuteAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                LogStepTiming(initial, i, step.Name, stepStarted, "error", timingEnabled);
                throw;
            }

            switch (result)
            {
                case StepResult.Continue cont:
                    current = cont.Next;
                    LogStepTiming(initial, i, step.Name, stepStarted, "continue", timingEnabled);
                    _logEvent?.Invoke("PIPELINE_STEP_CONTINUE", $"{i}:{step.Name}");
                    break;

                case StepResult.Terminate term:
                    LogStepTiming(initial, i, step.Name, stepStarted, "terminate", timingEnabled);
                    LogPipelineTiming(initial, pipelineStarted, "terminate", timingEnabled);
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
        LogPipelineTiming(initial, pipelineStarted, "exhausted", timingEnabled);
        return AgentResponse.FromError(
            "Chat pipeline completed without producing a response. Check that the final step terminates.");
    }

    private void LogStepTiming(
        TurnContext initial,
        int index,
        string stepName,
        long started,
        string outcome,
        bool enabled)
    {
        if (!enabled || _logEvent is null)
            return;

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _logEvent(
            "PIPELINE_STEP_TIMING",
            $"thread_id={initial.ThreadId} turn_id={initial.MessageId} " +
            $"step_index={index} step={stepName} outcome={outcome} elapsed_ms={elapsedMs:0.###}");
    }

    private void LogPipelineTiming(
        TurnContext initial,
        long started,
        string outcome,
        bool enabled)
    {
        if (!enabled || _logEvent is null)
            return;

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _logEvent(
            "PIPELINE_TIMING",
            $"thread_id={initial.ThreadId} turn_id={initial.MessageId} " +
            $"outcome={outcome} elapsed_ms={elapsedMs:0.###}");
    }

    private static bool IsLatencyTracingEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }
}
