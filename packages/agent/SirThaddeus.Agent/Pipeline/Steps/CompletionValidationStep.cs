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

    public CompletionValidationStep(CompletionValidator? validator, RepairLoop? repair)
    {
        _validator = validator;
        _repair = repair;
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
            return new StepResult.Continue(context);
        }

        if (validation.Passed || _repair is null)
            return new StepResult.Continue(context);

        // Validation flagged a miss — try one focused repair pass. The
        // repair loop prompts the LLM with a targeted "you missed X,
        // redo this part" instruction rather than a full re-run.
        RepairResult repair;
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
            return new StepResult.Continue(context);
        }

        if (string.IsNullOrWhiteSpace(repair.FinalText) ||
            string.Equals(repair.FinalText, context.AssistantDraft, StringComparison.Ordinal))
        {
            return new StepResult.Continue(context);
        }

        // Adopt the repaired text as the new draft. ResponseComposerStep
        // will pick it up as the final response.
        return new StepResult.Continue(context with { AssistantDraft = repair.FinalText });
    }
}
