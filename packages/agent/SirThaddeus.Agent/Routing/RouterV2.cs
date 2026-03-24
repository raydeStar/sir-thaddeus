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

    /// <summary>
    /// Creates a new <see cref="RouterV2"/> backed by the given LLM client and
    /// deterministic utility engine.
    /// </summary>
    /// <param name="llm">LLM client used by the fallback router for classification.</param>
    /// <param name="deterministicUtilityEngine">Engine for math/conversion shortcuts.</param>
    public RouterV2(
        ILlmClient llm,
        IDeterministicUtilityEngine deterministicUtilityEngine)
    {
        _fallbackRouter = new DefaultRouter(llm, deterministicUtilityEngine);
    }

    /// <inheritdoc />
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

        if (IntentFeatureExtractor.LooksLikeStrayTranscriptFragment(lower))
            return DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: 0.92);

        var looksLikeHistoricalKnowledgeAsk =
            lower.Contains("historical figure", StringComparison.Ordinal) ||
            (lower.Contains("historical", StringComparison.Ordinal) &&
             lower.Contains("figure", StringComparison.Ordinal));
        var looksLikeCurrentInfoLookup =
            lower.Contains("what happened", StringComparison.Ordinal) ||
            lower.Contains("news", StringComparison.Ordinal) ||
            lower.Contains("last week", StringComparison.Ordinal) ||
            lower.Contains("latest", StringComparison.Ordinal);

        if (IntentFeatureExtractor.LooksLikePreferenceOrOpinionPrompt(lower) &&
            !looksLikeHistoricalKnowledgeAsk)
        {
            return DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: 0.96);
        }

        if (IntentFeatureExtractor.LooksLikeSelfContainedReasoningPrompt(lower) &&
            !IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower) &&
            !IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lower) &&
            !looksLikeCurrentInfoLookup)
            return DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: 0.96);

        var strongContinuationPhrase =
            lower.Contains("anything else", StringComparison.Ordinal) ||
            lower.Contains("what else", StringComparison.Ordinal);

        if (SearchModeRouter.IsFollowUpMessage(lower) &&
            (request is { HasRecentSearchResults: true } || strongContinuationPhrase))
        {
            return DefaultRouter.MakeRoute(Intents.LookupSearch, confidence: 0.95, needsWeb: true, needsSearch: true, needsBrowser: true);
        }

        if (IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower))
            return DefaultRouter.MakeRoute(Intents.LookupDeepDive, confidence: 0.95, needsWeb: true, needsSearch: true, needsBrowser: true);

        if (IntentFeatureExtractor.LooksLikeScreenRequest(lower))
            return DefaultRouter.MakeRoute(Intents.ScreenObserve, confidence: 0.95, needsScreen: true);

        if (IntentFeatureExtractor.LooksLikeFileRequest(lower))
            return DefaultRouter.MakeRoute(Intents.FileTask, confidence: 0.95, needsFile: true);

        if (IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower))
            return DefaultRouter.MakeRoute(Intents.LookupNews, confidence: 0.93, needsWeb: true, needsSearch: true);

        if (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lower))
            return DefaultRouter.MakeRoute(Intents.LookupFact, confidence: 0.93, needsWeb: true, needsSearch: true);

        if (IntentFeatureExtractor.LooksLikeFactLookup(lower))
            return DefaultRouter.MakeRoute(
                Intents.LookupFact,
                confidence: ComputeFactLookupConfidence(lower),
                needsWeb: true,
                needsSearch: true);

        var explicitToolIntent = IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lower);
        if (!string.IsNullOrWhiteSpace(explicitToolIntent))
        {
            return explicitToolIntent switch
            {
                Intents.FileTask => DefaultRouter.MakeRoute(Intents.FileTask, confidence: 0.97, needsFile: true),
                Intents.ScreenObserve => DefaultRouter.MakeRoute(Intents.ScreenObserve, confidence: 0.97, needsScreen: true),
                Intents.SystemTask => DefaultRouter.MakeRoute(Intents.SystemTask, confidence: 0.97, needsSystem: true, risk: "medium"),
                Intents.LookupSearch => DefaultRouter.MakeRoute(Intents.LookupSearch, confidence: 0.97, needsWeb: true, needsSearch: true, needsBrowser: true),
                Intents.MemoryWrite => DefaultRouter.MakeRoute(Intents.MemoryWrite, confidence: 0.97, needsMemoryWrite: true),
                _ => DefaultRouter.MakeRoute(Intents.GeneralTool, confidence: 0.96)
            };
        }

        if (IntentFeatureExtractor.LooksLikeGreetingOnlyOrSmallTalk(lower))
            return DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: 0.94);

        return null;
    }

    private static double ComputeFactLookupConfidence(string lower)
    {
        var evidence = IntentFeatureExtractor.GetWebLookupHeuristicEvidence(lower);
        if (evidence.ShouldLookup)
            return Math.Clamp(Math.Max(0.88, evidence.Confidence), 0.88, 0.96);

        return 0.96;
    }
}
