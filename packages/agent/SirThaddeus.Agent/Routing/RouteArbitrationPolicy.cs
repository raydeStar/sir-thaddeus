using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Routing;

internal static class RouteArbitrationPolicy
{
    public static bool ShouldRunFootmanForRoute(
        RouterOutput route,
        string lowerIncoming,
        IntentFeatureExtractor.WebLookupHeuristicEvidence webEvidence)
    {
        var tier = ActionTierClassifier.Classify(route, lowerIncoming, webEvidence);

        if (tier == ActionTier.RetrievalSafeLocal)
            return false;

        if (tier == ActionTier.PlanComplex)
            return true;

        if (route.Intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerIncoming))
        {
            return false;
        }

        if (route.Intent.Equals(Intents.LookupNews, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lowerIncoming))
        {
            return false;
        }

        if (route.Intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) &&
            webEvidence.ShouldLookup &&
            route.Confidence >= 0.88 &&
            !LooksLikeWeatherSensitiveLookup(lowerIncoming))
        {
            return false;
        }

        if (route.Intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lowerIncoming))
        {
            return false;
        }

        if (route.Intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase) &&
            SearchModeRouter.IsFollowUpMessage(lowerIncoming))
        {
            return false;
        }

        if (route.Intent.Equals(Intents.ScreenObserve, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeScreenRequest(lowerIncoming))
        {
            return false;
        }

        if (route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
            (IntentFeatureExtractor.LooksLikeFileRequest(lowerIncoming) ||
             string.Equals(
                 IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lowerIncoming),
                 Intents.FileTask,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (route.Confidence < 0.95)
            return true;

        if (IsLookupIntent(route.Intent))
            return true;

        return route.NeedsWeb || route.NeedsSearch || route.NeedsBrowserAutomation;
    }

    public static bool ShouldBlockFootmanLookupDowngrade(
        string lowerIncoming,
        RouterOutput baseRoute,
        RouterOutput footmanRoute,
        IntentFeatureExtractor.WebLookupHeuristicEvidence webEvidence,
        FootmanBlockReason blockReason = FootmanBlockReason.None)
    {
        if (!IsLookupIntent(baseRoute.Intent))
            return false;

        if (IsLookupIntent(footmanRoute.Intent))
            return false;

        var tier = ActionTierClassifier.Classify(baseRoute, lowerIncoming, webEvidence);
        if (tier == ActionTier.RetrievalSafeLocal ||
            tier == ActionTier.RetrievalSafeExternal)
        {
            return !FootmanBlockReasonPolicy.IsValidBlockForTier(blockReason, tier);
        }

        if (baseRoute.Intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerIncoming))
        {
            return true;
        }

        if (baseRoute.Intent.Equals(Intents.LookupNews, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lowerIncoming))
        {
            return true;
        }

        if (baseRoute.Intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) &&
            IsLookupFloorEligiblePrompt(lowerIncoming) &&
            webEvidence.ShouldLookup)
        {
            return true;
        }

        if (baseRoute.Intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static string? GetLookupFloorIntent(
        string lowerIncoming,
        RouterOutput finalRoute,
        IntentFeatureExtractor.WebLookupHeuristicEvidence webEvidence)
    {
        if (IsLookupIntent(finalRoute.Intent))
            return null;

        if (!IsLookupFloorEligiblePrompt(lowerIncoming))
            return null;

        if (IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerIncoming))
            return Intents.LookupDeepDive;

        if (IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lowerIncoming))
            return Intents.LookupNews;

        if (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lowerIncoming))
            return Intents.LookupFact;

        if (webEvidence.ShouldLookup)
            return Intents.LookupFact;

        return null;
    }

    private static bool IsLookupFloorEligiblePrompt(string lowerIncoming)
    {
        if (string.IsNullOrWhiteSpace(lowerIncoming))
            return false;

        if (IntentFeatureExtractor.LooksLikeGreeting(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeConversationalCheckIn(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeVoiceMicCheck(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeFileRequest(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeScreenRequest(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeSystemCommand(lowerIncoming) ||
            lowerIncoming.Contains("tell me about yourself", StringComparison.Ordinal) ||
            lowerIncoming.Contains("about your favorite", StringComparison.Ordinal) ||
            lowerIncoming.Contains("favorite thing", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what do you think", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what's your opinion", StringComparison.Ordinal) ||
            lowerIncoming.Contains("whats your opinion", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what's your take", StringComparison.Ordinal) ||
            lowerIncoming.Contains("whats your take", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public static bool IsLookupIntent(string intent) =>
        intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupNews, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeWeatherSensitiveLookup(string lowerIncoming)
    {
        if (string.IsNullOrWhiteSpace(lowerIncoming))
            return false;

        return lowerIncoming.Contains("weather", StringComparison.Ordinal) ||
               lowerIncoming.Contains("forecast", StringComparison.Ordinal) ||
               lowerIncoming.Contains("temperature", StringComparison.Ordinal) ||
               lowerIncoming.Contains("humidity", StringComparison.Ordinal) ||
               lowerIncoming.Contains("wind", StringComparison.Ordinal) ||
               lowerIncoming.Contains("rain", StringComparison.Ordinal) ||
               lowerIncoming.Contains("snow", StringComparison.Ordinal);
    }
}