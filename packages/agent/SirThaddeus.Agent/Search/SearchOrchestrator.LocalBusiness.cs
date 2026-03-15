using System.Text;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Search;

public sealed partial class SearchOrchestrator
{
    /// <summary>
    /// Extracts an inline location from the user message, e.g.
    /// "florist in Hillsboro, OR" → "Hillsboro, OR".
    /// Returns null if no location pattern is found.
    /// </summary>
    private static string? ExtractInlineLocationFromMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var match = ExplicitLocationScopeRegex.Match(message);
        if (!match.Success)
            return null;

        // The regex matches "in Hillsboro, OR" — strip the preposition.
        var location = match.Value.Trim();
        var prefixEnd = location.IndexOf(' ');
        if (prefixEnd > 0)
            location = location[(prefixEnd + 1)..].Trim();

        location = Regex.Replace(
            location,
            @"\b(?:please|pls|thanks|thank\s+you)\b.*$",
            "",
            RegexOptions.IgnoreCase).Trim();

        return location.TrimEnd('?', '.', '!', ',');
    }

    private AgentResponse BuildLocalBusinessDiscoveryResponse(
        string userMessage,
        IReadOnlyList<SourceItem> sources,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var businessLabel = GetRequestedLocalBusinessLabel(userMessage);
        var location = ResolveLocalBusinessLocationContext(userMessage)?.Trim();
        var locText = string.IsNullOrWhiteSpace(location) ? " nearby" : $" nearby in {location}";
        var topSources = sources.Take(LocalBusinessTargetResults).ToList();

        var sb = new StringBuilder();
        sb.AppendLine(topSources.Count == 1
            ? $"Here are the {businessLabel}{locText} results I found (1 clearly relevant match):"
            : $"Here are the top {topSources.Count} {businessLabel}{locText}:");
        sb.AppendLine();

        foreach (var source in topSources)
        {
            var displayTitle = StripTitleSuffix(source.Title);
            sb.Append("- **");
            sb.Append(displayTitle);
            sb.Append("**");

            if (!string.IsNullOrWhiteSpace(source.Snippet))
            {
                sb.Append(": ");
                sb.Append(TrimSentence(source.Snippet, 180));
            }

            if (!string.IsNullOrWhiteSpace(source.Domain))
            {
                sb.Append(" (source: ");
                sb.Append(source.Domain);
                sb.Append(')');
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append("If you want, I can bring up more info on any one of these ");
        sb.Append(businessLabel);
        sb.Append('.');

        return new AgentResponse
        {
            Text = sb.ToString(),
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0
        };
    }

    private static IReadOnlyList<SourceItem> SelectLocalBusinessDiscoverySources(
        string userMessage,
        IReadOnlyList<SourceItem> sources,
        int targetCount,
        string? locationContext = null)
    {
        if (sources.Count == 0)
            return sources;

        // Filter out junk/synthetic sources that can't represent real businesses.
        sources = sources.Where(s => !IsJunkBusinessSource(s)).ToList();
        if (sources.Count == 0)
            return [];

        var locationFiltered = FilterSourcesForLocalBusinessLocation(sources, locationContext);
        if (locationFiltered.Count > 0)
            sources = locationFiltered;
        else if (!string.IsNullOrWhiteSpace(locationContext))
            return [];

        var keywords = GetLocalBusinessMatchKeywords(userMessage);
        if (keywords.Count == 0)
            return sources.Take(Math.Max(1, targetCount)).ToList();

        var strict = sources
            .Where(source => LocalBusinessSourceMatches(source, keywords))
            .ToList();

        // Demote directory/aggregator pages (e.g. "BEST 10 BAKERIES in OLYMPIA")
        // to the bottom so individual businesses appear first.
        strict = DemoteDirectoryAggregatorSources(strict);

        if (strict.Count >= targetCount)
            return strict.Take(targetCount).ToList();

        if (strict.Count == 0)
            return DemoteDirectoryAggregatorSources(sources.ToList()).Take(Math.Max(1, targetCount)).ToList();

        // When the provider only returned a small pool, keep precision over
        // recall and avoid backfilling with likely-irrelevant generic guides.
        if (sources.Count <= targetCount)
            return strict;

        var selected = new List<SourceItem>(strict);
        var selectedIds = new HashSet<string>(
            selected.Select(s => s.SourceId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            if (selectedIds.Contains(source.SourceId))
                continue;

            selected.Add(source);
            selectedIds.Add(source.SourceId);

            if (selected.Count >= targetCount)
                break;
        }

        return selected;
    }

    /// <summary>
    /// Returns true for obviously synthetic or junk sources that should
    /// never appear as a "business" in a local discovery response.
    /// Examples: Google Search fallback pages, ad redirects.
    /// </summary>
    private static bool IsJunkBusinessSource(SourceItem source)
    {
        var title = (source.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            return true;

        if (title.Equals("Google Search", StringComparison.OrdinalIgnoreCase))
            return true;

        if (title.StartsWith("Google", StringComparison.OrdinalIgnoreCase) &&
            title.Length < 30)
            return true;
        if (title.StartsWith("Bing", StringComparison.OrdinalIgnoreCase) &&
            title.Length < 30)
            return true;

        if (IsJunkUrl(source.Url) && !IsNewsRedirectWithValidMetadata(source))
            return true;

        return false;
    }

    private static bool IsNewsRedirectWithValidMetadata(SourceItem source)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            return false;

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains("news.google.com", StringComparison.Ordinal))
            return false;

        var hasTitle = !string.IsNullOrWhiteSpace(source.Title) && source.Title.Length > 10;
        var hasDomain = !string.IsNullOrWhiteSpace(source.Domain) &&
                        !source.Domain.Contains("google", StringComparison.OrdinalIgnoreCase);

        return hasTitle && hasDomain;
    }

    private static List<SourceItem> DemoteDirectoryAggregatorSources(List<SourceItem> sources)
    {
        return [.. sources.OrderBy(s => IsDirectoryAggregatorSource(s) ? 1 : 0)];
    }

    internal static bool IsDirectoryAggregatorSource(SourceItem source)
    {
        var title = (source.Title ?? "").Trim();
        var url = (source.Url ?? "").Trim();

        if (Regex.IsMatch(title, @"\b(?:BEST|TOP)\s+\d+\b", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(title, @"\bBest\b.*\bin\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(title, @"\b(?:shops?|restaurants?|places?|bakeries|delis?|florists?|salons?|cafes?|stores?)\b", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(title, @"\bTop\s+\w+\s+(?:in|near)\b", RegexOptions.IgnoreCase))
            return true;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (host.Contains("yelp.com", StringComparison.Ordinal) &&
                !path.StartsWith("/biz/", StringComparison.Ordinal))
                return true;

            if (host.Contains("tripadvisor.com", StringComparison.Ordinal) &&
                (path.StartsWith("/restaurants", StringComparison.Ordinal) ||
                 path.StartsWith("/attractions", StringComparison.Ordinal)))
                return true;

            if (host.Contains("foursquare.com", StringComparison.Ordinal) &&
                path.Contains("top-picks", StringComparison.Ordinal))
                return true;

            if (host.Contains("experienceolympia", StringComparison.Ordinal) ||
                host.Contains("visitseattle", StringComparison.Ordinal) ||
                host.Contains("timeout.com", StringComparison.Ordinal) ||
                host.Contains("eater.com", StringComparison.Ordinal) ||
                host.Contains("thrillist.com", StringComparison.Ordinal) ||
                host.Contains("infatuation.com", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool LocalBusinessSourceMatches(SourceItem source, IReadOnlyList<string> keywords)
    {
        var haystack = $"{source.Title} {source.Snippet} {source.Domain}";
        foreach (var keyword in keywords)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> GetLocalBusinessMatchKeywords(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();

        if (lower.Contains("deli", StringComparison.Ordinal) ||
            lower.Contains("delis", StringComparison.Ordinal) ||
            lower.Contains("delicatessen", StringComparison.Ordinal))
            return ["deli", "delis", "delicatessen", "sandwich", "sub", "hoagie", "pastrami", "bagel"];

        if (lower.Contains("bakery", StringComparison.Ordinal) || lower.Contains("bakeries", StringComparison.Ordinal))
            return ["bakery", "bakeries", "bread", "pastry", "pastries", "patisserie", "cake", "cakes", "donut", "donuts"];

        if (lower.Contains("restaurant", StringComparison.Ordinal))
            return ["restaurant", "restaurants", "eatery", "bistro", "diner", "grill", "cafe"];

        if (lower.Contains("florist", StringComparison.Ordinal))
            return ["florist", "floral", "flower"];

        if (lower.Contains("salon", StringComparison.Ordinal))
            return ["salon", "hair", "stylist", "barber"];

        if (lower.Contains("coffee", StringComparison.Ordinal) || lower.Contains("cafe", StringComparison.Ordinal))
            return ["coffee", "cafe", "espresso", "roastery"];

        if (lower.Contains("pharmacy", StringComparison.Ordinal))
            return ["pharmacy", "drugstore", "rx"];

        if (lower.Contains("dentist", StringComparison.Ordinal))
            return ["dentist", "dental", "orthodont"];

        if (lower.Contains("grocery", StringComparison.Ordinal))
            return ["grocery", "market", "supermarket"];

        return [];
    }

    private static string GetRequestedLocalBusinessLabel(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();

        if (lower.Contains("delis", StringComparison.Ordinal) ||
            lower.Contains("deli", StringComparison.Ordinal) ||
            lower.Contains("delicatessen", StringComparison.Ordinal))
            return "delis";
        if (lower.Contains("bakeries", StringComparison.Ordinal) || lower.Contains("bakery", StringComparison.Ordinal))
            return "bakeries";
        if (lower.Contains("restaurants", StringComparison.Ordinal) || lower.Contains("restaurant", StringComparison.Ordinal))
            return "restaurants";
        if (lower.Contains("florists", StringComparison.Ordinal) || lower.Contains("florist", StringComparison.Ordinal))
            return "florists";
        if (lower.Contains("coffee", StringComparison.Ordinal) || lower.Contains("cafe", StringComparison.Ordinal))
            return "coffee shops";
        if (lower.Contains("salon", StringComparison.Ordinal))
            return "salons";

        return "places";
    }

    private static string SingularizeBusinessLabel(string label)
    {
        return label switch
        {
            "delis" => "deli",
            "bakeries" => "bakery",
            "restaurants" => "restaurant",
            "florists" => "florist",
            "coffee shops" => "coffee shop",
            "salons" => "salon",
            _ => label.TrimEnd('s')
        };
    }

    private string? ResolveLocalBusinessLocationContext(string userMessage)
    {
        if (!string.IsNullOrWhiteSpace(UserLocationHint))
            return UserLocationHint.Trim();

        return ExtractInlineLocationFromMessage(userMessage);
    }

    private static IReadOnlyList<SourceItem> FilterSourcesForLocalBusinessLocation(
        IReadOnlyList<SourceItem> sources,
        string? locationContext)
    {
        var location = BuildLocalNewsLocationTokens(locationContext);
        if (location is null)
            return [];

        var locationMatches = sources
            .Where(source => IsLocalBusinessLocationMatch(source, location))
            .ToList();

        if (locationMatches.Count > 0)
            return locationMatches;

        return sources
            .Where(source => !IsExplicitlyOutOfAreaLocalBusinessSource(source, location))
            .ToList();
    }

    private static bool IsLocalBusinessLocationMatch(SourceItem source, LocalNewsLocationTokens location)
    {
        var normalizedStory = BuildLocalNewsStoryText(source);
        if (ContainsNormalizedTerm(normalizedStory, location.CityPhrase))
            return true;

        if (location.CityTokens.Any(token => ContainsNormalizedTerm(normalizedStory, token)))
            return true;

        return (!string.IsNullOrWhiteSpace(location.StateName) &&
                ContainsNormalizedTerm(normalizedStory, location.StateName)) ||
               (!string.IsNullOrWhiteSpace(location.StateCode) &&
                ContainsNormalizedTerm(normalizedStory, location.StateCode));
    }

    private static bool IsExplicitlyOutOfAreaLocalBusinessSource(SourceItem source, LocalNewsLocationTokens targetLocation)
    {
        var normalizedStory = BuildLocalNewsStoryText(source);
        if (string.IsNullOrWhiteSpace(normalizedStory))
            return false;

        if (IsLocalBusinessLocationMatch(source, targetLocation))
            return false;

        foreach (var state in StateCodeToName)
        {
            var normalizedStateName = NormalizeLocalNewsText(state.Value);
            var mentionsThisState =
                ContainsNormalizedTerm(normalizedStory, state.Key) ||
                ContainsNormalizedTerm(normalizedStory, normalizedStateName);

            if (!mentionsThisState)
                continue;

            var isTargetState =
                (!string.IsNullOrWhiteSpace(targetLocation.StateCode) &&
                 string.Equals(targetLocation.StateCode, state.Key, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(targetLocation.StateName) &&
                 string.Equals(targetLocation.StateName, normalizedStateName, StringComparison.OrdinalIgnoreCase));

            return !isTargetState;
        }

        return false;
    }

    private static string TrimSentence(string text, int maxLen)
    {
        var cleaned = (text ?? "").Trim();
        if (cleaned.Length <= maxLen)
            return cleaned;

        return cleaned[..maxLen].TrimEnd() + "…";
    }

    private AgentResponse BuildNewsNoResultsResponse(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string? resolvedLocationName = null)
    {
        var isLocalNewsRequest = LocalNewsSignalRegex.IsMatch(userMessage ?? "");
        var locationHint = UserLocationHint?.Trim();
        if (string.IsNullOrWhiteSpace(locationHint))
            locationHint = resolvedLocationName?.Trim();

        var text = isLocalNewsRequest switch
        {
            true when !string.IsNullOrWhiteSpace(locationHint) =>
                $"I couldn't find usable live local news results for {locationHint} right now. " +
                "Try asking for state news, naming a local outlet, or narrowing it to a topic like schools, crime, politics, or weather.",
            true =>
                "I couldn't find usable live local news results for that request right now. " +
                "Try including a city, naming a local outlet, or setting your location in Settings and trying again.",
            _ =>
                "I couldn't find usable live news results for that request right now. " +
                "Try narrowing it to a topic, place, or timeframe."
        };

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0
        };
    }

    private static bool IsLocalBusinessNoResultsRequest(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        return
            lower.Contains("deli", StringComparison.Ordinal) ||
            lower.Contains("delis", StringComparison.Ordinal) ||
            lower.Contains("delicatessen", StringComparison.Ordinal) ||
            lower.Contains("restaurant", StringComparison.Ordinal) ||
            lower.Contains("restaurants", StringComparison.Ordinal) ||
            lower.Contains("florist", StringComparison.Ordinal) ||
            lower.Contains("bakery", StringComparison.Ordinal) ||
            lower.Contains("bakeries", StringComparison.Ordinal) ||
            lower.Contains("cafe", StringComparison.Ordinal) ||
            lower.Contains("coffee", StringComparison.Ordinal) ||
            lower.Contains("bar", StringComparison.Ordinal) ||
            lower.Contains("pub", StringComparison.Ordinal) ||
            lower.Contains("store", StringComparison.Ordinal) ||
            lower.Contains("shop", StringComparison.Ordinal) ||
            lower.Contains("salon", StringComparison.Ordinal) ||
            lower.Contains("gym", StringComparison.Ordinal) ||
            lower.Contains("pharmacy", StringComparison.Ordinal) ||
            lower.Contains("gas station", StringComparison.Ordinal) ||
            lower.Contains("car wash", StringComparison.Ordinal) ||
            lower.Contains("laundromat", StringComparison.Ordinal) ||
            lower.Contains("grocery", StringComparison.Ordinal) ||
            lower.Contains("dentist", StringComparison.Ordinal) ||
            lower.Contains("clinic", StringComparison.Ordinal) ||
            lower.Contains("open", StringComparison.Ordinal) ||
            lower.Contains("hours", StringComparison.Ordinal);
    }
}
