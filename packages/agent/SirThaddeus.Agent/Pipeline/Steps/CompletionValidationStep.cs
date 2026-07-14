using SirThaddeus.Agent.Validation;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that validates the assistant draft against the user's
/// original request and, when validation fails, runs a targeted repair
/// pass. Delegates to <see cref="CompletionValidator"/> +
/// <see cref="RepairLoop"/> so the legacy orchestrator and the pipeline
/// emit byte-identical repair behavior.
///
/// <para>Place this step <b>after</b> <c>PostProcessStep</c> (validation
/// sees the sanitized draft) and <b>before</b> <c>ResponseComposerStep</c>
/// (so any repaired text becomes the final response).</para>
///
/// <para>No-op when either collaborator is null — runtimes can compose
/// the pipeline without validation if the extra round-trip cost isn't
/// wanted. Also no-op when the draft is blank (composer handles empty
/// drafts with its own deterministic marker).</para>
///
/// <para><b>Fail-open</b>: validator exceptions are treated as "passed"
/// (matches the underlying validator's contract). Repair-loop exceptions
/// leave the original draft intact.</para>
/// </summary>
public sealed class CompletionValidationStep : ITurnStep
{
    private readonly CompletionValidator? _validator;
    private readonly RepairLoop? _repair;
    private readonly Action<string, string>? _log;

    public CompletionValidationStep(
        CompletionValidator? validator,
        RepairLoop? repair,
        Action<string, string>? log = null)
    {
        _validator = validator;
        _repair = repair;
        _log = log;
    }

    public string Name => "CompletionValidation";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_validator is null)
            return new StepResult.Continue(context);

        if (string.IsNullOrWhiteSpace(context.AssistantDraft))
            return new StepResult.Continue(context);

        if (ToolBackedResponseQualityGuards.TryBuildCurrentTimeInLocationFallback(
                context.UserText ?? string.Empty,
                context.ToolCallsMade) is { Length: > 0 } currentTimeDraft)
        {
            return new StepResult.Continue(context with { AssistantDraft = currentTimeDraft });
        }

        if (IsDeterministicLocalBusinessPlacesDraft(context))
            return new StepResult.Continue(context);

        CompletionValidationResult validation;
        try
        {
            validation = await _validator
                .ValidateAsync(
                    userRequest: context.UserText ?? string.Empty,
                    assistantResponse: context.AssistantDraft!,
                    hasToolResults: context.ToolCallsMade.Count > 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Validator's own fail-open contract — a broken LLM call
            // shouldn't abort the turn.
            LogDecision(context, "error_fail_open", passed: true, repairNeeded: false, usedLlm: null, elapsedMs: null);
            return new StepResult.Continue(context);
        }

        LogDecision(
            context,
            "complete",
            validation.Passed,
            validation.RepairNeeded,
            validation.UsedLlm,
            validation.ElapsedMs);

        if (validation.Passed || _repair is null)
            return new StepResult.Continue(context);

        // Validation flagged a miss — try one focused repair pass. The
        // repair loop prompts the LLM with a targeted "you missed X,
        // redo this part" instruction rather than a full re-run.
        RepairResult repair;
        var repairStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            repair = await _repair
                .TryRepairAsync(
                    userRequest: context.UserText ?? string.Empty,
                    failedResponse: context.AssistantDraft!,
                    failedValidation: validation,
                    toolCallsMade: context.ToolCallsMade,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            LogRepair(context, "error_original_retained", repairStarted, changed: false);
            return new StepResult.Continue(context);
        }

        if (string.IsNullOrWhiteSpace(repair.FinalText) ||
            string.Equals(repair.FinalText, context.AssistantDraft, StringComparison.Ordinal))
        {
            LogRepair(context, "complete_original_retained", repairStarted, changed: false);
            return new StepResult.Continue(context);
        }

        // Adopt the repaired text as the new draft. ResponseComposerStep
        // will pick it up as the final response.
        LogRepair(context, "complete_repaired", repairStarted, changed: true);
        return new StepResult.Continue(context with { AssistantDraft = repair.FinalText });
    }

    private void LogDecision(
        TurnContext context,
        string outcome,
        bool passed,
        bool repairNeeded,
        bool? usedLlm,
        double? elapsedMs)
    {
        if (!IsLatencyTracingEnabled() || _log is null)
            return;

        var path = usedLlm switch
        {
            true => "helper_llm",
            false => "heuristic",
            null => "unknown"
        };
        var duration = elapsedMs.HasValue
            ? elapsedMs.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        _log(
            "COMPLETION_VALIDATION_DECISION",
            $"thread_id={context.ThreadId} turn_id={context.MessageId} outcome={outcome} " +
            $"path={path} passed={passed} repair_needed={repairNeeded} elapsed_ms={duration}");
    }

    private void LogRepair(TurnContext context, string outcome, long started, bool changed)
    {
        if (!IsLatencyTracingEnabled() || _log is null)
            return;

        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _log(
            "COMPLETION_REPAIR_TIMING",
            $"thread_id={context.ThreadId} turn_id={context.MessageId} outcome={outcome} " +
            $"changed={changed} elapsed_ms={elapsedMs:0.###}");
    }

    private static bool IsLatencyTracingEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeterministicLocalBusinessPlacesDraft(TurnContext context)
    {
        var draft = context.AssistantDraft ?? string.Empty;
        if (string.IsNullOrWhiteSpace(draft) ||
            !draft.Contains("places_discover/", StringComparison.OrdinalIgnoreCase) ||
            !draft.Contains("I found", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return context.ToolCallsMade.Any(call =>
            call.Success &&
            (string.Equals(call.ToolName, ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(call.ToolName, ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase)));
    }
}
