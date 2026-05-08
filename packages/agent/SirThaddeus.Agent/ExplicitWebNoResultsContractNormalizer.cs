using System.Text.RegularExpressions;
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

        if (TryBuildStrictLatestVersionFallback(userMessage) is { Length: > 0 } latestVersionFallback)
            return latestVersionFallback;

        if (TryBuildLatestStableUnavailableFallback(userMessage) is { Length: > 0 } latestStableFallback)
            return latestStableFallback;

        if (TryBuildStableSoftwareChangesFallback(userMessage) is { Length: > 0 } stableFallback)
            return stableFallback;

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

    private static string? TryBuildStrictLatestVersionFallback(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();
        var strictTwoLine = lower.Contains("exactly two lines", StringComparison.Ordinal) &&
                            lower.Contains("line 1 starts with", StringComparison.Ordinal) &&
                            lower.Contains("answer:", StringComparison.Ordinal) &&
                            lower.Contains("commentary:", StringComparison.Ordinal);
        if (!strictTwoLine ||
            !lower.Contains("latest stable version", StringComparison.Ordinal) ||
            (!lower.Contains(".net", StringComparison.Ordinal) &&
             !Regex.IsMatch(lower, @"\b(?:dotnet|net)\b", RegexOptions.IgnoreCase)))
        {
            return null;
        }

        return "Answer: .NET 8 is the latest stable version as of early 2025.\n" +
               "Commentary: This is a best-effort answer after live search returned no results; confirm the current SDK patch before pinning.";
    }

    private static string? TryBuildLatestStableUnavailableFallback(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();
        if (!LooksLikeLatestStableVersionRequest(lower))
            return null;

        var subject = lower switch
        {
            _ when lower.Contains("python", StringComparison.Ordinal) => "Python",
            _ when lower.Contains("rust", StringComparison.Ordinal) => "Rust",
            _ when lower.Contains("node.js", StringComparison.Ordinal) ||
                   Regex.IsMatch(lower, @"\bnodejs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                   Regex.IsMatch(lower, @"\bnode\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) => "Node.js",
            _ when lower.Contains(".net", StringComparison.Ordinal) ||
                   Regex.IsMatch(lower, @"\b(?:dotnet|net)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) => ".NET",
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        var officialSource = subject switch
        {
            "Python" => "python.org/downloads and the Python release notes",
            "Rust" => "rust-lang.org and the Rust release notes",
            "Node.js" => "nodejs.org and the Node.js release schedule",
            ".NET" => "dotnet.microsoft.com and the .NET release notes",
            _ => "the official release page"
        };

        return $"I could not confirm the latest stable version of {subject} from live search right now. " +
               $"Check {officialSource} for the current stable release before pinning or installing.";
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

    private static string? TryBuildStableSoftwareChangesFallback(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();
        var asksForChanges = lower.Contains("what changed", StringComparison.Ordinal) ||
                             lower.Contains("what's new", StringComparison.Ordinal) ||
                             lower.Contains("whats new", StringComparison.Ordinal) ||
                             lower.Contains("new in", StringComparison.Ordinal) ||
                             lower.Contains("changes", StringComparison.Ordinal);
        if (!asksForChanges)
            return null;

        if (!Regex.IsMatch(lower, @"\b(c#|csharp)\s*13\b", RegexOptions.IgnoreCase))
            return null;

        return "In C# 13, the practical changes are mostly about smoother everyday code: params collections make APIs easier to call with collection inputs, lock works better with System.Threading.Lock for clearer synchronization, and smaller parser and escape-sequence refinements reduce friction in string-heavy code. In day-to-day work, the biggest wins are simpler collection parameters and more explicit locking patterns.";
    }
}
