namespace SirThaddeus.Agent.Guardrails;

/// <summary>
/// Default coordinator for first-principles execution policy.
/// </summary>
public sealed class GuardrailsCoordinator : IGuardrailsCoordinator
{
    private readonly ReasoningGuardrailsPipeline _pipeline;

    public GuardrailsCoordinator(ReasoningGuardrailsPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public GuardrailsCoordinatorResult? TryRunDeterministicSpecialCase(string message, string mode)
    {
        var normalizedMode = ReasoningGuardrailsMode.Normalize(mode);
        if (!ReasoningGuardrailsMode.IsEnabled(normalizedMode))
            return null;

        var specialCase = _pipeline.TryRunDeterministicSpecialCase(message);
        return specialCase is null ? null : Map(specialCase);
    }

    public async Task<GuardrailsCoordinatorResult?> TryRunAsync(
        RouterOutput route,
        string message,
        string mode,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldAttemptReasoningGuardrails(route, message))
            return null;

        var result = await _pipeline.TryRunAsync(message, mode, cancellationToken);
        return result is null ? null : Map(result);
    }

    private static GuardrailsCoordinatorResult Map(GuardrailsPipelineResult result)
        => new()
        {
            AnswerText = result.AnswerText,
            RationaleLines = result.RationaleLines,
            TriggerRisk = result.TriggerRisk,
            TriggerWhy = result.TriggerWhy,
            TriggerSource = result.TriggerSource,
            LlmRoundTrips = result.LlmRoundTrips
        };

    private static bool ShouldAttemptReasoningGuardrails(RouterOutput route, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (route.NeedsScreenRead ||
            route.NeedsFileAccess ||
            route.NeedsSystemExecute ||
            route.NeedsBrowserAutomation ||
            route.NeedsMemoryRead ||
            route.NeedsMemoryWrite)
        {
            return false;
        }

        return route.Intent is Intents.ChatOnly or Intents.LookupSearch or Intents.GeneralTool;
    }
}

