using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent;

public static class ExplicitWebNoResultsContractNormalizer
{
    public const string TimeoutMessage =
        "Live lookup hit a timeout for this request, so I do not have confirmed results to quote right now. " +
        "Please retry in a moment or narrow the query.";

    public const string UnavailableMessage =
        "Live lookup is unavailable for this request, so I do not have confirmed results to quote right now. " +
        "Please retry in a moment.";

    public static string? TryBuildResponse(
        string? userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || toolCallsMade.Count == 0)
            return null;

        var lower = userMessage.Trim().ToLowerInvariant();
        var isExplicitLookupRequest = string.Equals(
            IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lower),
            Intents.LookupSearch,
            StringComparison.OrdinalIgnoreCase);
        var isLatestStableVersionRequest = LooksLikeLatestStableVersionRequest(lower);
        if (!isExplicitLookupRequest && !isLatestStableVersionRequest)
        {
            return null;
        }

        var successfulWebCalls = toolCallsMade
            .Where(call => IsRelevantWebTool(call.ToolName) && !string.IsNullOrWhiteSpace(call.Result))
            .ToList();
        if (successfulWebCalls.Count == 0 ||
            successfulWebCalls.Any(call => !LooksLikeWebNoResultsPayload(call.Result)))
        {
            return null;
        }

        if (lower.Contains("timeout", StringComparison.Ordinal))
            return TimeoutMessage;

        return UnavailableMessage;
    }

    public static bool ShouldPreserveResponse(
        string? userMessage,
        string? responseText,
        IReadOnlyList<ToolCallRecord>? toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(responseText) || toolCallsMade is null || toolCallsMade.Count == 0)
            return false;

        var normalizedText = TryBuildResponse(userMessage, toolCallsMade);
        return !string.IsNullOrWhiteSpace(normalizedText) &&
               string.Equals(normalizedText, responseText.Trim(), StringComparison.Ordinal);
    }

    public static string? TryBuildResponseFromFailureText(
        string? userMessage,
        string? responseText)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(responseText))
            return null;

        var lowerUser = userMessage.Trim().ToLowerInvariant();
        if (!string.Equals(
                IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lowerUser),
                Intents.LookupSearch,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lowerResponse = responseText.Trim().ToLowerInvariant();
        var lookupFailureLike = lowerResponse.Contains("web_search", StringComparison.Ordinal) ||
                                lowerResponse.Contains("real-time", StringComparison.Ordinal) ||
                                lowerResponse.Contains("real time", StringComparison.Ordinal) ||
                                lowerResponse.Contains("internet", StringComparison.Ordinal) ||
                                lowerResponse.Contains("network", StringComparison.Ordinal) ||
                                lowerResponse.Contains("local-first", StringComparison.Ordinal) ||
                                lowerResponse.Contains("local first", StringComparison.Ordinal) ||
                                lowerResponse.Contains("official_source_search", StringComparison.Ordinal) ||
                                lowerResponse.Contains("cannot access", StringComparison.Ordinal) ||
                                lowerResponse.Contains("can't access", StringComparison.Ordinal) ||
                                lowerResponse.Contains("cannot use", StringComparison.Ordinal) ||
                                lowerResponse.Contains("can't use", StringComparison.Ordinal) ||
                                lowerResponse.Contains("cannot perform", StringComparison.Ordinal) ||
                                lowerResponse.Contains("can't perform", StringComparison.Ordinal) ||
                                lowerResponse.Contains("cannot execute", StringComparison.Ordinal) ||
                                lowerResponse.Contains("timeout", StringComparison.Ordinal) ||
                                lowerResponse.Contains("timed out", StringComparison.Ordinal);
        if (!lookupFailureLike)
            return null;

        if (lowerUser.Contains("timeout", StringComparison.Ordinal) ||
            lowerResponse.Contains("timeout", StringComparison.Ordinal) ||
            lowerResponse.Contains("timed out", StringComparison.Ordinal))
        {
            return TimeoutMessage;
        }

        return UnavailableMessage;
    }

    private static bool LooksLikeLatestStableVersionRequest(string lowerUserMessage)
        => lowerUserMessage.Contains("latest stable version", StringComparison.Ordinal);

    private static bool IsRelevantWebTool(string toolName)
    {
        return toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeWebNoResultsPayload(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return false;

        var lower = result.Trim().ToLowerInvariant();
        return lower.Contains("0 result(s) returned", StringComparison.Ordinal) ||
               lower.Contains("no results", StringComparison.Ordinal) ||
               lower.Contains("no matching", StringComparison.Ordinal);
    }

}
