using SirThaddeus.Agent.Routing;
using System.Text.Json;

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
        var hasExplicitWebLookup = string.Equals(
            IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lower),
            Intents.LookupSearch,
            StringComparison.OrdinalIgnoreCase);
        var requiresLiveVersionLookup = LooksLikeLatestVersionLookup(lower);
        if (!hasExplicitWebLookup && !requiresLiveVersionLookup)
        {
            return null;
        }

        var successfulWebCalls = toolCallsMade
            .Where(call => IsRelevantWebTool(call.ToolName) && !string.IsNullOrWhiteSpace(call.Result))
            .ToList();
        if (successfulWebCalls.Count == 0)
        {
            return null;
        }

        var structuredFailures = successfulWebCalls
            .Select(call => ParseStructuredFailureKind(call.Result))
            .ToList();
        if (structuredFailures.All(kind => kind is not null))
        {
            if (structuredFailures.Any(kind => kind == "timeout"))
                return TimeoutMessage;

            if (structuredFailures.Any(kind => kind == "unavailable"))
                return UnavailableMessage;
        }

        if (successfulWebCalls.Any(call => !LooksLikeWebNoResultsPayload(call.Result)))
            return null;

        if (lower.Contains("timeout", StringComparison.Ordinal))
            return BuildUnavailableResponse(lower, timeout: true);

        return BuildUnavailableResponse(lower, timeout: false);
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
            return BuildUnavailableResponse(lowerUser, timeout: true);
        }

        return BuildUnavailableResponse(lowerUser, timeout: false);
    }

    private static string BuildUnavailableResponse(string lowerUserMessage, bool timeout)
    {
        if (!LooksLikeStrictTwoLineContract(lowerUserMessage))
            return timeout ? TimeoutMessage : UnavailableMessage;

        return timeout
            ? "Answer: Live lookup hit a timeout for this request, so I do not have confirmed results.\n" +
              "Commentary: Please retry in a moment or narrow the query."
            : "Answer: Live lookup is unavailable for this request, so I do not have confirmed results.\n" +
              "Commentary: Please retry in a moment.";
    }

    private static bool LooksLikeLatestVersionLookup(string lowerUserMessage)
    {
        return lowerUserMessage.Contains("latest", StringComparison.Ordinal) &&
               lowerUserMessage.Contains("version", StringComparison.Ordinal) &&
               (lowerUserMessage.Contains("stable", StringComparison.Ordinal) ||
                lowerUserMessage.Contains("current", StringComparison.Ordinal));
    }

    private static bool LooksLikeStrictTwoLineContract(string lowerUserMessage)
    {
        return lowerUserMessage.Contains("exactly two lines", StringComparison.Ordinal) &&
               lowerUserMessage.Contains("line 1 starts with", StringComparison.Ordinal) &&
               lowerUserMessage.Contains("line 2 starts with", StringComparison.Ordinal) &&
               lowerUserMessage.Contains("answer:", StringComparison.Ordinal) &&
               lowerUserMessage.Contains("commentary:", StringComparison.Ordinal);
    }
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

    private static string? ParseStructuredFailureKind(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return null;

        var lower = result.Trim().ToLowerInvariant();
        if (lower.StartsWith("error:", StringComparison.Ordinal))
            return ClassifyFailureText(lower);

        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                return null;
            }

            if (errorEl.ValueKind == JsonValueKind.String)
                return ClassifyFailureText(errorEl.GetString() ?? "");

            if (errorEl.ValueKind != JsonValueKind.Object)
                return null;

            var code = errorEl.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                ? codeEl.GetString() ?? ""
                : "";
            var message = errorEl.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String
                ? messageEl.GetString() ?? ""
                : "";
            return ClassifyFailureText(code + " " + message);
        }
        catch
        {
            return null;
        }
    }

    private static string? ClassifyFailureText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (text.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "timeout";
        }

        if (text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("tool_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "unavailable";
        }

        return null;
    }

}
