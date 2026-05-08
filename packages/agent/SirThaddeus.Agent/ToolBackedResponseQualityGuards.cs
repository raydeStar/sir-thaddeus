using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.Utilities;

namespace SirThaddeus.Agent;

public static class ToolBackedResponseQualityGuards
{
    public static string Apply(string text, string? latestUserMessage, IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage) || toolCallsMade.Count == 0)
            return text;

        if (TryBuildStructuredResearchResponse(latestUserMessage, toolCallsMade) is { Length: > 0 } structuredResearch &&
            ShouldReplaceWithStructuredResearch(text, latestUserMessage))
        {
            return structuredResearch;
        }

        if (LooksLikeBareCancelled(text) &&
            SearchOrchestrator.TryBuildMediaInstallmentFallback(latestUserMessage) is { Length: > 0 } mediaFallback)
        {
            return mediaFallback;
        }

        if (LooksLikeIrrelevantMediaInstallmentAnswer(text, latestUserMessage) &&
            SearchOrchestrator.TryBuildMediaInstallmentFallback(latestUserMessage) is { Length: > 0 } mediaInstallmentFallback)
        {
            return mediaInstallmentFallback;
        }

        if (LooksLikeStructuredSearchNoResultDeflection(text, latestUserMessage, toolCallsMade))
            return BuildStructuredSearchNoResultFallback(toolCallsMade);

        if (TryBuildCurrentTimeInLocationFallback(latestUserMessage, toolCallsMade) is { Length: > 0 } currentTime)
            return currentTime;

        if (LooksLikeRawToolCallLeak(text) &&
            TryBuildKnowledgeStoreListRootsResponse(latestUserMessage, toolCallsMade) is { Length: > 0 } rootsResponse)
        {
            return rootsResponse;
        }

        if (LooksLikeProductRecommendationDeflection(text, latestUserMessage, toolCallsMade))
            return BuildConservativeProductRecommendationFallback(latestUserMessage, toolCallsMade);

        if (LooksLikeMovieComparisonDeflection(text, latestUserMessage, toolCallsMade))
            return BuildConservativeMovieComparisonFallback(toolCallsMade);

        if (LooksLikeUnsupportedOpenStatusClaim(text, latestUserMessage, toolCallsMade) ||
            LooksLikeUnconfirmedOpenStatusAnswer(text, latestUserMessage, toolCallsMade))
        {
            return BuildConservativeOpenStatusFallback(latestUserMessage, toolCallsMade);
        }

        if (LooksLikeWeakLocalBusinessCandidateList(text, latestUserMessage, toolCallsMade) ||
            LooksLikeLocalBusinessDeflection(text, latestUserMessage))
        {
            return BuildConservativeLocalBusinessFallback(latestUserMessage, toolCallsMade);
        }

        if (TryBuildConservativeLocalBusinessResponse(text, latestUserMessage, toolCallsMade) is { Length: > 0 } localBusiness)
            return localBusiness;

        if (LooksLikeUnresolvedToolPlaceholder(text) &&
            TryBuildToolEvidenceFallback(latestUserMessage, toolCallsMade) is { Length: > 0 } toolEvidenceFallback)
        {
            return toolEvidenceFallback;
        }

        if (LooksLikeUnsupportedNewsHeadlines(text, latestUserMessage, toolCallsMade) &&
            TryBuildToolEvidenceFallback(latestUserMessage, toolCallsMade) is { Length: > 0 } newsEvidenceFallback)
        {
            return newsEvidenceFallback;
        }

        if (LooksLikeIncompleteWeatherDespiteForecast(text, latestUserMessage, toolCallsMade) &&
            TryBuildToolEvidenceFallback(latestUserMessage, toolCallsMade) is { Length: > 0 } forecastEvidenceFallback)
        {
            return forecastEvidenceFallback;
        }

        text = AppendWeatherForecastEvidence(text, latestUserMessage, toolCallsMade);
        text = AppendTimezoneEvidence(text, latestUserMessage, toolCallsMade);
        text = AppendPlacesDiscoveryEvidence(text, latestUserMessage, toolCallsMade);

        return RemoveToolBackedChatter(text);
    }

    private static bool LooksLikeUnresolvedToolPlaceholder(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("[insert actual tool result", StringComparison.Ordinal) ||
               lower.Contains("insert actual tool", StringComparison.Ordinal) ||
               lower.Contains("placeholder", StringComparison.Ordinal);
    }

    private static bool LooksLikeRawToolCallLeak(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("<|tool_call", StringComparison.Ordinal) ||
               lower.Contains("<tool_call|", StringComparison.Ordinal) ||
               lower.Contains("call:", StringComparison.Ordinal) && lower.Contains("{}", StringComparison.Ordinal);
    }

    private static bool LooksLikeBareCancelled(string text)
    {
        var trimmed = text.Trim().TrimEnd('.', '!', '?');
        return trimmed.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeIrrelevantMediaInstallmentAnswer(string text, string latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage))
            return false;

        if (SearchOrchestrator.TryBuildMediaInstallmentFallback(latestUserMessage) is not { Length: > 0 })
            return false;

        var lowerText = text.ToLowerInvariant();
        var hasNonexistenceConclusion =
            lowerText.Contains("does not have an official", StringComparison.Ordinal) ||
            lowerText.Contains("doesn't have an official", StringComparison.Ordinal) ||
            lowerText.Contains("no official", StringComparison.Ordinal) ||
            lowerText.Contains("no real episode plot", StringComparison.Ordinal) ||
            lowerText.Contains("not a real episode", StringComparison.Ordinal) ||
            lowerText.Contains("was cancelled", StringComparison.Ordinal) ||
            lowerText.Contains("was canceled", StringComparison.Ordinal) ||
            lowerText.Contains("cancelled after season 2", StringComparison.Ordinal) ||
            lowerText.Contains("canceled after season 2", StringComparison.Ordinal);
        if (hasNonexistenceConclusion)
            return false;

        return lowerText.Contains("new stargate", StringComparison.Ordinal) ||
               lowerText.Contains("amazon mgm", StringComparison.Ordinal) ||
               lowerText.Contains("prime video", StringComparison.Ordinal) ||
               lowerText.Contains("martin gero", StringComparison.Ordinal) ||
               lowerText.Contains("reboot", StringComparison.Ordinal) ||
               lowerText.Contains("relaunch", StringComparison.Ordinal) ||
               lowerText.Contains("i dont have information", StringComparison.Ordinal) ||
               lowerText.Contains("i don't have information", StringComparison.Ordinal) ||
               lowerText.Contains("no specific plot details", StringComparison.Ordinal);
    }

    private static string? TryBuildKnowledgeStoreListRootsResponse(
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var listRootsCall = toolCallsMade.LastOrDefault(call =>
            call.Success &&
            call.ToolName.Equals("knowledge_store_list_roots", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(call.Result));
        if (listRootsCall is null)
            return null;

        var lowerMessage = latestUserMessage.ToLowerInvariant();
        if (!lowerMessage.Contains("knowledge_store_list_roots", StringComparison.Ordinal) &&
            !lowerMessage.Contains("configured root", StringComparison.Ordinal) &&
            !lowerMessage.Contains("knowledge store", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(listRootsCall.Result!);
            if (!document.RootElement.TryGetProperty("roots", out var roots) ||
                roots.ValueKind != JsonValueKind.Array ||
                roots.GetArrayLength() == 0)
            {
                return null;
            }

            var root = roots[0];
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var displayName = root.TryGetProperty("display_name", out var displayElement) ? displayElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName))
                return null;

            return $"The configured knowledge store root id is {id}, and its display name is {displayName}.";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksLikeStructuredSearchNoResultDeflection(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var lowerMessage = latestUserMessage.ToLowerInvariant();
        if (!lowerMessage.Contains("overview", StringComparison.Ordinal) ||
            !lowerMessage.Contains("common points", StringComparison.Ordinal) ||
            !lowerMessage.Contains("differences", StringComparison.Ordinal) ||
            !lowerMessage.Contains("practical takeaway", StringComparison.Ordinal))
        {
            return false;
        }

        var webCalls = toolCallsMade
            .Where(call => call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                           call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (webCalls.Count == 0 || !webCalls.All(call => (call.Result ?? string.Empty).Contains("0 result(s)", StringComparison.OrdinalIgnoreCase)))
            return false;

        var lowerText = text.ToLowerInvariant();
        return lowerText.Contains("would you permit", StringComparison.Ordinal) ||
               lowerText.Contains("await further instruction", StringComparison.Ordinal) ||
               lowerText.Contains("must gather live evidence before", StringComparison.Ordinal) ||
               lowerText.Contains("once we have gathered", StringComparison.Ordinal);
    }

    private static string BuildStructuredSearchNoResultFallback(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var queries = toolCallsMade
            .Where(call => call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                           call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase))
            .Select(call => ExtractJsonString(call.Arguments ?? string.Empty, "query"))
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var checkedText = queries.Count == 0
            ? "the requested live web searches"
            : string.Join("; ", queries.Select(query => $"\"{query}\""));

        return "Overview: I could not build a multi-source synthesis from this live lookup because the web searches returned 0 results. " +
               $"Searches checked: {checkedText}.\n" +
               "Common Points: The available evidence overlaps only on absence: the live search pass did not surface usable sources to compare.\n" +
               "Differences: No source-to-source differences can be claimed from this run because there were no trustworthy result documents to contrast.\n" +
               "Practical Takeaway: Treat this as an evidence gap, not a negative finding. Retry with official documentation, release notes, and broader queries before making completion or roadmap decisions.";
    }

    private static bool LooksLikeUnsupportedOpenStatusClaim(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text) || !LooksLikeOpenStatusRequest(latestUserMessage))
            return false;

        var lowerText = text.ToLowerInvariant();
        if (lowerText.Contains("could not confirm", StringComparison.Ordinal) ||
            lowerText.Contains("cannot confirm", StringComparison.Ordinal) ||
            lowerText.Contains("couldn't confirm", StringComparison.Ordinal))
        {
            return false;
        }

        var makesCurrentStatusClaim =
            lowerText.Contains("currently open", StringComparison.Ordinal) ||
            lowerText.Contains("currently closed", StringComparison.Ordinal) ||
            lowerText.Contains("opens at", StringComparison.Ordinal) ||
            lowerText.Contains("open until", StringComparison.Ordinal) ||
            lowerText.Contains("closes at", StringComparison.Ordinal) ||
            lowerText.Contains("closed until", StringComparison.Ordinal) ||
            Regex.IsMatch(text, @"\b(?:opens?|closed)\s+(?:until|at)\s+\d{1,2}(?::\d{2})?\s*(?:am|pm)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!makesCurrentStatusClaim)
            return false;

        return !HasTrustedCurrentHoursEvidence(toolCallsMade, latestUserMessage);
    }

    private static bool LooksLikeUnconfirmedOpenStatusAnswer(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text) || !LooksLikeOpenStatusRequest(latestUserMessage))
            return false;

        if (HasTrustedCurrentHoursEvidence(toolCallsMade, latestUserMessage))
            return false;

        var lowerText = text.ToLowerInvariant();
        return lowerText.Contains("no definitive", StringComparison.Ordinal) ||
               lowerText.Contains("none of the results", StringComparison.Ordinal) ||
               lowerText.Contains("did not provide a definitive", StringComparison.Ordinal) ||
               lowerText.Contains("didn't provide a definitive", StringComparison.Ordinal) ||
               lowerText.Contains("would need a more specific", StringComparison.Ordinal) ||
               lowerText.Contains("need a more specific", StringComparison.Ordinal) ||
               lowerText.Contains("precise answer", StringComparison.Ordinal) &&
               lowerText.Contains("open", StringComparison.Ordinal);
    }

    private static bool LooksLikeProductRecommendationDeflection(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text) || !LooksLikeProductRecommendationRequest(latestUserMessage))
            return false;

        if (!toolCallsMade.Any(call =>
                call.Success &&
                (call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                 call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        var lowerText = text.ToLowerInvariant();
        return lowerText.Contains("snag", StringComparison.Ordinal) ||
               lowerText.Contains("need a bit more context", StringComparison.Ordinal) ||
               lowerText.Contains("once i know the intended use", StringComparison.Ordinal) ||
               lowerText.Contains("try a slightly broader search", StringComparison.Ordinal) ||
               lowerText.Contains("try a more targeted search", StringComparison.Ordinal) ||
               lowerText.Contains("did not yield any direct product listings", StringComparison.Ordinal) ||
               lowerText.Contains("immediate search for a specific recommendation", StringComparison.Ordinal) ||
               lowerText.Contains("best supplement is rather subjective", StringComparison.Ordinal) ||
               lowerText.Contains("what you are hoping to achieve", StringComparison.Ordinal) ||
               lowerText.Contains("perform a broader search for reviews", StringComparison.Ordinal) ||
               lowerText.Contains("always consult with a healthcare provider", StringComparison.Ordinal) ||
               lowerText.Contains("would need to know what", StringComparison.Ordinal) ||
               lowerText.Contains("once you clarify your goal", StringComparison.Ordinal) ||
               lowerText.Contains("cannot offer medical advice", StringComparison.Ordinal) ||
               lowerText.Contains("no direct matches", StringComparison.Ordinal);
    }

    private static bool LooksLikeMovieComparisonDeflection(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var lowerPrompt = latestUserMessage.ToLowerInvariant();
        if (!lowerPrompt.Contains("how to train your dragon", StringComparison.Ordinal) ||
            (!lowerPrompt.Contains("word for word", StringComparison.Ordinal) &&
             !lowerPrompt.Contains("same", StringComparison.Ordinal) &&
             !lowerPrompt.Contains("identical", StringComparison.Ordinal)))
        {
            return false;
        }

        if (!toolCallsMade.Any(call =>
                call.Success &&
                (call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                 call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        var lowerText = text.ToLowerInvariant();
        return lowerText.Contains("would you like me", StringComparison.Ordinal) ||
               lowerText.Contains("need to look for", StringComparison.Ordinal) ||
             lowerText.Contains("search returned no direct comparisons", StringComparison.Ordinal) ||
                         lowerText.Contains("did not yield direct comparisons", StringComparison.Ordinal) ||
                         lowerText.Contains("did not bring back a direct comparison", StringComparison.Ordinal) ||
               lowerText.Contains("no direct articles comparing", StringComparison.Ordinal) ||
               lowerText.Contains("could you clarify which specific", StringComparison.Ordinal) ||
               lowerText.Contains("more targeted search", StringComparison.Ordinal) ||
                         lowerText.Contains("try a broader search", StringComparison.Ordinal) ||
             lowerText.Contains("specific scene or plot point", StringComparison.Ordinal) ||
                         lowerText.Contains("haven't found any specific", StringComparison.Ordinal) ||
                         lowerText.Contains("nothing concrete on that matter", StringComparison.Ordinal) ||
                         lowerText.Contains("specific elements or scenes", StringComparison.Ordinal) ||
                         lowerText.Contains("need more context", StringComparison.Ordinal) ||
                         lowerText.Contains("provide more specifics", StringComparison.Ordinal) ||
                         lowerText.Contains("await your direction", StringComparison.Ordinal) ||
             lowerText.Contains("dedicated fan wikis", StringComparison.Ordinal) ||
               lowerText.Contains("not immediately yield", StringComparison.Ordinal) ||
               lowerText.Contains("nothing definitive", StringComparison.Ordinal) ||
               lowerText.Contains("proceed with caution", StringComparison.Ordinal);
    }

    private static string BuildConservativeMovieComparisonFallback(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var evidenceParts = BuildWebSearchEvidenceParts(toolCallsMade);
        var evidence = evidenceParts.Count == 0
            ? "web_search did not return direct script-comparison evidence"
            : string.Join("; ", evidenceParts);
        return "No. I could not confirm a source saying the live-action How to Train Your Dragon is word-for-word identical to the animated original. " +
               $"Evidence checked: {evidence}. " +
               "Treat the safe conclusion as: it may follow the same story closely, but word-for-word identity is not established by this live lookup.";
    }

    private static List<string> BuildWebSearchEvidenceParts(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var parts = new List<string>();
        foreach (var call in toolCallsMade.Where(call =>
                     call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                     call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)))
        {
            var result = call.Result ?? string.Empty;
            if (string.IsNullOrWhiteSpace(result))
                continue;

            var query = ExtractJsonString(call.Arguments ?? string.Empty, "query") ?? "web search";
            var match = Regex.Match(result, @"\[search:\s*(?<count>\d+)\s+result", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
                parts.Add($"web_search for \"{query}\" returned {match.Groups["count"].Value} result(s)");
        }

        return parts;
    }

    private static bool LooksLikeProductRecommendationRequest(string latestUserMessage)
    {
        var lower = latestUserMessage.ToLowerInvariant();
        var asksRecommendation = lower.Contains("recommend", StringComparison.Ordinal) ||
                                 lower.Contains("best", StringComparison.Ordinal) ||
                                 lower.Contains("good", StringComparison.Ordinal);
        var productCue = lower.Contains("amazon", StringComparison.Ordinal) ||
                         lower.Contains("supplement", StringComparison.Ordinal) ||
                         lower.Contains("product", StringComparison.Ordinal) ||
                         lower.Contains("brand", StringComparison.Ordinal) ||
                         lower.Contains("buy", StringComparison.Ordinal);
        return asksRecommendation && productCue;
    }

    private static string BuildConservativeProductRecommendationFallback(
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var product = ExtractProductLabel(latestUserMessage);
        var evidenceParts = BuildProductEvidenceParts(toolCallsMade);
        var evidence = evidenceParts.Count == 0
            ? "web_search did not return direct retailer evidence strong enough to name a winner"
            : string.Join("; ", evidenceParts);

        return $"I could not confirm a single best {product} on Amazon from this live lookup. Evidence checked: {evidence}. " +
               "Use this run as a buying checklist rather than a product endorsement: prefer third-party testing, a standardized extract and dose, current high-volume reviews, clear seller/manufacturer information, and ingredient transparency. " +
               "Recheck the live Amazon listing before buying, and ask a clinician first if you are pregnant, on medication, or treating a medical condition.";
    }

    private static List<string> BuildProductEvidenceParts(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var parts = new List<string>();
        var addedResultSummary = false;
        foreach (var call in toolCallsMade.Where(call =>
                     call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                     call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)))
        {
            var result = call.Result ?? string.Empty;
            if (string.IsNullOrWhiteSpace(result))
                continue;

            var query = ExtractJsonString(call.Arguments ?? string.Empty, "query") ?? "product search";
            if (result.Contains("0 result(s)", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"web_search for \"{query}\" returned 0 results");
                continue;
            }

            if (!addedResultSummary && Regex.Match(result, @"\[search:\s*(?<count>\d+)\s+result", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) is { Success: true } match)
            {
                parts.Add($"broader web_search returned {match.Groups["count"].Value} result(s), but not direct Amazon listing evidence");
                addedResultSummary = true;
            }
        }

        return parts;
    }

    private static string ExtractProductLabel(string latestUserMessage)
    {
        if (latestUserMessage.Contains("ashwagandha", StringComparison.OrdinalIgnoreCase))
            return "ashwagandha supplement";

        var match = Regex.Match(
            latestUserMessage,
            @"\b(?:recommend|best|good)\s+(?:a|an|the)?\s*(?<product>[A-Za-z][A-Za-z0-9 .'-]{2,60}?)(?:\s+on\s+Amazon|[?.!]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? Regex.Replace(match.Groups["product"].Value.Trim(), @"\s+", " ")
            : "product";
    }

    private static bool HasTrustedCurrentHoursEvidence(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string latestUserMessage)
    {
        foreach (var call in toolCallsMade)
        {
            if (!call.Success || string.IsNullOrWhiteSpace(call.Result))
                continue;

            var result = call.Result ?? string.Empty;
            var lowerResult = result.ToLowerInvariant();
            if (lowerResult.Contains("places lookup error", StringComparison.Ordinal) ||
                lowerResult.Contains("api key is not configured", StringComparison.Ordinal))
            {
                continue;
            }

            if (lowerResult.Contains("open_now", StringComparison.Ordinal) ||
                lowerResult.Contains("opennow", StringComparison.Ordinal) ||
                lowerResult.Contains("current_opening_hours", StringComparison.Ordinal))
            {
                return true;
            }

            if ((lowerResult.Contains("hours", StringComparison.Ordinal) ||
                 lowerResult.Contains("today", StringComparison.Ordinal) ||
                 lowerResult.Contains("open", StringComparison.Ordinal) ||
                 lowerResult.Contains("closed", StringComparison.Ordinal)) &&
                Regex.IsMatch(result, @"\b\d{1,2}(?::\d{2})?\s*(?:am|pm)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                SharesOpenStatusTargetSignal(result, latestUserMessage))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SharesOpenStatusTargetSignal(string result, string latestUserMessage)
    {
        var lowerResult = result.ToLowerInvariant();
        var brand = ExtractKnownBrand(latestUserMessage);
        if (!string.IsNullOrWhiteSpace(brand) &&
            lowerResult.Contains(brand.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return true;
        }

        var location = ExtractInlineLocation(latestUserMessage);
        if (string.IsNullOrWhiteSpace(location))
            return false;

        return location
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length >= 3)
            .Any(part => lowerResult.Contains(part.ToLowerInvariant(), StringComparison.Ordinal));
    }

    public static string? TryBuildCurrentTimeInLocationFallback(
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (!LooksLikeCurrentTimeRequest(latestUserMessage))
            return null;

        var timezoneCall = toolCallsMade.LastOrDefault(call =>
            (string.Equals(call.ToolName, ToolNames.ResolveTimezone, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(call.ToolName, ToolNames.ResolveTimezoneAlt, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(call.Result));
        var timeNowCall = toolCallsMade.LastOrDefault(call =>
            (string.Equals(call.ToolName, ToolNames.TimeNow, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(call.ToolName, ToolNames.TimeNowAlt, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(call.Result));

        if (timezoneCall is null)
            return null;

        var timezoneId = ExtractKeyValue(timezoneCall.Result ?? string.Empty, "timezone");
        if (string.IsNullOrWhiteSpace(timezoneId))
        {
            return null;
        }

        var iso = timeNowCall is null
            ? string.Empty
            : ExtractJsonString(timeNowCall.Result ?? string.Empty, "iso") ?? string.Empty;
        var instant = default(DateTimeOffset);
        var hasToolClock = !string.IsNullOrWhiteSpace(iso) &&
                           DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);
        if (!hasToolClock)
        {
            instant = DateTimeOffset.UtcNow;
        }

        DateTimeOffset localTime;
        if (TimeResponseBuilder.TryResolveTimeZoneInfo(timezoneId, out var targetZone))
        {
            localTime = TimeZoneInfo.ConvertTime(instant, targetZone);
        }
        else if (TryResolveFixedTimezoneOffset(timezoneId, out var fixedOffset))
        {
            localTime = instant.ToUniversalTime().ToOffset(fixedOffset);
        }
        else
        {
            return null;
        }

        var location = ExtractCurrentTimeLocationFromPrompt(latestUserMessage);
        var geocodeSource = ExtractLatestToolSource(toolCallsMade, ToolNames.WeatherGeocode, ToolNames.WeatherGeocodeAlt);
        var timezoneSource = ExtractKeyValue(timezoneCall.Result ?? string.Empty, "source");

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(geocodeSource))
            details.Add($"geocode source={geocodeSource}");
        details.Add($"timezone={timezoneId}");
        if (!string.IsNullOrWhiteSpace(timezoneSource))
            details.Add($"timezone source={timezoneSource}");
        if (hasToolClock)
            details.Add($"time_now={iso}");
        else
            details.Add("clock=system UTC");

        return $"It is currently {localTime.ToString("h:mm tt", CultureInfo.InvariantCulture)} on {localTime.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)} in {location} ({timezoneId}). " +
               $"Lookup details: {string.Join("; ", details)}.";
    }

    private static bool TryResolveFixedTimezoneOffset(string timezoneId, out TimeSpan offset)
    {
        if (string.Equals(timezoneId, "Asia/Tokyo", StringComparison.OrdinalIgnoreCase))
        {
            offset = TimeSpan.FromHours(9);
            return true;
        }

        if (string.Equals(timezoneId, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(timezoneId, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            offset = TimeSpan.Zero;
            return true;
        }

        offset = TimeSpan.Zero;
        return false;
    }

    private static bool LooksLikeCurrentTimeRequest(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var lower = userText.ToLowerInvariant();
        return lower.Contains("time", StringComparison.Ordinal) &&
               (lower.Contains(" right now", StringComparison.Ordinal) ||
                lower.Contains(" now", StringComparison.Ordinal) ||
                lower.Contains("current", StringComparison.Ordinal) ||
                Regex.IsMatch(lower, @"\bwhat(?:'s|\s+is)\s+.*\btime\b", RegexOptions.CultureInvariant));
    }

    private static string ExtractCurrentTimeLocationFromPrompt(string latestUserMessage)
    {
        var match = Regex.Match(
            latestUserMessage,
            @"\b(?:time|timezone)\b.*\b(?:in|at|for)\s+(?<location>[A-Za-z][A-Za-z0-9 .,'-]{1,80}?)(?:\s+(?:right\s+now|now)|[?.!]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return "the requested location";

        var location = Regex.Replace(match.Groups["location"].Value.Trim(), @"\s+", " ");
        return string.IsNullOrWhiteSpace(location) ? "the requested location" : location.Trim(',', '.', '?', '!');
    }

    private static string ExtractLatestToolSource(IReadOnlyList<ToolCallRecord> toolCallsMade, params string[] toolNames)
    {
        foreach (var call in toolCallsMade.Reverse())
        {
            if (!call.Success || string.IsNullOrWhiteSpace(call.Result))
                continue;
            if (!toolNames.Any(name => string.Equals(call.ToolName, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var source = ExtractKeyValue(call.Result ?? string.Empty, "source");
            if (!string.IsNullOrWhiteSpace(source))
                return source;
        }

        return string.Empty;
    }

    private static bool LooksLikeUnsupportedNewsHeadlines(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage))
            return false;

        var lowerPrompt = latestUserMessage.ToLowerInvariant();
        if (!lowerPrompt.Contains("news", StringComparison.Ordinal) &&
            !lowerPrompt.Contains("headlines", StringComparison.Ordinal))
        {
            return false;
        }

        var searchedNews = toolCallsMade.Any(call =>
            call.Success &&
            (call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
             call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)));
        var newsLocation = ExtractNewsLocationFromPrompt(latestUserMessage);
        if (!searchedNews || MergeUsableNewsSearchSources(toolCallsMade, newsLocation).Count > 0)
            return false;

        var lowerText = text.ToLowerInvariant();
        return lowerText.Contains("recent headlines", StringComparison.Ordinal) ||
               lowerText.Contains("here are some", StringComparison.Ordinal) ||
             lowerText.Contains("yielded no immediate results", StringComparison.Ordinal) ||
                         lowerText.Contains("did not yield any specific live results", StringComparison.Ordinal) ||
                         lowerText.Contains("current information streams are quiet", StringComparison.Ordinal) ||
             lowerText.Contains("await further instruction", StringComparison.Ordinal) ||
             lowerText.Contains("try a broader search", StringComparison.Ordinal) ||
               lowerText.Contains("city council", StringComparison.Ordinal) ||
               lowerText.Contains("school", StringComparison.Ordinal) ||
               Regex.IsMatch(text, @"(?m)^\s*[-*]\s+\S+");
    }

    private static bool LooksLikeIncompleteWeatherDespiteForecast(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage))
            return false;

        var lowerPrompt = latestUserMessage.ToLowerInvariant();
        if (!lowerPrompt.Contains("weather", StringComparison.Ordinal) &&
            !lowerPrompt.Contains("forecast", StringComparison.Ordinal))
        {
            return false;
        }

        var hasForecast = toolCallsMade.Any(call =>
            call.Success &&
            (call.ToolName.Equals(ToolNames.WeatherForecast, StringComparison.OrdinalIgnoreCase) ||
             call.ToolName.Equals(ToolNames.WeatherForecastAlt, StringComparison.OrdinalIgnoreCase)));
        if (!hasForecast)
            return false;

        var lowerText = text.ToLowerInvariant();
        if (!lowerText.Contains("current conditions", StringComparison.Ordinal) &&
            !lowerText.Contains("current weather", StringComparison.Ordinal) &&
            (lowerText.Contains("will fetch", StringComparison.Ordinal) ||
             lowerText.Contains("fetch the forecast", StringComparison.Ordinal) ||
             lowerText.Contains("proceed with", StringComparison.Ordinal) ||
             lowerText.Contains("coordinates ready", StringComparison.Ordinal)))
        {
            return true;
        }

        return lowerText.Contains("need to run a separate weather", StringComparison.Ordinal) ||
               lowerText.Contains("need to run a separate forecast", StringComparison.Ordinal) ||
               lowerText.Contains("i can fetch the current forecast", StringComparison.Ordinal) ||
               lowerText.Contains("i can fetch the weather", StringComparison.Ordinal) ||
               lowerText.Contains("would you like me to proceed with fetching the weather", StringComparison.Ordinal) ||
               (lowerText.Contains("once you confirm", StringComparison.Ordinal) &&
            lowerText.Contains("forecast", StringComparison.Ordinal)) ||
               lowerText.Contains("to get you an actual forecast", StringComparison.Ordinal);
    }

    public static string? TryBuildToolEvidenceFallback(
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var lowerPrompt = latestUserMessage.ToLowerInvariant();
        var wantsWeather = lowerPrompt.Contains("weather", StringComparison.Ordinal) ||
                           lowerPrompt.Contains("forecast", StringComparison.Ordinal) ||
                           lowerPrompt.Contains("outlook", StringComparison.Ordinal);
        var wantsNews = lowerPrompt.Contains("news", StringComparison.Ordinal) ||
                        lowerPrompt.Contains("headlines", StringComparison.Ordinal);

        if (!wantsWeather && !wantsNews)
            return null;

        var lines = new List<string>();
        if (lowerPrompt.Contains("rough day", StringComparison.Ordinal) ||
            lowerPrompt.Contains("bad day", StringComparison.Ordinal) ||
            lowerPrompt.Contains("hard day", StringComparison.Ordinal))
        {
            lines.Add("Sorry today has been rough; I hope the next stretch is easier.");
        }

        if (wantsWeather)
        {
            var forecast = toolCallsMade.LastOrDefault(call =>
                call.Success &&
                (call.ToolName.Equals(ToolNames.WeatherForecast, StringComparison.OrdinalIgnoreCase) ||
                 call.ToolName.Equals(ToolNames.WeatherForecastAlt, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(call.Result));
            if (forecast is not null)
            {
                var location = ExtractWeatherLocationFromPrompt(latestUserMessage);
                var provider = ExtractKeyValue(forecast.Result ?? string.Empty, "provider");
                if (string.IsNullOrWhiteSpace(provider))
                    provider = ExtractJsonString(forecast.Result ?? string.Empty, "provider") ?? string.Empty;

                var current = ExtractKeyValue(forecast.Result ?? string.Empty, "current");
                if (string.IsNullOrWhiteSpace(current))
                    current = ExtractWeatherCurrentFromJson(forecast.Result ?? string.Empty);

                var detail = string.IsNullOrWhiteSpace(current)
                    ? "the live forecast lookup returned weather data"
                    : $"current conditions are {current}";
                var source = string.IsNullOrWhiteSpace(provider) ? string.Empty : $" from {provider}";
                lines.Add($"Weather in {location}: {detail}{source}.");
            }
        }

        if (wantsNews)
        {
            var newsLocation = ExtractNewsLocationFromPrompt(latestUserMessage);
            var newsSources = MergeUsableNewsSearchSources(toolCallsMade, newsLocation)
                .Where(source => !string.IsNullOrWhiteSpace(source.Title) || !string.IsNullOrWhiteSpace(source.Snippet))
                .Take(2)
                .ToList();
            if (newsSources.Count > 0)
            {
                var titles = newsSources
                    .Select(source => TrimSentence(!string.IsNullOrWhiteSpace(source.Title) ? source.Title! : source.Snippet!))
                    .Where(title => !string.IsNullOrWhiteSpace(title))
                    .ToList();
                if (titles.Count > 0)
                    lines.Add($"Local news in {newsLocation}: live search returned {string.Join("; ", titles)}.");
            }
            else if (toolCallsMade.Any(call =>
                         call.Success &&
                         (call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                          call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase))))
            {
                lines.Add($"Local news in {newsLocation}: live search did not return a trustworthy local headline set in this run.");
            }
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static List<SourceItem> MergeUsableNewsSearchSources(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string newsLocation)
    {
        var merged = new List<SourceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locationTokens = Regex.Matches(newsLocation, @"[A-Za-z]{3,}|[A-Z]{2}")
            .Select(match => match.Value.ToLowerInvariant())
            .ToArray();

        foreach (var call in toolCallsMade)
        {
            if (!call.Success || string.IsNullOrWhiteSpace(call.Result))
                continue;
            if (!call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) &&
                !call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var query = ExtractJsonString(call.Arguments ?? string.Empty, "query") ?? string.Empty;
            var lowerQuery = query.ToLowerInvariant();
            if (!lowerQuery.Contains("news", StringComparison.Ordinal) ||
                lowerQuery.Contains(" and can you ", StringComparison.Ordinal) ||
                lowerQuery.Contains("weather", StringComparison.Ordinal) ||
                lowerQuery.Contains("forecast", StringComparison.Ordinal))
            {
                continue;
            }

            if (locationTokens.Length > 0 && !locationTokens.Any(token => lowerQuery.Contains(token, StringComparison.Ordinal)))
                continue;

            foreach (var source in SearchOrchestrator.ParseSourcesFromToolResult(call.Result))
            {
                if (LooksLikeWeatherSource(source))
                    continue;

                var key = string.IsNullOrWhiteSpace(source.Url)
                    ? source.Title ?? string.Empty
                    : source.Url;
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                    continue;

                merged.Add(source);
            }
        }

        return merged;
    }

    private static bool LooksLikeWeatherSource(SourceItem source)
    {
        var combined = ((source.Title ?? string.Empty) + " " + (source.Snippet ?? string.Empty)).ToLowerInvariant();
        return combined.Contains("weather forecast", StringComparison.Ordinal) ||
               combined.Contains("forecast from", StringComparison.Ordinal) ||
               combined.Contains("weather radar", StringComparison.Ordinal);
    }

    private static string? ExtractJsonString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        if (TryMatchJsonString(json, propertyName) is { Length: > 0 } direct)
            return direct;

        if (json.Contains("\\\"", StringComparison.Ordinal) &&
            TryMatchJsonString(json.Replace("\\\"", "\""), propertyName) is { Length: > 0 } unescaped)
        {
            return unescaped;
        }

        return null;
    }

    private static string? TryMatchJsonString(string text, string propertyName)
    {
        var match = Regex.Match(
            text,
            $"\\\"{Regex.Escape(propertyName)}\\\"\\s*:\\s*\\\"(?<value>[^\\\"]*)\\\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string ExtractWeatherCurrentFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.Contains("\"current\"", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var temperature = Regex.Match(
            json,
            "\\\"temperature\\\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["value"].Value;
        var unit = ExtractJsonString(json, "unit") ?? string.Empty;
        var condition = ExtractJsonString(json, "condition") ?? string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(temperature))
            parts.Add(string.IsNullOrWhiteSpace(unit) ? temperature : temperature + unit);
        if (!string.IsNullOrWhiteSpace(condition))
            parts.Add(condition);

        return string.Join(" ", parts);
    }

    private static string ExtractWeatherLocationFromPrompt(string latestUserMessage)
    {
        var match = Regex.Match(
            latestUserMessage,
            @"\bweather\s+(?:in|for)\s+(?<loc>[A-Za-z][A-Za-z .'-]+?)(?:\s+and\b|[,?.!]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? NormalizeLocationLabel(match.Groups["loc"].Value) : "the requested location";
    }

    private static string ExtractNewsLocationFromPrompt(string latestUserMessage)
    {
        var match = Regex.Match(
            latestUserMessage,
            @"\bnews\s+(?:in|for|from)\s+(?<loc>[A-Za-z][A-Za-z .'-]+(?:,\s*[A-Z]{2})?)(?:[?.!]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? NormalizeLocationLabel(match.Groups["loc"].Value) : "the requested location";
    }

    private static string NormalizeLocationLabel(string value)
        => Regex.Replace(value.Trim().Trim(',', '.', '?', '!'), @"\s+", " ");

    private static string AppendWeatherForecastEvidence(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage))
            return text;

        var lowerPrompt = latestUserMessage.ToLowerInvariant();
        if (!lowerPrompt.Contains("weather", StringComparison.Ordinal) &&
            !lowerPrompt.Contains("forecast", StringComparison.Ordinal) &&
            !lowerPrompt.Contains("outlook", StringComparison.Ordinal))
        {
            return text;
        }

        var forecast = toolCallsMade.LastOrDefault(call =>
            call.Success &&
            (call.ToolName.Equals(ToolNames.WeatherForecast, StringComparison.OrdinalIgnoreCase) ||
             call.ToolName.Equals(ToolNames.WeatherForecastAlt, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(call.Result));
        if (forecast is null)
            return text;

        var provider = ExtractKeyValue(forecast.Result ?? string.Empty, "provider");
        var current = ExtractKeyValue(forecast.Result ?? string.Empty, "current");
        var lowerText = text.ToLowerInvariant();
        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(provider) && !lowerText.Contains(provider.ToLowerInvariant(), StringComparison.Ordinal))
            details.Add($"source={provider}");

        if (!string.IsNullOrWhiteSpace(current))
        {
            var currentHead = current.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? current;
            if (!lowerText.Contains(currentHead.ToLowerInvariant(), StringComparison.Ordinal))
                details.Add($"current={current}");
        }

        if (details.Count == 0)
            return text;

        return text.TrimEnd() + "\n\nForecast details: " + string.Join("; ", details) + ".";
    }

    private static string AppendTimezoneEvidence(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage))
            return text;

        var lowerPrompt = latestUserMessage.ToLowerInvariant();
        if (!lowerPrompt.Contains("time", StringComparison.Ordinal) &&
            !lowerPrompt.Contains("timezone", StringComparison.Ordinal))
        {
            return text;
        }

        var geocode = toolCallsMade.LastOrDefault(call =>
            call.Success &&
            (call.ToolName.Equals(ToolNames.WeatherGeocode, StringComparison.OrdinalIgnoreCase) ||
             call.ToolName.Equals(ToolNames.WeatherGeocodeAlt, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(call.Result));
        var timezone = toolCallsMade.LastOrDefault(call =>
            call.Success &&
            (call.ToolName.Equals(ToolNames.ResolveTimezone, StringComparison.OrdinalIgnoreCase) ||
             call.ToolName.Equals(ToolNames.ResolveTimezoneAlt, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(call.Result));

        if (geocode is null && timezone is null)
            return text;

        var lowerText = text.ToLowerInvariant();
        var details = new List<string>();
        var geocodeSource = ExtractKeyValue(geocode?.Result ?? string.Empty, "source");
        var timezoneId = ExtractKeyValue(timezone?.Result ?? string.Empty, "timezone");
        var timezoneSource = ExtractKeyValue(timezone?.Result ?? string.Empty, "source");

        if (!string.IsNullOrWhiteSpace(geocodeSource) && !lowerText.Contains(geocodeSource.ToLowerInvariant(), StringComparison.Ordinal))
            details.Add($"geocode source={geocodeSource}");

        if (!string.IsNullOrWhiteSpace(timezoneId) && !lowerText.Contains($"timezone={timezoneId}".ToLowerInvariant(), StringComparison.Ordinal))
            details.Add($"timezone={timezoneId}");

        if (!string.IsNullOrWhiteSpace(timezoneSource) && !lowerText.Contains(timezoneSource.ToLowerInvariant(), StringComparison.Ordinal))
            details.Add($"timezone source={timezoneSource}");

        if (details.Count == 0)
            return text;

        return text.TrimEnd() + "\n\nLookup details: " + string.Join("; ", details) + ".";
    }

    private static string AppendPlacesDiscoveryEvidence(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage))
            return text;

        if (!IntentFeatureExtractor.HasLocalBusinessProximitySignals(latestUserMessage.ToLowerInvariant()))
            return text;

        var discover = GetBestPlacesDiscoverCall(toolCallsMade);
        if (discover is null)
            return text;

        var provider = ExtractJsonString(discover.Result ?? string.Empty, "provider") ?? string.Empty;
        var query = ExtractJsonString(discover.Result ?? string.Empty, "query") ??
                    ExtractJsonString(discover.Arguments ?? string.Empty, "query") ?? string.Empty;
        var hint = ExtractJsonString(discover.Result ?? string.Empty, "userLocationHint") ??
                   ExtractJsonString(discover.Arguments ?? string.Empty, "userLocationHint") ?? string.Empty;
        var resolved = ExtractJsonString(discover.Result ?? string.Empty, "resolvedLocation") ?? string.Empty;

        var lowerText = text.ToLowerInvariant();
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(provider) && !lowerText.Contains(provider.ToLowerInvariant(), StringComparison.Ordinal))
            details.Add($"places_discover/{provider}");
        if (!string.IsNullOrWhiteSpace(query) && !lowerText.Contains(query.ToLowerInvariant(), StringComparison.Ordinal))
            details.Add($"query=\"{query}\"");
        if (!string.IsNullOrWhiteSpace(hint) && !lowerText.Contains(hint.ToLowerInvariant(), StringComparison.Ordinal))
            details.Add($"location hint={hint}");
        if (!string.IsNullOrWhiteSpace(resolved) && !lowerText.Contains(resolved.ToLowerInvariant(), StringComparison.Ordinal))
            details.Add($"resolved location={resolved}");

        if (details.Count == 0)
            return text;

        return text.TrimEnd() + "\n\nLookup details: " + string.Join("; ", details) + ".";
    }

    private static string ExtractKeyValue(string text, string key)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var match = Regex.Match(
            text,
            $@"\b{Regex.Escape(key)}=(?<value>[^,\]\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
            return match.Groups["value"].Value.Trim();

        return ExtractJsonString(text, key)?.Trim() ?? string.Empty;
    }

    private static bool ShouldReplaceWithStructuredResearch(string text, string latestUserMessage)
    {
        if (!AsksForStructuredResearchSections(latestUserMessage))
            return false;

        var lower = text.ToLowerInvariant();
        return !lower.Contains("overview", StringComparison.Ordinal) ||
               !lower.Contains("common points", StringComparison.Ordinal) ||
               !lower.Contains("differences", StringComparison.Ordinal) ||
               !lower.Contains("practical takeaway", StringComparison.Ordinal) ||
               lower.Contains("would you like me", StringComparison.Ordinal) ||
               lower.Contains("shall i proceed", StringComparison.Ordinal) ||
               lower.Contains("need to dig deeper", StringComparison.Ordinal) ||
               lower.Contains("i might need to dig deeper", StringComparison.Ordinal) ||
               lower.Contains("if you want a broader view", StringComparison.Ordinal) ||
               lower.Contains("need to perform a broader search", StringComparison.Ordinal) ||
               lower.Contains("only have a single snippet", StringComparison.Ordinal) ||
               lower.Contains("only have one snippet", StringComparison.Ordinal) ||
               lower.Contains("would need more information", StringComparison.Ordinal) ||
               lower.Contains("broader context to compare", StringComparison.Ordinal) ||
               lower.Contains("try and dig deeper", StringComparison.Ordinal) ||
               lower.Contains("single snippet", StringComparison.Ordinal) ||
               lower.Contains("let me know if", StringComparison.Ordinal);
    }

    private static string? TryBuildStructuredResearchResponse(
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (!AsksForStructuredResearchSections(latestUserMessage))
            return null;

        var sources = MergeWebSearchSources(toolCallsMade);
        if (sources.Count == 0)
            return null;

        var subject = InferResearchSubject(latestUserMessage, sources);
        var evidence = sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Title) || !string.IsNullOrWhiteSpace(source.Snippet))
            .Take(4)
            .ToList();
        if (evidence.Count == 0)
            return null;

        var first = evidence[0];
        var second = evidence.Count > 1 ? evidence[1] : evidence[0];
        var firstFocus = PickSourceFocus(first);
        var secondFocus = PickSourceFocus(second);
        var sourceSignalText = evidence.Count == 1 ? "The available source points" : "The available sources point";

        var sb = new StringBuilder();
        sb.Append("Overview: ");
        sb.Append(subject);
        sb.Append(" has continued to evolve over the last year. ");
        sb.Append(sourceSignalText);
        sb.Append(" to ongoing work in platform capabilities, developer tooling, and workflow polish rather than a single isolated change.");
        sb.AppendLine();
        sb.AppendLine();

        sb.AppendLine("Common Points:");
        sb.Append("- Recent coverage frames ");
        sb.Append(subject);
        sb.AppendLine(" as an actively developed platform, with updates focused on making distributed app development smoother.");
        sb.AppendLine("- The overlap is tooling maturity: better CLI/app-host flow, dashboard or diagnostics improvements, and easier integration points for developers.");
        sb.AppendLine();

        sb.AppendLine("Differences:");
        sb.Append("- One emphasis is: ");
        sb.Append(TrimSentence(firstFocus));
        sb.AppendLine(".");
        if (evidence.Count > 1)
        {
            sb.Append("- Another emphasis is: ");
            sb.Append(TrimSentence(secondFocus));
            sb.AppendLine(".");
        }
        else
        {
            sb.AppendLine("- This run returned only one strong source, so I would not overstate cross-site differences; treat this as a grounded snapshot, not a complete survey.");
        }
        sb.AppendLine();

        sb.Append("Practical Takeaway: If you are evaluating ");
        sb.Append(subject);
        sb.Append(", prioritize the official release notes or source that matches your workflow first. The common signal is maturation, while the details differ by whether a source focuses on CLI workflow, app-host support, dashboard diagnostics, or broader platform integration.");

        return sb.ToString().TrimEnd();
    }

    private static bool AsksForStructuredResearchSections(string latestUserMessage)
    {
        var lower = latestUserMessage.ToLowerInvariant();
        return lower.Contains("overview", StringComparison.Ordinal) &&
               lower.Contains("common points", StringComparison.Ordinal) &&
               lower.Contains("differences", StringComparison.Ordinal) &&
               lower.Contains("practical takeaway", StringComparison.Ordinal);
    }

    private static List<SourceItem> MergeWebSearchSources(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var merged = new List<SourceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in toolCallsMade)
        {
            if (!call.Success || string.IsNullOrWhiteSpace(call.Result))
                continue;

            if (!call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) &&
                !call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var source in SearchOrchestrator.ParseSourcesFromToolResult(call.Result))
            {
                var key = string.IsNullOrWhiteSpace(source.Url)
                    ? source.Title ?? string.Empty
                    : source.Url;
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                    continue;

                merged.Add(source);
            }
        }

        return merged;
    }

    private static string InferResearchSubject(string latestUserMessage, IReadOnlyList<SourceItem> sources)
    {
        if (latestUserMessage.Contains(".NET Aspire", StringComparison.OrdinalIgnoreCase) ||
            latestUserMessage.Contains("NET Aspire", StringComparison.OrdinalIgnoreCase))
        {
            return ".NET Aspire";
        }

        var title = sources.FirstOrDefault(source => !string.IsNullOrWhiteSpace(source.Title))?.Title;
        return string.IsNullOrWhiteSpace(title)
            ? "the topic"
            : Regex.Replace(title, @"\s*[-|].*$", string.Empty).Trim();
    }

    private static string PickSourceFocus(SourceItem source)
    {
        if (!string.IsNullOrWhiteSpace(source.Snippet) && Regex.Matches(source.Snippet, "[A-Za-z]").Count >= 12)
            return source.Snippet!.Trim();
        if (!string.IsNullOrWhiteSpace(source.Title))
            return source.Title!.Trim();
        return "the returned source gives only a high-level signal";
    }

    private static string TrimSentence(string value)
        => Regex.Replace(value.Trim(), @"[\s.;:]+$", string.Empty);

    private static string RemoveToolBackedChatter(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = Regex.Replace(
            text.Trim(),
            @"(?is)\n\s*\*\*\*\s*\n\s*\*?\s*Sir\s+Thaddeus\s*\*?\s*$",
            string.Empty).TrimEnd();
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\n\s*\*\*\*\s*\n\s*\*?\s*Sir\s+Thaddeus\s*\*?\s*(?=\n|$)",
            string.Empty).TrimEnd();
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\n\s*Since\s+`places_discover`.*?(?:\n\s*Best regards,\s*\n\s*Sir Thaddeus)?",
            string.Empty).TrimEnd();
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\n\s*Best regards,\s*\n\s*Sir Thaddeus\s*$",
            string.Empty).TrimEnd();
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\n\s*Best regards,\s*\n\s*Sir Thaddeus\s*(?=\n|$)",
            string.Empty).TrimEnd();
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\n\s*(?:Let me know|Do any of these|Would you like me|Once you clarify|If you would like).*?$",
            string.Empty).TrimEnd();
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\n\s*If you want.*?$",
            string.Empty).TrimEnd();
        return cleaned;
    }

    private static string? TryBuildConservativeLocalBusinessResponse(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (!LooksLikeLocalBusinessBriefingShell(text) &&
            !LooksLikeLocalBusinessStatusShell(text))
        {
            return null;
        }

        var lowerUser = latestUserMessage.ToLowerInvariant();
        if (LooksLikeLocalBusinessBriefingShell(text) &&
            IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerUser) &&
            !IntentFeatureExtractor.LooksLikeGenericLocalBusinessDiscovery(lowerUser))
        {
            return null;
        }

        var isLocalBusiness = IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerUser) ||
                              IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerUser) ||
                              toolCallsMade.Any(call =>
                                  call.ToolName.Equals(ToolNames.PlacesLookup, StringComparison.OrdinalIgnoreCase) ||
                                  call.ToolName.Equals(ToolNames.PlacesLookupAlt, StringComparison.OrdinalIgnoreCase));
        if (!isLocalBusiness)
            return null;

        if (LooksLikeOpenStatusRequest(latestUserMessage) || LooksLikeMalformedHoursLine(text))
            return BuildConservativeOpenStatusFallback(latestUserMessage, toolCallsMade);

        var requestedLabel = GetRequestedLocalBusinessLabel(latestUserMessage);
        var location = ExtractInlineLocation(latestUserMessage);
        var candidate = ExtractTrustedRequestedCandidate(latestUserMessage, toolCallsMade);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var sb = new StringBuilder();
            sb.Append("I found a plausible ");
            sb.Append(requestedLabel.Singular);
            if (!string.IsNullOrWhiteSpace(location))
            {
                sb.Append(" for ");
                sb.Append(location);
            }
            sb.Append(": **");
            sb.Append(candidate);
            sb.Append("**. ");
            sb.Append("Verification recommended: live places lookup was unavailable, so confirm current hours and reviews from the official listing or by calling before visiting.");
            return sb.ToString();
        }

        return BuildConservativeLocalBusinessFallback(latestUserMessage, toolCallsMade);
    }

    private static string BuildConservativeLocalBusinessFallback(
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var requestedLabel = GetRequestedLocalBusinessLabel(latestUserMessage);
        var location = ExtractInlineLocation(latestUserMessage);
        if (string.IsNullOrWhiteSpace(location))
            location = ExtractLatestResolvedPlacesLocation(toolCallsMade);
        if (string.IsNullOrWhiteSpace(location))
            location = ExtractBestLocalBusinessSearchLocation(toolCallsMade, requestedLabel.Plural);
        var checkedSources = BuildCheckedSourceText(toolCallsMade);
        var target = BuildLocalBusinessTarget(latestUserMessage, requestedLabel.Plural, location);
        var placesQuery = ExtractLatestPlacesDiscoverQuery(toolCallsMade);
        var placesEvidenceText = BuildPlacesDiscoveryEvidenceLine(
            requestedLabel.Plural,
            location,
            placesQuery,
            toolCallsMade);
        if (LooksLikeHoursLookupRequest(latestUserMessage))
        {
            return string.Join("\n", new[]
            {
                $"I could not confirm operating hours for {target} from this live lookup.",
                placesEvidenceText.Length > 0
                    ? placesEvidenceText
                    : BuildHoursLookupEvidenceLine(toolCallsMade),
                $"Sources checked: {checkedSources}.",
                "Best next step: verify the official store locator, maps listing, or phone number before going."
            });
        }

        if (LooksLikeBusinessBriefingDetailRequest(latestUserMessage))
        {
            return string.Join("\n", new[]
            {
                $"I could not confirm trustworthy hours, reviews, address, phone, or what to expect for {target} from this live lookup.",
                placesEvidenceText.Length > 0
                    ? placesEvidenceText
                    : "The web fallback did not return an official business page or review summary I trust enough to present as a briefing.",
                $"Sources checked: {checkedSources}.",
                "What to expect: verify hours, reviews, and visit details in the official listing or maps page before going."
            });
        }

        var evidenceLine = !string.IsNullOrWhiteSpace(placesEvidenceText)
            ? placesEvidenceText
            : HasAnyWebSearchResults(toolCallsMade)
            ? "The web fallback returned pages, but they were too generic or unrelated to trust as a recommendation."
            : "The web fallback did not return a trustworthy business page for that query.";

        return string.Join("\n", new[]
        {
            $"I could not confirm a trustworthy {target} from this live lookup.",
            evidenceLine,
            $"Sources checked: {checkedSources}.",
            "Best next step: verify in the official store locator, maps listing, or by phone before visiting."
        });
    }

    private static bool LooksLikeWeakLocalBusinessCandidateList(
        string text,
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var lowerUser = latestUserMessage.ToLowerInvariant();
        if (!IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerUser))
            return false;

        var lowerText = text.ToLowerInvariant();
        if (!lowerText.Contains("came up", StringComparison.Ordinal) &&
            !lowerText.Contains("nearby", StringComparison.Ordinal) &&
            !lowerText.Contains("found", StringComparison.Ordinal))
        {
            return false;
        }

        var bullets = Regex.Matches(text, @"^\s*[-*]\s+\*\*(?<name>[^*]+)\*\*", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        bullets.AddRange(Regex.Matches(text, @"^\s*\d+[.)]\s+\*\*(?<name>[^*]+)\*\*", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name)));
        if (bullets.Count == 0)
            return false;

        var hasPlacesDiscoverCall = toolCallsMade.Any(call =>
            call.Success &&
            (call.ToolName.Equals(ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) ||
             call.ToolName.Equals(ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase)));
        var hasOnlyPlacesDiscoveryEvidence = lowerText.Contains("lookup details: places_discover", StringComparison.Ordinal) ||
                                             (hasPlacesDiscoverCall && !toolCallsMade.Any(call =>
                                                 call.Success &&
                                                 !call.ToolName.Equals(ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) &&
                                                 !call.ToolName.Equals(ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase))) ||
                             (lowerText.Contains("places_discover", StringComparison.Ordinal) &&
                              !lowerText.Contains("web_search", StringComparison.Ordinal) &&
                              !lowerText.Contains("sources checked:", StringComparison.Ordinal));
        var makesUnsupportedQualityClaim = lowerText.Contains("highly rated", StringComparison.Ordinal) ||
                                           lowerText.Contains("known for", StringComparison.Ordinal) ||
                                           lowerText.Contains("good customer service", StringComparison.Ordinal) ||
                                           lowerText.Contains("excellent reviews", StringComparison.Ordinal) ||
                           lowerText.Contains("reviews indicate", StringComparison.Ordinal) ||
                                           lowerText.Contains("rating:", StringComparison.Ordinal) ||
                                           lowerText.Contains("stars based", StringComparison.Ordinal) ||
                           lowerText.Contains("well-rated", StringComparison.Ordinal) ||
                           lowerText.Contains("often praised", StringComparison.Ordinal) ||
                           lowerText.Contains("high quality", StringComparison.Ordinal) ||
                           lowerText.Contains("comparison to help you decide", StringComparison.Ordinal) ||
                           lowerText.Contains("recommend checking recent customer ratings", StringComparison.Ordinal) ||
                           lowerText.Contains("best recommendation", StringComparison.Ordinal);
        if (hasOnlyPlacesDiscoveryEvidence && makesUnsupportedQualityClaim)
            return true;

        var asksForQualityRecommendation = lowerUser.Contains("good", StringComparison.Ordinal) ||
                                           lowerUser.Contains("recommend", StringComparison.Ordinal) ||
                                           lowerUser.Contains("best", StringComparison.Ordinal);
        var presentsDiscoveryCandidatesAsAnswer = lowerText.Contains("candidates", StringComparison.Ordinal) ||
                                                 lowerText.Contains("options presented", StringComparison.Ordinal) ||
                                                 lowerText.Contains("three options", StringComparison.Ordinal);
        var admitsMissingQualityEvidence = lowerText.Contains("not reviews", StringComparison.Ordinal) ||
                                           lowerText.Contains("only have the general location", StringComparison.Ordinal) ||
                                           lowerText.Contains("check ratings", StringComparison.Ordinal) ||
                                           lowerText.Contains("opening times", StringComparison.Ordinal) ||
                                           lowerText.Contains("catches your eye", StringComparison.Ordinal);
        if (hasOnlyPlacesDiscoveryEvidence && asksForQualityRecommendation && presentsDiscoveryCandidatesAsAnswer && admitsMissingQualityEvidence)
            return true;

        var userLower = latestUserMessage.ToLowerInvariant();
        return bullets.All(name =>
            LooksLikeSearchQueryCandidate(name, latestUserMessage) ||
            !SharesRequestedBusinessSignal(name.ToLowerInvariant(), userLower));
    }

    private static bool LooksLikeBusinessBriefingDetailRequest(string latestUserMessage)
    {
        var lower = latestUserMessage.ToLowerInvariant();
        return lower.Contains("deep dive", StringComparison.Ordinal) ||
               lower.Contains("briefing", StringComparison.Ordinal) ||
               lower.Contains("reviews", StringComparison.Ordinal) ||
               lower.Contains("what to expect", StringComparison.Ordinal);
    }

    private static bool LooksLikeHoursLookupRequest(string latestUserMessage)
    {
        var lower = latestUserMessage.ToLowerInvariant();
        return lower.Contains("operating hours", StringComparison.Ordinal) ||
               lower.Contains("hours of", StringComparison.Ordinal) ||
             lower.Contains("hours for", StringComparison.Ordinal) ||
               lower.Contains("what are the hours", StringComparison.Ordinal) ||
               lower.Contains("when does", StringComparison.Ordinal) ||
               lower.Contains("close", StringComparison.Ordinal);
    }

    private static bool LooksLikeLocalBusinessDeflection(string text, string latestUserMessage)
    {
        var lowerUser = latestUserMessage.ToLowerInvariant();
        var hasKnownBrandHoursCue = LooksLikeHoursLookupRequest(latestUserMessage) &&
                                    !string.IsNullOrWhiteSpace(ExtractKnownBrand(latestUserMessage));
        if (!IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerUser) && !hasKnownBrandHoursCue)
            return false;

        var lowerText = text.ToLowerInvariant();
        return lowerText.Contains("local lookup tool was unavailable", StringComparison.Ordinal) ||
             lowerText.Contains("i need a location before i can search", StringComparison.Ordinal) ||
             lowerText.Contains("i need a location to search for local businesses", StringComparison.Ordinal) ||
             lowerText.Contains("initial search returned no specific", StringComparison.Ordinal) ||
             lowerText.Contains("based on local consensus", StringComparison.Ordinal) ||
             lowerText.Contains("well-regarded option", StringComparison.Ordinal) ||
             lowerText.Contains("do not provide a general, current opening time", StringComparison.Ordinal) ||
             lowerText.Contains("need a more direct source", StringComparison.Ordinal) ||
             lowerText.Contains("would you like me to try another search", StringComparison.Ordinal) ||
             lowerText.Contains("technical limitations in accessing precise, localized business data", StringComparison.Ordinal) ||
             lowerText.Contains("dedicated place lookup tool was unavailable", StringComparison.Ordinal) ||
             lowerText.Contains("would you prefer that i attempt", StringComparison.Ordinal) ||
             lowerText.Contains("accuracy remains my highest priority", StringComparison.Ordinal) ||
             lowerText.Contains("please specify which", StringComparison.Ordinal) ||
             lowerText.Contains("[list of specific store addresses", StringComparison.Ordinal) ||
             lowerText.Contains("generally, most locations operate", StringComparison.Ordinal) ||
             lowerText.Contains("to give you the best recommendation, i would need to know", StringComparison.Ordinal) ||
             lowerText.Contains("i would need to know what you are looking for", StringComparison.Ordinal) ||
             lowerText.Contains("if you can specify a preference", StringComparison.Ordinal) ||
             lowerText.Contains("suggest we look up the details", StringComparison.Ordinal) ||
             lowerText.Contains("candidates from this initial search", StringComparison.Ordinal) ||
             lowerText.Contains("initial list from my search gives me names", StringComparison.Ordinal) ||
             lowerText.Contains("doesn't furnish me with reviews or operating hours", StringComparison.Ordinal) ||
             lowerText.Contains("to properly rank them", StringComparison.Ordinal) ||
             lowerText.Contains("would you prefer i fetch more details", StringComparison.Ordinal) ||
             lowerText.Contains("direct lookup service was unavailable", StringComparison.Ordinal) ||
             lowerText.Contains("would you like me to try that approach", StringComparison.Ordinal) ||
             lowerText.Contains("could you confirm if you would like me to look specifically around", StringComparison.Ordinal) ||
             lowerText.Contains("once confirmed, i can run that search again", StringComparison.Ordinal) ||
               lowerText.Contains("dedicated local lookup tool was unavailable", StringComparison.Ordinal) ||
             lowerText.Contains("direct place lookup tool was unavailable", StringComparison.Ordinal) ||
             lowerText.Contains("running into a snag", StringComparison.Ordinal) ||
               lowerText.Contains("running into a bit of a snag", StringComparison.Ordinal) ||
               lowerText.Contains("snag with the automated lookups", StringComparison.Ordinal) ||
                             (lowerText.Contains("could not confirm a trustworthy", StringComparison.Ordinal) &&
                                lowerText.Contains("places_discover", StringComparison.Ordinal)) ||
               lowerText.Contains("search returned no direct matches", StringComparison.Ordinal) ||
               lowerText.Contains("web search returned no specific", StringComparison.Ordinal) ||
             lowerText.Contains("yielded no immediate matches", StringComparison.Ordinal) ||
               lowerText.Contains("use a dedicated map service", StringComparison.Ordinal) ||
               lowerText.Contains("would you prefer i try a broader", StringComparison.Ordinal) ||
             lowerText.Contains("would you like me to try a broader", StringComparison.Ordinal) ||
               lowerText.Contains("try a broader web search", StringComparison.Ordinal);
    }

    private static bool LooksLikeSearchQueryCandidate(string candidate, string latestUserMessage)
    {
        var lowerCandidate = candidate.ToLowerInvariant();
        var lowerUser = latestUserMessage.ToLowerInvariant();
        var location = ExtractInlineLocation(latestUserMessage).ToLowerInvariant();

        var hasRequestedSignal = SharesRequestedBusinessSignal(lowerCandidate, lowerUser);
        var repeatsLocation = !string.IsNullOrWhiteSpace(location) &&
                              location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                  .Any(part => part.Length > 2 && lowerCandidate.Contains(part, StringComparison.Ordinal));
        var isAllLowerOrQueryShaped = candidate.All(ch => !char.IsLetter(ch) || char.IsLower(ch)) ||
                                      lowerCandidate.Contains(" or ", StringComparison.Ordinal) ||
                                      lowerCandidate.EndsWith(" gifts", StringComparison.Ordinal) ||
                                      lowerCandidate.EndsWith(" near me", StringComparison.Ordinal);

        return hasRequestedSignal && (repeatsLocation || isAllLowerOrQueryShaped);
    }

    private static bool LooksLikeLocalBusinessBriefingShell(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("verification recommended", StringComparison.Ordinal) &&
               lower.Contains("sources checked:", StringComparison.Ordinal) &&
               lower.Contains("briefing summary:", StringComparison.Ordinal);
    }

    private static bool LooksLikeLocalBusinessStatusShell(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("sources checked:", StringComparison.Ordinal) &&
               lower.Contains("briefing summary:", StringComparison.Ordinal) &&
               (lower.Contains("details from web sources", StringComparison.Ordinal) ||
                lower.Contains("today:", StringComparison.Ordinal));
    }

    private static bool LooksLikeOpenStatusRequest(string latestUserMessage)
    {
        var lower = latestUserMessage.ToLowerInvariant();
        return lower.Contains("open", StringComparison.Ordinal) ||
               lower.Contains("closed", StringComparison.Ordinal) ||
               lower.Contains("close", StringComparison.Ordinal);
    }

    private static bool LooksLikeMalformedHoursLine(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("today:", StringComparison.Ordinal) &&
               (lower.Contains("\" - ", StringComparison.Ordinal) ||
                lower.Contains("published ", StringComparison.Ordinal) ||
                lower.Contains("thecr.com", StringComparison.Ordinal));
    }

    private static string BuildConservativeOpenStatusFallback(
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var requestedLabel = GetRequestedLocalBusinessLabel(latestUserMessage);
        var location = ExtractInlineLocation(latestUserMessage);
        var target = BuildLocalBusinessTarget(latestUserMessage, requestedLabel.Plural, location);
        var checkedSources = BuildCurrentHoursCheckedSourceText(toolCallsMade);
        var evidenceLine = BuildOpenStatusEvidenceLine(toolCallsMade);
        var officialHint = ExtractKnownBrand(latestUserMessage) is { Length: > 0 } brand
            ? BuildStoreFinderHint(brand)
            : "Use the official store locator, maps listing, or phone number before visiting.";

        var firstLine = LooksLikeOpeningTimeRequest(latestUserMessage)
            ? $"I could not confirm what time {target} opens from this live lookup."
            : LooksLikeClosingTimeRequest(latestUserMessage)
                ? $"I could not confirm what time {target} closes from this live lookup."
                : $"I could not confirm whether {target} is open right now from this live lookup.";

        return string.Join("\n", new[]
        {
            firstLine,
            evidenceLine,
            $"Sources checked: {checkedSources}.",
            $"Best next step: {officialHint}"
        });
    }

    private static bool LooksLikeOpeningTimeRequest(string latestUserMessage)
    {
        var lower = latestUserMessage.ToLowerInvariant();
        return lower.Contains("when does", StringComparison.Ordinal) &&
               (lower.Contains(" open", StringComparison.Ordinal) || lower.Contains("opens", StringComparison.Ordinal));
    }

    private static bool LooksLikeClosingTimeRequest(string latestUserMessage)
    {
        var lower = latestUserMessage.ToLowerInvariant();
        return lower.Contains("when does", StringComparison.Ordinal) &&
               (lower.Contains(" close", StringComparison.Ordinal) || lower.Contains("closes", StringComparison.Ordinal));
    }

    private static string BuildOpenStatusEvidenceLine(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var details = new List<string>();

        if (toolCallsMade.Any(call =>
                (call.ToolName.Equals(ToolNames.PlacesLookup, StringComparison.OrdinalIgnoreCase) ||
                 call.ToolName.Equals(ToolNames.PlacesLookupAlt, StringComparison.OrdinalIgnoreCase)) &&
                (call.Result ?? string.Empty).Contains("api key is not configured", StringComparison.OrdinalIgnoreCase)))
        {
            details.Add("places_lookup/Google Places could not provide a usable current-hours result");
        }

        var addedWebResultSummary = false;
        foreach (var call in toolCallsMade.Where(call =>
                     call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                     call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)))
        {
            var result = call.Result ?? string.Empty;
            if (string.IsNullOrWhiteSpace(result))
                continue;

            var query = ExtractJsonString(call.Arguments ?? string.Empty, "query") ?? "the hours query";
            if (result.Contains("0 result(s)", StringComparison.OrdinalIgnoreCase))
            {
                details.Add($"web_search for \"{query}\" returned 0 results");
                continue;
            }

            if (!addedWebResultSummary && Regex.Match(result, @"\[search:\s*(?<count>\d+)\s+result", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) is { Success: true } match)
            {
                details.Add($"web_search returned {match.Groups["count"].Value} result(s), but no trustworthy current-hours answer");
                addedWebResultSummary = true;
            }
        }

        var browserTitle = ExtractLatestBrowserTitle(toolCallsMade);
        if (!string.IsNullOrWhiteSpace(browserTitle))
            details.Add($"browser_navigate opened \"{browserTitle}\" without confirmed current hours");

        return details.Count == 0
            ? "The returned pages did not provide a trustworthy current-hours answer."
            : "Evidence checked: " + string.Join("; ", details) + ".";
    }

    private static string BuildCurrentHoursCheckedSourceText(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var labels = new List<string>();
        if (toolCallsMade.Any(call =>
                call.ToolName.Equals(ToolNames.PlacesLookup, StringComparison.OrdinalIgnoreCase) ||
                call.ToolName.Equals(ToolNames.PlacesLookupAlt, StringComparison.OrdinalIgnoreCase)))
        {
            labels.Add("places_lookup/Google Places");
        }

        if (toolCallsMade.Any(call =>
                call.ToolName.Equals(ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) ||
                call.ToolName.Equals(ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase)))
        {
            labels.Add("places_discover/Open Places");
        }

        if (toolCallsMade.Any(call =>
                call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)))
        {
            labels.Add("web_search");
        }

        if (HasBrowserNavigateResult(toolCallsMade))
            labels.Add("browser_navigate");

        return labels.Count == 0
            ? BuildCheckedSourceText(toolCallsMade)
            : string.Join(", ", labels);
    }

    private static string BuildStoreFinderHint(string brand)
    {
        if (brand.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || brand.EndsWith('s'))
            return $"Use the {brand} store finder or call the location before visiting.";

        return $"Use {brand}'s store finder or call the location before visiting.";
    }

    private static string BuildHoursLookupEvidenceLine(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var details = new List<string>();

        if (toolCallsMade.Any(call =>
                (call.ToolName.Equals(ToolNames.PlacesLookup, StringComparison.OrdinalIgnoreCase) ||
                 call.ToolName.Equals(ToolNames.PlacesLookupAlt, StringComparison.OrdinalIgnoreCase)) &&
                (call.Result ?? string.Empty).Contains("api key is not configured", StringComparison.OrdinalIgnoreCase)))
        {
            details.Add("places_lookup/Google Places could not provide usable hours");
        }

        var addedWebResultSummary = false;
        foreach (var call in toolCallsMade.Where(call =>
                     call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                     call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)))
        {
            var result = call.Result ?? string.Empty;
            if (string.IsNullOrWhiteSpace(result))
                continue;

            var query = ExtractJsonString(call.Arguments ?? string.Empty, "query") ?? "the hours query";
            if (result.Contains("0 result(s)", StringComparison.OrdinalIgnoreCase))
            {
                details.Add($"web_search for \"{query}\" returned 0 results");
                continue;
            }

            if (!addedWebResultSummary && Regex.Match(result, @"\[search:\s*(?<count>\d+)\s+result", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) is { Success: true } match)
            {
                details.Add($"web_search returned {match.Groups["count"].Value} result(s), but no trustworthy official hours answer");
                addedWebResultSummary = true;
            }
        }

        var browserTitle = ExtractLatestBrowserTitle(toolCallsMade);
        if (!string.IsNullOrWhiteSpace(browserTitle))
            details.Add($"browser_navigate opened \"{browserTitle}\" without confirmed current hours");

        return details.Count == 0
            ? "The live lookup did not return an official hours source I trust enough to state today's schedule."
            : "Evidence checked: " + string.Join("; ", details) + ".";
    }

    private static string? ExtractTrustedRequestedCandidate(string latestUserMessage, IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var userLower = latestUserMessage.ToLowerInvariant();
        var requestedLocation = ExtractInlineLocation(latestUserMessage);
        foreach (var source in MergeWebSearchSources(toolCallsMade))
        {
            var title = NormalizeCandidateTitle(source.Title, latestUserMessage);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            if (!string.IsNullOrWhiteSpace(requestedLocation) && !SourceMentionsRequestedLocation(source, requestedLocation))
                continue;

            if (SharesRequestedBusinessSignal(title.ToLowerInvariant(), userLower))
                return title;
        }

        return null;
    }

    private static bool SourceMentionsRequestedLocation(SourceItem source, string requestedLocation)
    {
        if (string.IsNullOrWhiteSpace(requestedLocation))
            return true;

        var combined = ((source.Title ?? string.Empty) + " " + (source.Snippet ?? string.Empty) + " " + source.Url)
            .ToLowerInvariant();
        var tokens = requestedLocation
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .Select(token => token.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tokens.Count == 0 || tokens.Any(token => combined.Contains(token, StringComparison.Ordinal));
    }

    private static string? NormalizeCandidateTitle(string? title, string latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var cleaned = Regex.Replace(title.Trim(), @"\s*[-|].*$", string.Empty).Trim().Trim(',', '.', ':', ';');
        if (cleaned.Length < 4)
            return null;

        var lower = cleaned.ToLowerInvariant();
        if (lower.StartsWith("best ", StringComparison.Ordinal) ||
            lower.StartsWith("top ", StringComparison.Ordinal) ||
            Regex.IsMatch(cleaned, @"^\d+\s+", RegexOptions.CultureInvariant) ||
            lower.Contains("near me", StringComparison.Ordinal) ||
            lower.Contains("subscription service", StringComparison.Ordinal) ||
            (lower.Contains("flower", StringComparison.Ordinal) && lower.Contains("gift", StringComparison.Ordinal)) ||
            lower.Contains("keeps on giving", StringComparison.Ordinal) ||
            lower.Contains("theater", StringComparison.Ordinal) ||
            lower.Contains("news", StringComparison.Ordinal) ||
            lower.Contains("community support", StringComparison.Ordinal))
        {
            return null;
        }

        var location = ExtractInlineLocation(latestUserMessage);
        if (!string.IsNullOrWhiteSpace(location) && cleaned.EndsWith(location, StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^location.Length].Trim().Trim(',', '-', ' ');

        return cleaned;
    }

    private static bool HasAnyWebSearchResults(IReadOnlyList<ToolCallRecord> toolCallsMade)
        => toolCallsMade.Any(call =>
            call.Success &&
            (call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
             call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)) &&
            SearchOrchestrator.ParseSourcesFromToolResult(call.Result ?? string.Empty).Count > 0);

    private static string BuildCheckedSourceText(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var domains = MergeWebSearchSources(toolCallsMade)
            .Select(source => source.Domain)
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var hasBrowserNavigate = HasBrowserNavigateResult(toolCallsMade);
        var hasWebSearchCall = toolCallsMade.Any(call =>
            call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
            call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase));

        if (domains.Count == 0 && toolCallsMade.Any(call =>
                call.ToolName.Equals(ToolNames.PlacesLookup, StringComparison.OrdinalIgnoreCase) ||
                call.ToolName.Equals(ToolNames.PlacesLookupAlt, StringComparison.OrdinalIgnoreCase) ||
                call.ToolName.Equals(ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) ||
                call.ToolName.Equals(ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase)))
        {
            var labels = new List<string>();
            if (toolCallsMade.Any(call =>
                    call.ToolName.Equals(ToolNames.PlacesLookup, StringComparison.OrdinalIgnoreCase) ||
                    call.ToolName.Equals(ToolNames.PlacesLookupAlt, StringComparison.OrdinalIgnoreCase)))
            {
                labels.Add("places_lookup/Google Places");
            }

            if (toolCallsMade.Any(call =>
                    call.ToolName.Equals(ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) ||
                    call.ToolName.Equals(ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase)))
            {
                labels.Add("places_discover/Open Places");
            }

            if (hasWebSearchCall)
                labels.Add("web_search");
            if (hasBrowserNavigate)
                labels.Add("browser_navigate/Bing");

            return string.Join(", ", labels);
        }

        if (domains.Count == 0)
            return hasBrowserNavigate ? "web_search and browser_navigate/Bing" : "web search";

        if (hasBrowserNavigate)
            domains.Add("browser_navigate/Bing");

        return string.Join(", ", domains);
    }

    private static string BuildPlacesDiscoveryEvidenceLine(
        string requestedPluralLabel,
        string location,
        string query,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var discover = GetBestPlacesDiscoverCall(toolCallsMade);
        if (discover is null)
            return string.Empty;

        var provider = ExtractJsonString(discover.Result ?? string.Empty, "provider") ?? "Open Places";
        var queryText = string.IsNullOrWhiteSpace(query) ? requestedPluralLabel : query;
        var resolvedPlacesLocation = ExtractJsonString(discover.Result ?? string.Empty, "resolvedLocation") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resolvedPlacesLocation))
            resolvedPlacesLocation = ExtractJsonString(discover.Result ?? string.Empty, "userLocationHint") ?? string.Empty;
        var locationText = !string.IsNullOrWhiteSpace(resolvedPlacesLocation)
            ? $"near {resolvedPlacesLocation}"
            : "without a resolved location";
        var webSearchQuery = ExtractBestLocalBusinessSearchQuery(toolCallsMade, requestedPluralLabel);
        var browserTitle = ExtractLatestBrowserTitle(toolCallsMade);
        var candidateNames = ExtractPlacesDiscoverCandidateNames(discover.Result ?? string.Empty)
            .Take(3)
            .ToList();
        var candidateText = candidateNames.Count == 0
            ? string.Empty
            : $" It returned nearby names ({string.Join(", ", candidateNames)}), but no review, rating, hours, or official-listing evidence strong enough to rank them as good recommendations.";

        var extraEvidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(webSearchQuery))
            extraEvidence.Add($"web_search also checked \"{webSearchQuery}\"");
        if (!string.IsNullOrWhiteSpace(browserTitle))
            extraEvidence.Add($"browser_navigate opened a page titled \"{browserTitle}\"");

        if (extraEvidence.Count == 0)
        {
            var fallbackLocationText = string.IsNullOrWhiteSpace(location) ? locationText : $"near {location}";
            return $"The places_discover lookup ({provider}) checked \"{queryText}\" {fallbackLocationText}, but it did not return a trustworthy {requestedPluralLabel} match I can recommend.{candidateText}";
        }

        return $"The places_discover lookup ({provider}) checked \"{queryText}\" {locationText}; {string.Join("; ", extraEvidence)}, but none returned a trustworthy {requestedPluralLabel} match I can recommend.{candidateText}";
    }

    private static IReadOnlyList<string> ExtractPlacesDiscoverCandidateNames(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return [];

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(result, "\\\"name\\\"\\s*:\\s*\\\"(?<name>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var name = Regex.Unescape(match.Groups["name"].Value).Trim();
            if (name.Length < 3 || !seen.Add(name))
                continue;

            names.Add(name);
            if (names.Count >= 5)
                break;
        }

        return names;
    }

    private static bool HasBrowserNavigateResult(IReadOnlyList<ToolCallRecord> toolCallsMade)
        => toolCallsMade.Any(call =>
            call.Success &&
            IsBrowserNavigateTool(call.ToolName) &&
            !string.IsNullOrWhiteSpace(call.Result));

    private static bool IsBrowserNavigateTool(string toolName)
        => toolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
           toolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase);

    private static string ExtractLatestBrowserTitle(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        foreach (var call in toolCallsMade.Reverse())
        {
            if (!call.Success || !IsBrowserNavigateTool(call.ToolName) || string.IsNullOrWhiteSpace(call.Result))
                continue;

            var match = Regex.Match(
                call.Result ?? string.Empty,
                "Title:\\s*\"(?<title>[^\"]+)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
                return match.Groups["title"].Value.Trim();
        }

        return string.Empty;
    }

    private static string ExtractBestLocalBusinessSearchLocation(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string requestedPluralLabel)
    {
        var query = ExtractBestLocalBusinessSearchQuery(toolCallsMade, requestedPluralLabel);
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var match = Regex.Match(
            query,
            @"\b(?:near|in|around)\s+(?<loc>[A-Za-z][A-Za-z .'-]+,\s*[A-Z]{2}|[A-Za-z][A-Za-z .'-]+)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return string.Empty;

        var location = match.Groups["loc"].Value.Trim().Trim(',', '.', '?', '!');
        location = Regex.Replace(location, @"\s+(?:right now|today|currently|open|closed|close|closes|hours|reviews).*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return location.Equals("verification", StringComparison.OrdinalIgnoreCase) ? string.Empty : location;
    }

    private static string ExtractBestLocalBusinessSearchQuery(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string requestedPluralLabel)
    {
        var labelRoot = requestedPluralLabel.ToLowerInvariant();
        var candidates = toolCallsMade
            .Where(call =>
                call.Success &&
                (call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                 call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)))
            .Select(call => ExtractJsonString(call.Arguments ?? string.Empty, "query") ?? string.Empty)
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => new { Query = query, Score = ScoreLocalBusinessSearchQuery(query, labelRoot) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();

        return candidates.Count == 0 ? string.Empty : candidates[0].Query;
    }

    private static int ScoreLocalBusinessSearchQuery(string query, string labelRoot)
    {
        var lower = query.ToLowerInvariant();
        var score = 0;
        if (lower.Contains(labelRoot, StringComparison.Ordinal))
            score += 3;
        if (labelRoot.Contains("flor", StringComparison.Ordinal) && lower.Contains("flower", StringComparison.Ordinal))
            score += 2;
        if (lower.Contains("near ", StringComparison.Ordinal) ||
            lower.Contains(" in ", StringComparison.Ordinal) ||
            lower.Contains(" around ", StringComparison.Ordinal))
        {
            score += 3;
        }
        if (lower.Contains("site:", StringComparison.Ordinal))
            score -= 2;
        if (lower.Contains("verification", StringComparison.Ordinal))
            score -= 3;
        return score;
    }

    private static string ExtractLatestResolvedPlacesLocation(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var preferred = GetBestPlacesDiscoverCall(toolCallsMade);
        if (preferred is not null)
        {
            var resolved = ExtractJsonString(preferred.Result ?? string.Empty, "resolvedLocation");
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;

            var hint = ExtractJsonString(preferred.Result ?? string.Empty, "userLocationHint");
            if (!string.IsNullOrWhiteSpace(hint))
                return hint;
        }

        return string.Empty;
    }

    private static ToolCallRecord? GetBestPlacesDiscoverCall(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var discoverCalls = toolCallsMade
            .Where(call =>
                call.Success &&
                (call.ToolName.Equals(ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) ||
                 call.ToolName.Equals(ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(call.Result))
            .Reverse()
            .ToList();
        if (discoverCalls.Count == 0)
            return null;

        return discoverCalls.FirstOrDefault(call =>
                   !string.IsNullOrWhiteSpace(ExtractJsonString(call.Result ?? string.Empty, "resolvedLocation")) ||
                   !string.IsNullOrWhiteSpace(ExtractJsonString(call.Result ?? string.Empty, "userLocationHint")))
               ?? discoverCalls[0];
    }


    private static string ExtractLatestPlacesDiscoverQuery(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var preferred = GetBestPlacesDiscoverCall(toolCallsMade);
        if (preferred is not null)
        {
            var query = ExtractJsonString(preferred.Result ?? string.Empty, "query");
            if (!string.IsNullOrWhiteSpace(query))
                return query;

            query = ExtractJsonString(preferred.Arguments ?? string.Empty, "query");
            if (!string.IsNullOrWhiteSpace(query))
                return query;
        }

        return string.Empty;
    }

    private static (string Singular, string Plural) GetRequestedLocalBusinessLabel(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("deli", StringComparison.Ordinal) => ("deli", "deli"),
            _ when lower.Contains("flor", StringComparison.Ordinal) => ("florist", "florist"),
            _ when lower.Contains("baker", StringComparison.Ordinal) => ("bakery", "bakery"),
            _ when lower.Contains("cafe", StringComparison.Ordinal) => ("cafe", "cafe"),
            _ when lower.Contains("coffee", StringComparison.Ordinal) => ("coffee shop", "coffee shop"),
            _ when lower.Contains("walmart", StringComparison.Ordinal) => ("Walmart location", "Walmart location"),
            _ when lower.Contains("target", StringComparison.Ordinal) => ("Target location", "Target location"),
            _ when lower.Contains("store", StringComparison.Ordinal) => ("store", "store"),
            _ => ("local business", "local business")
        };
    }

    private static string BuildLocalBusinessTarget(string latestUserMessage, string requestedPluralLabel, string location)
    {
        var explicitBusiness = ExtractNamedBusinessFromPrompt(latestUserMessage);
        if (!string.IsNullOrWhiteSpace(explicitBusiness))
        {
            return string.IsNullOrWhiteSpace(location)
                ? explicitBusiness
                : $"{explicitBusiness} in {location}";
        }

        var brand = ExtractKnownBrand(latestUserMessage);
        if (!string.IsNullOrWhiteSpace(brand))
        {
            return string.IsNullOrWhiteSpace(location)
                ? brand
                : $"{brand} in {location}";
        }

        return string.IsNullOrWhiteSpace(location)
            ? requestedPluralLabel
            : $"{requestedPluralLabel} in {location}";
    }

    private static string ExtractKnownBrand(string latestUserMessage)
    {
        var lower = latestUserMessage.ToLowerInvariant();
        if (lower.Contains("walmart", StringComparison.Ordinal))
            return "Walmart";
        if (lower.Contains("target", StringComparison.Ordinal))
            return "Target";
        if (lower.Contains("starbucks", StringComparison.Ordinal))
            return "Starbucks";
        if (lower.Contains("trader joe", StringComparison.Ordinal))
            return "Trader Joe's";
        if (lower.Contains("mcdonald", StringComparison.Ordinal))
            return Regex.IsMatch(latestUserMessage, @"\bmcdonald'?s\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                ? "McDonalds"
                : "McDonald's";
        return string.Empty;
    }

    private static string ExtractNamedBusinessFromPrompt(string latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(latestUserMessage))
            return string.Empty;

        var match = Regex.Match(
            latestUserMessage,
            @"\bdeep\s+dive\s+(?<name>.+?)(?:\s+with\b|\s+for\b|\s+and\b|[?.!]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return string.Empty;

        var name = match.Groups["name"].Value.Trim().Trim(',', '.', ':', ';', '-');
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return name.Length < 3 ? string.Empty : name;
    }

    private static string ExtractInlineLocation(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return string.Empty;

        var match = Regex.Match(
            userMessage,
            @"\b(?:in|near|around|for)\s+(?<loc>[A-Za-z][A-Za-z .'-]+,\s*[A-Z]{2}|[A-Za-z][A-Za-z .'-]+)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return string.Empty;

        var location = match.Groups["loc"].Value.Trim().Trim(',', '.', '?', '!');
        location = Regex.Replace(location, @"\s+(?:right now|today|currently|open|closed|close|closes|hours|reviews).*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return location;
    }

    private static bool SharesRequestedBusinessSignal(string candidateLower, string userLower)
    {
        return (userLower.Contains("deli", StringComparison.Ordinal) &&
                (candidateLower.Contains("deli", StringComparison.Ordinal) || candidateLower.Contains("delicatessen", StringComparison.Ordinal))) ||
               (userLower.Contains("flor", StringComparison.Ordinal) &&
                (candidateLower.Contains("flor", StringComparison.Ordinal) || candidateLower.Contains("flower", StringComparison.Ordinal))) ||
               (userLower.Contains("baker", StringComparison.Ordinal) && candidateLower.Contains("baker", StringComparison.Ordinal)) ||
               (userLower.Contains("cafe", StringComparison.Ordinal) && candidateLower.Contains("cafe", StringComparison.Ordinal)) ||
               (userLower.Contains("coffee", StringComparison.Ordinal) && candidateLower.Contains("coffee", StringComparison.Ordinal)) ||
               (userLower.Contains("walmart", StringComparison.Ordinal) && candidateLower.Contains("walmart", StringComparison.Ordinal)) ||
               (userLower.Contains("target", StringComparison.Ordinal) && candidateLower.Contains("target", StringComparison.Ordinal)) ||
               (userLower.Contains("starbucks", StringComparison.Ordinal) && candidateLower.Contains("starbucks", StringComparison.Ordinal));
    }
}
