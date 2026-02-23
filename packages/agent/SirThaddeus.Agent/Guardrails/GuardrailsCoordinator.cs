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
        string? extraContext = null,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldAttemptReasoningGuardrails(route, message))
            return null;

        var result = await _pipeline.TryRunAsync(message, mode, extraContext, cancellationToken);
        if (result is null)
            return null;

        if (IsLookupIntent(route.Intent) && LooksLikeLowConfidenceAnswer(result.AnswerText))
            return null;

        return Map(result);
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


    private static bool IsLookupIntent(string intent) =>
        intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupNews, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeLowConfidenceAnswer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var lower = text.Trim().ToLowerInvariant();
        ReadOnlySpan<string> markers =
        [
            "depends",
            "it depends",
            "might",
            "could",
            "may",
            "not sure",
            "i'm not sure",
            "im not sure",
            "i don't know",
            "i dont know",
            "likely",
            "possibly",
            "perhaps"
        ];

        foreach (var marker in markers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ShouldAttemptReasoningGuardrails(RouterOutput route, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (route.NeedsScreenRead ||
            route.NeedsFileAccess ||
            route.NeedsSystemExecute ||
            route.NeedsBrowserAutomation)
        {
            return false;
        }

        return route.Intent is Intents.ChatOnly or Intents.LookupSearch or Intents.LookupFact or Intents.LookupNews or Intents.LookupDeepDive or Intents.GeneralTool or Intents.MemoryRead or Intents.MemoryWrite;
    }
}

