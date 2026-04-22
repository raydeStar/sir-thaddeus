namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that injects an onboarding suffix into the system
/// prompt when the user is new (no profile, no stored memory) or only
/// partially known (profile present but identity still missing). Mirrors
/// the orchestrator's <c>OnboardingColdPrompt</c> / <c>OnboardingFollowUpPrompt</c>
/// path so the desktop UI produces the same warm-introduction behavior
/// the CLI + harness do.
///
/// <para>The step does not compute onboarding state itself — that depends
/// on runtime-specific signals (memory tool responses, profile presence).
/// Instead it accepts a <c>Func&lt;TurnContext, OnboardingMode&gt;</c>
/// resolver so the facade can wire in whatever signal it has. When the
/// resolver returns <see cref="OnboardingMode.NotNeeded"/>, the step is a
/// no-op.</para>
///
/// <para>Place this step <b>after</b> <see cref="MemoryContextStep"/> and
/// <b>before</b> the tool loop so the onboarding suffix lands next to the
/// base system prompt and is visible on the model's very first call.</para>
/// </summary>
public sealed class OnboardingInjectionStep : ITurnStep
{
    private readonly Func<TurnContext, OnboardingMode> _resolveMode;

    public OnboardingInjectionStep(Func<TurnContext, OnboardingMode> resolveMode)
    {
        _resolveMode = resolveMode ?? throw new ArgumentNullException(nameof(resolveMode));
    }

    public string Name => "OnboardingInjection";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var mode = _resolveMode(context);
        var suffix = mode switch
        {
            OnboardingMode.Cold => OrchestratorPrompts.OnboardingColdPrompt,
            OnboardingMode.FollowUp => OrchestratorPrompts.OnboardingFollowUpPrompt,
            _ => null,
        };

        if (suffix is null)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var updated = PromptSuffixAppender.Append(context.LlmMessages, suffix);
        return Task.FromResult<StepResult>(
            new StepResult.Continue(context with { LlmMessages = updated }));
    }
}

/// <summary>
/// Onboarding state resolved from runtime signals (memory / profile /
/// conversation history). Pipeline-side representation so steps don't
/// need to depend on runtime-specific types.
/// </summary>
public enum OnboardingMode
{
    /// <summary>User is already known; skip onboarding. Most common case.</summary>
    NotNeeded,

    /// <summary>First contact — no profile, no memory. Use the warm
    /// introduction + "ask who they are" prompt.</summary>
    Cold,

    /// <summary>Profile exists but identity still missing after a few turns.
    /// Use the softer "I still don't know you" follow-up.</summary>
    FollowUp,
}
