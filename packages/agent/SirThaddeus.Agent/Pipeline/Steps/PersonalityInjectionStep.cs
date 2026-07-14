using SirThaddeus.PersonalityEngine;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Applies the active personality to the production prompt and injects its
/// few-shot examples. The composition itself is shared with direct evaluation
/// through <see cref="ProductionPromptComposer"/>.
/// </summary>
public sealed class PersonalityInjectionStep : ITurnStep
{
    private readonly IPersonalityRuntime? _runtime;

    public PersonalityInjectionStep(IPersonalityRuntime? runtime)
    {
        _runtime = runtime;
    }

    public string Name => "PersonalityInjection";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_runtime is null)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var messages = ProductionPromptComposer.ApplyPersonality(
            context.LlmMessages,
            _runtime,
            context.UserText);
        return Task.FromResult<StepResult>(new StepResult.Continue(context with { LlmMessages = messages }));
    }
}
