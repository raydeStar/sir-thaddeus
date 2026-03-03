using SirThaddeus.Agent.Search;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Tiered V2 router that preserves legacy heuristic parity while keeping
/// a lightweight LLM classification fallback.
/// </summary>
public sealed class RouterV2 : IRouter
{
    private readonly DefaultRouter _fallbackRouter;

    public RouterV2(
        ILlmClient llm,
        IDeterministicUtilityEngine deterministicUtilityEngine)
    {
        _fallbackRouter = new DefaultRouter(llm, deterministicUtilityEngine);
    }

    public Task<RouterOutput> RouteAsync(RouterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tier1 = TryRouteTier1(request);
        if (tier1 is not null)
            return Task.FromResult(tier1);

        return _fallbackRouter.RouteAsync(request, cancellationToken);
    }

    /// <summary>
    /// Runs deterministic Tier-1 rules intended to avoid unnecessary LLM
    /// classification for obvious intents. Returns <c>null</c> when the
    /// message should continue through the legacy fallback router.
    /// </summary>
    internal static RouterOutput? TryRouteTier1(RouterRequest request)
    {
        var userMessage = request.UserMessage ?? "";
        var lower = userMessage.Trim().ToLowerInvariant();

        if (IntentFeatureExtractor.LooksLikeReasoningFollowUp(lower) &&
            request is { HasRecentFirstPrinciplesRationale: true })
        {
            return DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: 0.92);
        }

        if (IntentFeatureExtractor.LooksLikeLogicPuzzlePrompt(lower))
            return DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: 0.95);

        if (IntentFeatureExtractor.LooksLikeVoiceMicCheck(lower))
            return DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: 0.98);

        if (SearchModeRouter.IsFollowUpMessage(lower) &&
            request is { HasRecentSearchResults: true })
        {
            return DefaultRouter.MakeRoute(Intents.LookupSearch, confidence: 0.95, needsWeb: true, needsSearch: true, needsBrowser: true);
        }

        if (IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower))
            return DefaultRouter.MakeRoute(Intents.LookupDeepDive, confidence: 0.95, needsWeb: true, needsSearch: true, needsBrowser: true);

        if (IntentFeatureExtractor.LooksLikeScreenRequest(lower))
            return DefaultRouter.MakeRoute(Intents.ScreenObserve, confidence: 0.95, needsScreen: true);

        if (IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower))
            return DefaultRouter.MakeRoute(Intents.LookupNews, confidence: 0.93, needsWeb: true, needsSearch: true);

        if (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lower))
            return DefaultRouter.MakeRoute(Intents.LookupFact, confidence: 0.93, needsWeb: true, needsSearch: true);

        if (IntentFeatureExtractor.LooksLikeFactLookup(lower))
            return DefaultRouter.MakeRoute(Intents.LookupFact, confidence: 0.96, needsWeb: true, needsSearch: true);

        if (IntentFeatureExtractor.LooksLikeExplicitToolInvocation(lower))
            return DefaultRouter.MakeRoute(Intents.GeneralTool, confidence: 0.96);

        return null;
    }
}
