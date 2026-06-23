using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search.DeepDive;
using SirThaddeus.WebSearch;

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
        var requireExactLocationMatch = !string.IsNullOrWhiteSpace(ExtractInlineLocationFromMessage(userMessage));
        var selectedSources = SelectLocalBusinessDiscoverySources(
            userMessage,
            sources,
            LocalBusinessTargetResults,
            location);
        if (selectedSources.Count > 0)
            sources = selectedSources;

        Session.LastWasLocalBusinessDiscovery = true;
        Session.RecordLocalBusinessCandidates(businessLabel, sources);

        if (requireExactLocationMatch &&
            !string.IsNullOrWhiteSpace(location) &&
            FilterSourcesForLocalBusinessLocation(sources, location, requireMatch: true).Count == 0)
        {
            return BuildNoResultsResponse(userMessage, toolCallsMade);
        }

        var topSources = sources
            .Where(source => !IsDirectoryAggregatorSource(source))
            .Select(source => new
            {
                Source = source,
                Name = ExtractBusinessNameFromSourceTitle(source.Title, userMessage)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Take(LocalBusinessTargetResults)
            .ToList();

        if (topSources.Count == 0)
        {
            var snippetNames = ExtractLocalBusinessNamesFromSearchSources(sources, userMessage);
            if (snippetNames.Count > 0)
                return BuildCleanedLocalBusinessResponse(userMessage, snippetNames, location, toolCallsMade);

            return BuildDirectoryLocalBusinessResponse(userMessage, sources, toolCallsMade);
        }

        var sb = new StringBuilder();
        sb.AppendLine(topSources.Count == 1
            ? $"Here are the {businessLabel}{locText} results I found (1 clearly relevant match):"
            : $"Here are the top {topSources.Count} {businessLabel}{locText}:");
        sb.AppendLine();

        foreach (var item in topSources)
        {
            var source = item.Source;
            var displayTitle = item.Name!;
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

        var requiresExactLocationMatch = !string.IsNullOrWhiteSpace(ExtractInlineLocationFromMessage(userMessage));
        var locationFiltered = FilterSourcesForLocalBusinessDiscovery(
            userMessage,
            sources,
            locationContext,
            requiresExactLocationMatch);
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

        if (Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (host.Contains("wikipedia.org", StringComparison.Ordinal) ||
                host.Contains("wiktionary.org", StringComparison.Ordinal) ||
                host.Contains("britannica.com", StringComparison.Ordinal) ||
                host.Contains("dictionary.com", StringComparison.Ordinal) ||
                host.Contains("thesaurus.com", StringComparison.Ordinal) ||
                host.Contains("reddit.com", StringComparison.Ordinal) ||
                host.Contains("quora.com", StringComparison.Ordinal) ||
                host.Contains("facebook.com", StringComparison.Ordinal) ||
                host.Contains("instagram.com", StringComparison.Ordinal) ||
                host.Contains("tiktok.com", StringComparison.Ordinal) ||
                host.Contains("twitter.com", StringComparison.Ordinal) ||
                host.Contains("x.com", StringComparison.Ordinal) ||
                host.Contains("youtube.com", StringComparison.Ordinal))
            {
                return true;
            }

            if (host.Contains("terrysflorist.com", StringComparison.Ordinal) ||
                host.Contains("ftd.com", StringComparison.Ordinal) ||
                host.Contains("1800flowers.com", StringComparison.Ordinal) ||
                host.Contains("teleflora.com", StringComparison.Ordinal) ||
                host.Contains("fromyouflowers.com", StringComparison.Ordinal) ||
                host.Contains("flower.com", StringComparison.Ordinal) ||
                host.Contains("florgeous.com", StringComparison.Ordinal) ||
                host.Contains("seattleflowers.com", StringComparison.Ordinal) ||
                host.Contains("avasflowers.com", StringComparison.Ordinal) ||
                host.Contains("proflowers.com", StringComparison.Ordinal) ||
                host.Contains("ubereats.com", StringComparison.Ordinal) ||
                host.Contains("postmates.com", StringComparison.Ordinal) ||
                host.Contains("doordash.com", StringComparison.Ordinal) ||
                host.Contains("grubhub.com", StringComparison.Ordinal) ||
                host.Contains("slice.life", StringComparison.Ordinal))
            {
                return true;
            }
        }

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

        if (Regex.IsMatch(title, @"^(?:where|how)\s+to\s+(?:get|find)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(title, @"\b(?:baker(?:y|ies)|florists?|restaurants?|delis?|salons?|cafes?|coffee\s+shops?|stores?|pharmacies|groceries|dentists?|desserts?)\b", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(title, @"\b(?:guide|roundup|directory|best\s+of)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(title, @"\b(?:baker(?:y|ies)|florists?|restaurants?|delis?|salons?|cafes?|coffee\s+shops?|stores?|pharmacies|groceries|dentists?|desserts?)\b", RegexOptions.IgnoreCase))
            return true;

        // "{Category} in {Location}" pattern — e.g. "Bakeries in Olympia, WA"
        if (Regex.IsMatch(title, @"^(?:bakeries|bakery|florists?|restaurants?|delis?|salons?|cafes?|coffee\s+shops?|stores?|pharmacies|groceries|dentists?)\b.*\bin\b", RegexOptions.IgnoreCase))
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

            if (host.Contains("yellowpages.com", StringComparison.Ordinal) ||
                host.Contains("realyellowpages.com", StringComparison.Ordinal) ||
                host.Contains("superpages.com", StringComparison.Ordinal) ||
                host.Contains("citysearch.com", StringComparison.Ordinal) ||
                host.Contains("manta.com", StringComparison.Ordinal) ||
                host.Contains("mapquest.com", StringComparison.Ordinal) ||
                host.Contains("experienceolympia", StringComparison.Ordinal) ||
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

        if (lower.Contains("bank", StringComparison.Ordinal) || lower.Contains("banks", StringComparison.Ordinal))
            return ["bank", "banks", "credit union", "financial"];

        if (lower.Contains("park", StringComparison.Ordinal) || lower.Contains("parks", StringComparison.Ordinal))
            return ["park", "parks", "playground", "nature", "trail"];

        // Chain / brand name detection — match by the brand name itself.
        var brandKeyword = ExtractBrandKeyword(lower);
        if (brandKeyword is not null)
            return [brandKeyword];

        return [];
    }

    private static IReadOnlyList<string> GetLocalBusinessRetryAliases(string userMessage)
    {
        var label = GetRequestedLocalBusinessLabel(userMessage);

        return label switch
        {
            "delis" => ["sandwich shop", "delicatessen"],
            "florists" => ["flower shop"],
            "bakeries" => ["pastry shop", "cake shop"],
            "coffee shops" => ["espresso bar", "coffee shop"],
            _ => []
        };
    }

    private static string? ExtractBrandKeyword(string lower)
    {
        ReadOnlySpan<string> brands =
        [
            "starbucks", "mcdonald", "walmart", "target", "costco",
            "walgreens", "cvs", "home depot", "lowe's", "lowes",
            "taco bell", "burger king", "wendy's", "wendys",
            "subway", "chick-fil-a", "chipotle", "domino's", "dominos",
            "dunkin", "panda express", "pizza hut", "papa john",
            "whole foods", "kroger", "safeway", "albertsons",
            "best buy", "gamestop", "petco", "petsmart",
            "ikea", "nordstrom", "aldi", "sprouts", "fred meyer", "winco",
            "trader joe"
        ];

        foreach (var brand in brands)
        {
            if (lower.Contains(brand, StringComparison.Ordinal))
                return brand;
        }

        return null;
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
        if (lower.Contains("bank", StringComparison.Ordinal))
            return "banks";
        if (lower.Contains("park", StringComparison.Ordinal))
            return "parks";
        if (lower.Contains("grocery", StringComparison.Ordinal) || lower.Contains("supermarket", StringComparison.Ordinal))
            return "grocery stores";

        // Brand name as label (e.g. "Starbucks locations").
        var brand = ExtractBrandKeyword(lower);
        if (brand is not null)
            return $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(brand)} locations";

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
            "banks" => "bank",
            "parks" => "park",
            "grocery stores" => "grocery store",
            _ => label.TrimEnd('s')
        };
    }

    private string? ResolveLocalBusinessLocationContext(string userMessage)
    {
        var explicitLocation = ExtractInlineLocationFromMessage(userMessage)?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitLocation))
            return explicitLocation;

        if (!string.IsNullOrWhiteSpace(UserLocationHint))
            return UserLocationHint.Trim();

        return null;
    }

    private static IReadOnlyList<SourceItem> FilterSourcesForLocalBusinessLocation(
        IReadOnlyList<SourceItem> sources,
        string? locationContext,
        bool requireMatch = false)
    {
        var location = BuildLocalNewsLocationTokens(locationContext);
        if (location is null)
            return [];

        var locationMatches = sources
            .Where(source => requireMatch
                ? IsExactLocalBusinessLocationMatch(source, location)
                : IsLocalBusinessLocationMatch(source, location))
            .ToList();

        if (locationMatches.Count > 0)
            return locationMatches;

        if (requireMatch)
            return [];

        return sources
            .Where(source => !IsExplicitlyOutOfAreaLocalBusinessSource(source, location))
            .ToList();
    }

    private static IReadOnlyList<SourceItem> FilterSourcesForLocalBusinessDiscovery(
        string userMessage,
        IReadOnlyList<SourceItem> sources,
        string? locationContext,
        bool requireMatch = false)
    {
        var locationFiltered = FilterSourcesForLocalBusinessLocation(sources, locationContext, requireMatch);
        if (locationFiltered.Count > 0)
            return locationFiltered;

        var location = BuildLocalNewsLocationTokens(locationContext);
        if (location is null)
            return [];

        if (requireMatch)
            return [];

        return sources
            .Where(source => !IsExplicitlyOutOfAreaLocalBusinessSource(source, location) &&
                             SourceLooksLikeLocalBusinessCandidate(source, userMessage))
            .ToList();
    }

    private static bool SourceLooksLikeLocalBusinessCandidate(SourceItem source, string userMessage)
    {
        if (IsDirectoryAggregatorSource(source))
            return true;

        return !string.IsNullOrWhiteSpace(ExtractBusinessNameFromSourceTitle(source.Title, userMessage));
    }

    private static bool IsLocalBusinessLocationMatch(SourceItem source, LocalNewsLocationTokens location)
    {
        var rawStory = BuildRawLocalBusinessStoryText(source);
        var normalizedStory = BuildLocalNewsStoryText(source);
        if (string.IsNullOrWhiteSpace(normalizedStory))
            return false;

        var cityMatch = ContainsNormalizedTerm(normalizedStory, location.CityPhrase) ||
                        location.CityTokens.Any(token => ContainsNormalizedTerm(normalizedStory, token));

        if (cityMatch)
            return !MentionsConflictingState(rawStory, normalizedStory, location);

        return MentionsTargetState(rawStory, normalizedStory, location);
    }

    private static bool IsExactLocalBusinessLocationMatch(SourceItem source, LocalNewsLocationTokens location)
    {
        var rawStory = BuildRawLocalBusinessStoryText(source);
        var normalizedStory = BuildLocalNewsStoryText(source);
        if (string.IsNullOrWhiteSpace(normalizedStory))
            return false;

        var cityMatch = ContainsNormalizedTerm(normalizedStory, location.CityPhrase) ||
                        location.CityTokens.Any(token => ContainsNormalizedTerm(normalizedStory, token));
        if (!cityMatch)
            return false;

        return !MentionsConflictingState(rawStory, normalizedStory, location);
    }

    private static bool IsExplicitlyOutOfAreaLocalBusinessSource(SourceItem source, LocalNewsLocationTokens targetLocation)
    {
        var rawStory = BuildRawLocalBusinessStoryText(source);
        var normalizedStory = BuildLocalNewsStoryText(source);
        if (string.IsNullOrWhiteSpace(normalizedStory))
            return false;

        if (IsLocalBusinessLocationMatch(source, targetLocation))
            return false;

        return MentionsConflictingState(rawStory, normalizedStory, targetLocation);

    }

    private static string BuildRawLocalBusinessStoryText(SourceItem source)
    {
        return $"{source.Title} {source.Snippet}".Trim();
    }

    private static bool MentionsTargetState(
        string rawStory,
        string normalizedStory,
        LocalNewsLocationTokens targetLocation)
    {
        if (!string.IsNullOrWhiteSpace(targetLocation.StateName) &&
            ContainsNormalizedTerm(normalizedStory, targetLocation.StateName))
        {
            return true;
        }

        return ContainsUppercaseStateCode(rawStory, targetLocation.StateCode);
    }

    private static bool MentionsConflictingState(
        string rawStory,
        string normalizedStory,
        LocalNewsLocationTokens targetLocation)
    {
        var mentionsTargetState = false;
        var mentionsNonTargetState = false;

        foreach (var state in StateCodeToName)
        {
            var normalizedStateName = NormalizeLocalNewsText(state.Value);
            var mentionsThisState =
                ContainsNormalizedTerm(normalizedStory, normalizedStateName) ||
                ContainsUppercaseStateCode(rawStory, state.Key);

            if (!mentionsThisState)
                continue;

            var isTargetState =
                (!string.IsNullOrWhiteSpace(targetLocation.StateCode) &&
                 string.Equals(targetLocation.StateCode, state.Key, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(targetLocation.StateName) &&
                 string.Equals(targetLocation.StateName, normalizedStateName, StringComparison.OrdinalIgnoreCase));

            if (isTargetState)
                mentionsTargetState = true;
            else
                mentionsNonTargetState = true;
        }

        return mentionsNonTargetState && !mentionsTargetState;
    }

    private static bool ContainsUppercaseStateCode(string rawText, string? stateCode)
    {
        if (string.IsNullOrWhiteSpace(rawText) || string.IsNullOrWhiteSpace(stateCode))
            return false;

        return Regex.IsMatch(
            rawText,
            $@"(?<![A-Za-z]){Regex.Escape(stateCode.Trim().ToUpperInvariant())}(?![A-Za-z])",
            RegexOptions.CultureInvariant);
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
        var lowerUserMessage = (userMessage ?? string.Empty).Trim().ToLowerInvariant();
        if (lowerUserMessage.Contains("timeout", StringComparison.Ordinal) &&
            IsExplicitLookupToolInvocationRequest(userMessage))
        {
            return new AgentResponse
            {
                Text = "Web search hit a timeout before results were retrieved. Please retry in a moment or narrow the query.",
                Success = true,
                ToolCallsMade = toolCallsMade.ToList(),
                LlmRoundTrips = 0
            };
        }

        var isLocalNewsRequest = LocalNewsSignalRegex.IsMatch(userMessage ?? "");
        // Prefer explicit location from the message, then resolved entity,
        // then the profile-based location hint.
        var explicitLocation = ExtractExplicitNewsLocation(userMessage);
        var locationHint = explicitLocation?.Trim();
        if (string.IsNullOrWhiteSpace(locationHint))
            locationHint = resolvedLocationName?.Trim();
        if (string.IsNullOrWhiteSpace(locationHint))
            locationHint = UserLocationHint?.Trim();

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
        var hasBusinessCategory =
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
            lower.Contains("bank", StringComparison.Ordinal) ||
            lower.Contains("banks", StringComparison.Ordinal) ||
            lower.Contains("park", StringComparison.Ordinal) ||
            lower.Contains("parks", StringComparison.Ordinal) ||
            lower.Contains("dentist", StringComparison.Ordinal) ||
            lower.Contains("clinic", StringComparison.Ordinal);

        if (hasBusinessCategory)
            return true;

        var hasBusinessContext =
            lower.Contains("near me", StringComparison.Ordinal) ||
            lower.Contains("nearby", StringComparison.Ordinal) ||
            lower.Contains("in ", StringComparison.Ordinal) ||
            lower.Contains("open now", StringComparison.Ordinal) ||
            lower.Contains("business hours", StringComparison.Ordinal) ||
            lower.Contains("hours for", StringComparison.Ordinal) ||
            lower.Contains("where can i", StringComparison.Ordinal) ||
            lower.Contains("find me", StringComparison.Ordinal) ||
            lower.Contains("recommend", StringComparison.Ordinal);

        return hasBusinessContext &&
               (lower.Contains("open", StringComparison.Ordinal) ||
                lower.Contains("hours", StringComparison.Ordinal));
    }

    private static bool IsNearbyLocalBusinessDiscoveryRequest(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower) || !IsLocalBusinessNoResultsRequest(userMessage ?? string.Empty))
            return false;

        return lower.Contains("near me", StringComparison.Ordinal) ||
               lower.Contains("nearby", StringComparison.Ordinal) ||
               lower.Contains("around me", StringComparison.Ordinal) ||
               lower.Contains("around here", StringComparison.Ordinal) ||
               lower.Contains("close by", StringComparison.Ordinal);
    }

    private static bool IsSpecificLocalBusinessVerificationRequest(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower) || !IsLocalBusinessNoResultsRequest(userMessage ?? string.Empty))
            return false;

        if (IntentFeatureExtractor.LooksLikeGenericLocalBusinessDiscovery(lower))
            return false;

        return IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower) ||
               lower.Contains("open", StringComparison.Ordinal) ||
               lower.Contains("hours", StringComparison.Ordinal) ||
               lower.Contains("close", StringComparison.Ordinal);
    }

    private AgentResponse BuildNearbyLocalBusinessNoMatchResponse(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var label = SingularizeBusinessLabel(GetRequestedLocalBusinessLabel(userMessage));
        var location = ResolveLocalBusinessLocationContext(userMessage)?.Trim();
        var nearbyClause = string.IsNullOrWhiteSpace(location)
            ? "nearby"
            : $"nearby in {location}";

        var text = $"I don't have a trustworthy {label} recommendation {nearbyClause} yet from the returned search results. " +
                   $"Share a neighborhood, ZIP code, or major street and I'll rerun a tighter {label} search.";

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0
        };
    }

    private AgentResponse BuildLocalBusinessVerificationFallbackResponse(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var cleanedQuery = DeepDiveCoordinator.CleanQueryForWebFallback(userMessage).Trim();
        if (string.IsNullOrWhiteSpace(cleanedQuery))
            cleanedQuery = GetRequestedLocalBusinessLabel(userMessage);

        var location = ResolveLocalBusinessLocationContext(userMessage)?.Trim();
        if (!string.IsNullOrWhiteSpace(location) &&
            !cleanedQuery.Contains(location, StringComparison.OrdinalIgnoreCase))
        {
            cleanedQuery = Regex.IsMatch(cleanedQuery, @"\b(?:in|near)\b", RegexOptions.IgnoreCase)
                ? $"{cleanedQuery} {location}"
                : $"{cleanedQuery} in {location}";
        }

        var title = cleanedQuery
            .Replace("\u2019", "'", StringComparison.Ordinal)
            .Replace("`", "'", StringComparison.Ordinal);
        var verificationLine = HasUsableLocalBusinessWebSearchEvidence(toolCallsMade)
            ? "Search results returned candidate pages, but none gave a trustworthy live-hours answer for this business."
            : "This live search run did not surface a trustworthy business page or official hours listing for that query.";

        var text = string.Join("\n",
        [
            $"**{title}**",
            "Verification recommended",
            verificationLine,
            "Best next step: check the official store locator or call the location before heading over.",
            "Sources checked: deep-dive.",
            $"Briefing summary: hours and review details are based on currently available web sources ({DateTime.Now.Year})."
        ]);

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0
        };
    }

    private static bool HasUsableLocalBusinessWebSearchEvidence(
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        foreach (var toolCall in toolCallsMade)
        {
            if (!toolCall.Success ||
                !toolCall.ToolName.Contains("websearch", StringComparison.OrdinalIgnoreCase) &&
                !toolCall.ToolName.Contains("web_search", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(toolCall.Result) ||
                LooksLikeNoResultsPayload(toolCall.Result) ||
                toolCall.Result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                WebToolFailureMapper.TryParseStructuredError(toolCall.Result, out _, out _))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    // ── Enriched Local Business Discovery ────────────────────────────
    // Three-phase pipeline: web_search → browser_navigate (read articles)
    // → places_lookup (get real business details).

    private sealed record EnrichedBusiness(
        string Name,
        string? Address,
        string? Phone,
        string? Website,
        bool? OpenNow,
        double? Rating,
        int? TotalRatings,
        string? Snippet);

    private async Task<AgentResponse> EnrichLocalBusinessDiscoveryAsync(
        string userMessage,
        IReadOnlyList<SourceItem> sources,
        string? locationContext,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        // ── Phase 1: Extract candidate business names from two sources ──
        // a) Non-aggregator source titles (individual business websites).
        // b) Aggregator article content (read the top 1-2 list articles).

        var candidateNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1a. Individual business sites — their title IS the business name.
        foreach (var source in sources)
        {
            if (IsDirectoryAggregatorSource(source))
                continue;
            var name = ExtractBusinessNameFromSourceTitle(source.Title, userMessage);
            if (name is not null && name.Length >= 3 && !seen.Contains(name))
            {
                seen.Add(name);
                candidateNames.Add(name);
            }
        }

        // 1b. Aggregator articles — read and extract numbered/heading names.
        var aggregators = sources
            .Where(s => IsDirectoryAggregatorSource(s) && !IsJunkUrl(s.Url))
            .Take(LocalBusinessMaxArticleFetches)
            .ToList();

        if (aggregators.Count > 0)
        {
            var articleTexts = new List<string>();
            foreach (var source in aggregators)
            {
                var content = await FetchSingleUrlAsync(source.Url, toolCallsMade, ct);
                if (!string.IsNullOrWhiteSpace(content))
                    articleTexts.Add(content!);
            }

            foreach (var name in ExtractBusinessNamesFromArticles(articleTexts, userMessage))
            {
                if (!seen.Contains(name))
                {
                    seen.Add(name);
                    candidateNames.Add(name);
                }
            }
        }

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOCAL_BUSINESS_ENRICH_EXTRACT",
            Result = candidateNames.Count > 0 ? "ok" : "no_names",
            Details = new Dictionary<string, object>
            {
                ["names_extracted"] = string.Join(" | ", candidateNames),
                ["from_titles"] = candidateNames.Count - (aggregators.Count > 0 ? 0 : 0),
                ["aggregators_read"] = aggregators.Count
            }
        });

        if (candidateNames.Count == 0)
        {
            var supplementalNames = await FetchSupplementalLocalBusinessNamesAsync(
                userMessage,
                locationContext,
                [],
                toolCallsMade,
                ct);

            if (supplementalNames.Count > 0)
                return BuildCleanedLocalBusinessResponse(userMessage, supplementalNames, locationContext, toolCallsMade);

            var directPlaceFallback = await TryBuildLocalBusinessDirectPlaceFallbackAsync(
                userMessage,
                toolCallsMade,
                ct,
                surfaceConfigMessage: false);
            if (directPlaceFallback is not null)
                return directPlaceFallback;

            return BuildLocalBusinessDiscoveryResponse(userMessage, sources, toolCallsMade);
        }

        // ── Phase 2: Look up each candidate via places_lookup ──
        if (HarnessDisallowsPlacesTools())
            return BuildCleanedLocalBusinessResponse(userMessage, candidateNames, locationContext, toolCallsMade);

        var enriched = new List<EnrichedBusiness>();
        var lookups = Math.Min(candidateNames.Count, LocalBusinessMaxPlaceLookups);

        for (var i = 0; i < lookups; i++)
        {
            var name = candidateNames[i];
            var placeQuery = string.IsNullOrWhiteSpace(locationContext)
                ? name
                : $"{name} {locationContext}";

            var business = await LookupPlaceAsync(placeQuery, locationContext, toolCallsMade, ct);
            if (business is not null)
                enriched.Add(business);
            else if (toolCallsMade.Count > 0 &&
                     !toolCallsMade[^1].Success &&
                     toolCallsMade[^1].ToolName.Contains("places", StringComparison.OrdinalIgnoreCase))
                break; // Permanent config failure (e.g. missing API key) — stop wasting budget.
        }

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOCAL_BUSINESS_ENRICH_LOOKUP",
            Result = enriched.Count > 0 ? "ok" : "no_places",
            Details = new Dictionary<string, object>
            {
                ["lookups_attempted"] = lookups,
                ["places_found"] = enriched.Count
            }
        });

        // If places_lookup returned nothing (tool unavailable, etc.),
        // build a cleaned-up response using just the extracted names.
        if (enriched.Count == 0)
            return BuildCleanedLocalBusinessResponse(userMessage, candidateNames, locationContext, toolCallsMade);

        return BuildEnrichedLocalBusinessResponse(userMessage, enriched, locationContext, toolCallsMade);
    }

    private async Task<(bool Attempted, AgentResponse? Response)> TryHandleLocalBusinessWithOpenPlacesAsync(
        string userMessage,
        string? locationContext,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var discovery = await DiscoverOpenPlacesAsync(
            userMessage,
            locationContext,
            toolCallsMade,
            ct,
            radiusMeters: LocalBusinessNearbyPrimaryRadiusMeters,
            maxResults: LocalBusinessDiscoveryFetchMaxResults);
        if (discovery is null)
            return (false, null);

        if (discovery.Results.Count > 0 &&
            discovery.Results.Count < LocalBusinessMinimumDisplayResults)
        {
            var expanded = await DiscoverOpenPlacesAsync(
                userMessage,
                locationContext,
                toolCallsMade,
                ct,
                radiusMeters: LocalBusinessNearbyExpandedRadiusMeters,
                maxResults: LocalBusinessDiscoveryFetchMaxResults);

            if (expanded is not null && expanded.Results.Count > 0)
                discovery = MergePlacesDiscoveryResults(discovery, expanded);
        }

        if (discovery.Results.Count == 0)
        {
            var fallback = await TryBuildLocalBusinessDirectPlaceFallbackAsync(userMessage, toolCallsMade, ct);
            if (fallback is not null)
                return (true, fallback);

            // Preserve the older web-search path when open-data discovery did
            // not actually yield usable business results.
            return (false, null);
        }

        var sources = BuildSourceItemsFromPlacesDiscovery(discovery, PreferredUnits);
        var responseLocation = string.IsNullOrWhiteSpace(locationContext)
            ? discovery.ResolvedLocation
            : locationContext;
        var businesses = discovery.Results
            .Take(LocalBusinessTargetResults)
            .Select(candidate =>
            {
                // Combine address and distance into one detail line so the
                // formatter renders a single clean line per business instead
                // of duplicating the address in an italic snippet below.
                var detailParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(candidate.Address))
                    detailParts.Add(candidate.Address);
                if (candidate.DistanceMeters.HasValue)
                    detailParts.Add(FormatPlaceDistance(candidate.DistanceMeters.Value, PreferredUnits));

                return new EnrichedBusiness(
                    candidate.Name,
                    detailParts.Count > 0 ? string.Join(" · ", detailParts) : null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null);
            })
            .ToList();

        if (businesses.Count < LocalBusinessMinimumDisplayResults)
        {
            // Keep Open Places responses deterministic and tool-local.
            // have nearby structured place matches.
        }

        var includesSupplementalSpots = businesses.Any(biz =>
            string.IsNullOrWhiteSpace(biz.Address) &&
            !string.IsNullOrWhiteSpace(biz.Name));

        Session.RecordSearchResults(
            SearchMode.WebFactFind,
            userMessage,
            "any",
            sources,
            DateTimeOffset.UtcNow);
        Session.LastWasLocalBusinessDiscovery = true;
        Session.RecordLocalBusinessCandidates(GetRequestedLocalBusinessLabel(userMessage), sources);

        return (true, BuildEnrichedLocalBusinessResponse(
            userMessage,
            businesses,
            responseLocation,
            toolCallsMade,
            includesSupplementalSpots,
            sources));
    }

    private async Task<IReadOnlyList<string>> FetchSupplementalLocalBusinessNamesAsync(
        string userMessage,
        string? locationContext,
        IEnumerable<string> existingNames,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var label = GetRequestedLocalBusinessLabel(userMessage);
        var location = string.IsNullOrWhiteSpace(locationContext)
            ? UserLocationHint
            : locationContext;
        var query = string.IsNullOrWhiteSpace(location)
            ? $"best {label} near me"
            : $"best {label} in {location}";

        var seen = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        var existingCount = seen.Count;

        var toolResult = await CallWebSearchAsync(
            query,
            "any",
            toolCallsMade,
            ct,
            originalUserMessage: userMessage,
            maxResults: LocalBusinessFetchMaxResults);

        if (string.IsNullOrWhiteSpace(toolResult) || LooksLikeNoResultsPayload(toolResult))
            return [];

        var sources = ParseSourcesFromToolResult(toolResult);
        if (sources.Count == 0)
            return [];

        sources = [.. SelectLocalBusinessDiscoverySources(
            userMessage,
            sources,
            LocalBusinessTargetResults,
            location)];

        foreach (var source in sources)
        {
            var name = ExtractBusinessNameFromSourceTitle(source.Title, userMessage);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (!seen.Add(name))
                continue;

            names.Add(name);
            if (names.Count + existingCount >= LocalBusinessTargetResults)
                break;
        }

        return names;
    }

    /// <summary>Test-only forwarder for <see cref="ExtractBusinessNameFromSourceTitle"/>.</summary>
    internal static string? TestHook_ExtractBusinessNameFromSourceTitle(string? title, string userMessage)
        => ExtractBusinessNameFromSourceTitle(title, userMessage);

    /// <summary>
    /// Extracts a business name from a source title by stripping common
    /// suffixes like site taglines, location qualifiers, and separators.
    /// e.g. "San Francisco Street Bakery – Olympia's Neighborhood Bakery" → "San Francisco Street Bakery"
    /// </summary>
    private static string? ExtractBusinessNameFromSourceTitle(string? title, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var rescuedSeoName = TryExtractBusinessNameFromSeoTitle(title!, userMessage);
        if (!string.IsNullOrWhiteSpace(rescuedSeoName))
            return rescuedSeoName;

        var rescuedDashName = TryExtractBusinessNameFromDashSeparatedTitle(title!, userMessage);
        if (!string.IsNullOrWhiteSpace(rescuedDashName))
            return rescuedDashName;

        var name = StripLocalBusinessNamePrefixNoise(title!.Trim());

        // Strip everything after common separators (–, —, |, :) when
        // the trailing portion is a tagline or description.
        name = Regex.Replace(name, @"\s*[–—|]\s+.*$", "").Trim();
        name = Regex.Replace(name, @"\s*:\s+(?:Find|Shop|Order|Browse|Welcome|Home|About|Our).*$", "", RegexOptions.IgnoreCase).Trim();

        // Strip "(City, ST)" or location parenthetical.
        name = Regex.Replace(name, @"\s*\(.*\)\s*$", "").Trim();

        // Strip trailing "- <anything>" — covers site names, city names,
        // directory brands ("- The Real Yellow Pages", "- MapQuest", etc.).
        name = Regex.Replace(name, @"\s+-\s+.+$", "").Trim();

        return IsAcceptableSourceTitleBusinessName(name, userMessage)
            ? name
            : null;
    }

    private static string? TryExtractBusinessNameFromSeoTitle(string title, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var pipeSegments = title.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanExtractedBusinessName)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();
        if (pipeSegments.Count > 1 &&
            LooksLikeSeoLocalBusinessCategorySegment(pipeSegments[0], userMessage))
        {
            for (var i = pipeSegments.Count - 1; i >= 1; i--)
            {
                if (IsAcceptableSourceTitleBusinessName(pipeSegments[i], userMessage))
                    return pipeSegments[i];
            }
        }

        var colonIndex = title.IndexOf(':');
        if (colonIndex > 0)
        {
            var prefix = CleanExtractedBusinessName(title[..colonIndex]);
            var suffix = title[(colonIndex + 1)..].Trim();
            var suffixCandidate = Regex.Split(suffix, @"\s+[|–—-]\s+")
                .FirstOrDefault()?
                .Trim() ?? string.Empty;
            suffixCandidate = CleanExtractedBusinessName(suffixCandidate);

            if (LooksLikeSeoLocalBusinessCategorySegment(prefix, userMessage) &&
                IsAcceptableSourceTitleBusinessName(suffixCandidate, userMessage))
            {
                return suffixCandidate;
            }
        }

        return null;
    }

    private static string? TryExtractBusinessNameFromDashSeparatedTitle(string title, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var rawSegments = Regex.Split(title, @"\s+-\s+")
            .Select(segment => segment.Trim())
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();
        var segments = rawSegments
            .Select(CleanExtractedBusinessName)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();
        if (segments.Count < 2 || rawSegments.Count != segments.Count)
            return null;

        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (!LooksLikeSeoLocalBusinessCategorySegment(rawSegments[i], userMessage) &&
                !LooksLikeLocalBusinessPageSectionSegment(rawSegments[i], userMessage))
            {
                continue;
            }

            for (var j = segments.Count - 1; j > i; j--)
            {
                if (IsAcceptableSourceTitleBusinessName(segments[j], userMessage) ||
                    LooksLikeStrongLocalBusinessTitleCandidate(segments[j], userMessage))
                    return segments[j];
            }
        }

        return null;
    }

    private static bool IsAcceptableSourceTitleBusinessName(string name, string userMessage)
    {
        if (name.Length < 3 || name.Length > 60)
            return false;
        if (LooksLikeSourceBrandOrAggregatorName(name))
            return false;
        if (IsGenericNonBusinessName(name))
            return false;
        if (LooksLikeDiscussionOrQuestionTitle(name))
            return false;
        if (LooksLikeLocationOnlyBusinessCandidate(name, userMessage))
            return false;
        if (LooksLikeChainDepartmentCandidate(name, userMessage))
            return false;
        if (LooksLikeGenericLocalBusinessCategoryPhrase(name, userMessage))
            return false;
        if (Regex.IsMatch(name, @"^store\s*#?\d+\b", RegexOptions.IgnoreCase))
            return false;
        if (name.StartsWith("r/", StringComparison.Ordinal))
            return false;
        if (Regex.IsMatch(name, @"^(?:Best|Top)\s+\d*\s*", RegexOptions.IgnoreCase))
            return false;

        var label = GetRequestedLocalBusinessLabel(userMessage);
        if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase))
            return false;

        var singular = SingularizeBusinessLabel(label);
        if (Regex.IsMatch(name, $@"^{Regex.Escape(label)}\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, $@"^{Regex.Escape(singular)}\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeSourceBrandOrAggregatorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = Regex.Replace(name, @"\s+", " ").Trim().ToLowerInvariant();
        return normalized is "reddit" or
               "yelp" or
               "tripadvisor" or
               "facebook" or
               "instagram" or
               "quora" or
               "wikipedia" or
               "wiktionary" or
               "google search" or
               "bing" or
               "the real yellow pages" or
               "yellow pages";
    }

    private static bool LooksLikeStrongLocalBusinessTitleCandidate(string name, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (IsGenericNonBusinessName(name))
            return false;
        if (LooksLikeDiscussionOrQuestionTitle(name))
            return false;
        if (LooksLikeLocationOnlyBusinessCandidate(name, userMessage))
            return false;
        if (LooksLikeChainDepartmentCandidate(name, userMessage))
            return false;
        if (LooksLikeGenericLocalBusinessCategoryPhrase(name, userMessage))
            return false;
        if (LooksLikeBlacklistedFloristBrandCandidate(name, userMessage))
            return false;

        var signalTokens = GetLocalBusinessMatchKeywords(userMessage)
            .SelectMany(keyword => Regex.Matches(keyword, @"[A-Za-z0-9']+")
                .Select(match => match.Value.ToLowerInvariant()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (GetRequestedLocalBusinessLabel(userMessage).Equals("florists", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var floristSignal in new[] { "gift", "gifts", "bloom", "blooms", "petal", "petals", "rose", "roses", "garden", "gardens" })
                signalTokens.Add(floristSignal);
        }

        var genericTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "by", "of", "the", "delivery", "deliveries", "shop", "shops", "service", "services",
            "local", "find", "about", "contact", "home", "welcome", "best", "top"
        };

        var locationTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitLocation = ExtractInlineLocationFromMessage(userMessage);
        if (!string.IsNullOrWhiteSpace(explicitLocation))
        {
            foreach (Match match in Regex.Matches(explicitLocation, @"[A-Za-z0-9']+"))
                locationTokens.Add(match.Value);
        }

        var tokens = Regex.Matches(name, @"[A-Za-z][A-Za-z'&-]*")
            .Select(match => match.Value)
            .ToList();
        if (tokens.Count < 2)
            return false;

        var hasSignal = tokens.Any(token => signalTokens.Contains(token));
        if (!hasSignal)
            return false;

        return tokens.Any(token =>
            token.Length > 1 &&
            char.IsUpper(token[0]) &&
            !signalTokens.Contains(token) &&
            !genericTokens.Contains(token) &&
            !locationTokens.Contains(token));
    }

    private static bool LooksLikeSeoLocalBusinessCategorySegment(string segment, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return false;

        var lower = segment.ToLowerInvariant();
        var requestedTokens = GetLocalBusinessMatchKeywords(userMessage)
            .SelectMany(keyword => Regex.Matches(keyword, @"[A-Za-z0-9']+")
                .Select(match => match.Value.ToLowerInvariant()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedTokens.Count == 0)
            return false;

        var segmentTokens = Regex.Matches(segment, @"[A-Za-z0-9']+")
            .Select(match => match.Value.ToLowerInvariant())
            .ToList();
        if (!segmentTokens.Any(token => requestedTokens.Contains(token)))
            return false;

        var explicitLocation = ExtractInlineLocationFromMessage(userMessage);
        if (!string.IsNullOrWhiteSpace(explicitLocation))
        {
            var normalizedSegment = NormalizeLocalNewsText(segment);
            var normalizedLocation = NormalizeLocalNewsText(explicitLocation);
            if (ContainsNormalizedTerm(normalizedSegment, normalizedLocation))
                return true;

            var city = explicitLocation.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(city) &&
                ContainsNormalizedTerm(normalizedSegment, NormalizeLocalNewsText(city)))
            {
                return true;
            }
        }

        return lower.Contains(" near ", StringComparison.Ordinal) ||
               lower.Contains(" in ", StringComparison.Ordinal);
    }

    private static bool LooksLikeLocalBusinessPageSectionSegment(string segment, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return false;

        var lower = segment.ToLowerInvariant();
        if (!Regex.IsMatch(lower, @"^(?:about|shop(?:\s+by)?|contact|home|welcome)\b", RegexOptions.IgnoreCase))
            return false;

        return LooksLikeSeoLocalBusinessCategorySegment(segment, userMessage) ||
               GetLocalBusinessMatchKeywords(userMessage)
                   .Any(keyword => lower.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeGenericLocalBusinessCategoryPhrase(string name, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var keywordTokens = GetLocalBusinessMatchKeywords(userMessage)
            .SelectMany(keyword => Regex.Matches(keyword, @"[A-Za-z0-9']+")
                .Select(match => match.Value.ToLowerInvariant()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keywordTokens.Count == 0)
            return false;

        var genericTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "best", "by", "co", "company", "delivery", "deliveries",
            "find", "for", "fresh", "gift", "gifts", "good", "in", "local", "near",
            "nearby", "online", "order", "orders", "service", "services", "shop", "shops",
            "send", "store", "stores", "the", "to"
        };

        var meaningfulTokens = Regex.Matches(name, @"[A-Za-z0-9']+")
            .Select(match => match.Value.ToLowerInvariant())
            .Where(token => token.Length > 1 && !genericTokens.Contains(token))
            .ToList();
        if (meaningfulTokens.Count == 0)
            return false;

        return meaningfulTokens.All(token =>
        {
            if (keywordTokens.Contains(token))
                return true;

            if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 3)
                return keywordTokens.Contains(token[..^3] + "y");

            if (token.EndsWith("s", StringComparison.Ordinal) && token.Length > 2)
                return keywordTokens.Contains(token[..^1]);

            return false;
        });
    }

    private static bool LooksLikeDiscussionOrQuestionTitle(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.Contains('?', StringComparison.Ordinal))
            return true;

        return Regex.IsMatch(
            name,
            @"^(?:help!?|does\s+anyone\s+know|anyone\s+know|looking\s+for|where\s+can\s+i|can\s+you\s+recommend|recommend\s+me)\b",
            RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeLocationOnlyBusinessCandidate(string name, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(userMessage))
            return false;

        var explicitLocation = ExtractInlineLocationFromMessage(userMessage)?.Trim();
        if (string.IsNullOrWhiteSpace(explicitLocation))
            return false;

        var normalizedName = Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.', ',', '!', '?');
        var normalizedLocation = Regex.Replace(explicitLocation, @"\s+", " ").Trim().TrimEnd('.', ',', '!', '?');
        if (normalizedName.Equals(normalizedLocation, StringComparison.OrdinalIgnoreCase))
            return true;

        var city = normalizedLocation.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return !string.IsNullOrWhiteSpace(city) &&
               normalizedName.Equals(city, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFloristProductCatalogCandidate(string name, string userMessage)
    {
        if (!GetRequestedLocalBusinessLabel(userMessage).Equals("florists", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();
        var hasBusinessSignal = Regex.IsMatch(
            lower,
            @"\b(?:florist|floral|flowers?|blooms?|gifts?|shop|studio|design|greenhouse|garden)\b",
            RegexOptions.IgnoreCase);
        if (hasBusinessSignal)
            return false;

        return Regex.IsMatch(
            lower,
            @"\b(?:bouquet|spray|basket|arrangement|wreath|centerpiece|corsage|standing\s+spray|floor\s+basket)\b",
            RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeChainDepartmentCandidate(string name, string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var lower = name.ToLowerInvariant();
        var explicitBrand = ExtractBrandKeyword((userMessage ?? string.Empty).ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(explicitBrand) &&
            lower.Contains(explicitBrand, StringComparison.Ordinal))
        {
            return false;
        }

        var hasDepartmentPromoLanguage =
            lower.Contains("store #", StringComparison.Ordinal) ||
            lower.Contains("party tray", StringComparison.Ordinal) ||
            lower.Contains("party trays", StringComparison.Ordinal) ||
            lower.Contains("charcuterie", StringComparison.Ordinal) ||
            lower.Contains("gourmet cheese", StringComparison.Ordinal) ||
            lower.Contains("grab & go", StringComparison.Ordinal) ||
            lower.Contains("sandwiches & wraps", StringComparison.Ordinal);

        ReadOnlySpan<string> chainBrands =
        [
            "walmart", "sam's club", "sams club", "costco", "target",
            "kroger", "safeway", "albertsons", "fred meyer", "winco"
        ];

        var mentionsChainBrand = false;
        foreach (var brand in chainBrands)
        {
            if (lower.Contains(brand, StringComparison.Ordinal))
            {
                mentionsChainBrand = true;
                break;
            }
        }

        if (!mentionsChainBrand && !hasDepartmentPromoLanguage)
            return false;

        var requestedKeywords = GetLocalBusinessMatchKeywords(userMessage ?? string.Empty);
        var mentionsRequestedCategory = requestedKeywords.Count == 0 ||
            requestedKeywords.Any(keyword => lower.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        return mentionsRequestedCategory;
    }

    private async Task<string?> FetchSingleUrlAsync(
        string url,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var args = JsonSerializer.Serialize(new { url });
        string? content = null;
        var resolvedToolName = BrowseToolName;

        void RecordFailure(string result)
        {
            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName = resolvedToolName,
                Arguments = args,
                Result = result,
                Success = false
            });
        }

        try
        {
            content = await _mcp.CallToolAsync(BrowseToolName, args, ct);
            if (WebToolFailureMapper.TryParseStructuredError(content, out _, out _) ||
                content.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                RecordFailure(content);
                return null;
            }
        }
        catch (OperationCanceledException ex)
        {
            RecordFailure($"Error: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ContainsToolBudgetOrCancellationMarker(ex.Message))
        {
            RecordFailure($"Error: {ex.Message}");
            return null;
        }
        catch
        {
            try
            {
                resolvedToolName = BrowseToolNameAlt;
                content = await _mcp.CallToolAsync(BrowseToolNameAlt, args, ct);
                if (WebToolFailureMapper.TryParseStructuredError(content, out _, out _) ||
                    content.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                {
                    RecordFailure(content);
                    return null;
                }
            }
            catch (OperationCanceledException ex)
            {
                RecordFailure($"Error: {ex.Message}");
                return null;
            }
            catch (Exception ex) when (ContainsToolBudgetOrCancellationMarker(ex.Message))
            {
                RecordFailure($"Error: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                RecordFailure($"Error: {ex.Message}");
                return null;
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = resolvedToolName,
            Arguments = args,
            Result    = content!.Length > 1200 ? content[..1200] + "…" : content,
            Success   = true
        });

        if (content!.Length > MaxArticleChars)
            content = content[..MaxArticleChars];

        return content;
    }

    private static bool LastToolCallWasBudgetOrCancellation(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string toolNameFragment)
    {
        if (toolCallsMade.Count == 0)
            return false;

        var lastToolCall = toolCallsMade[^1];
        return !lastToolCall.Success &&
               lastToolCall.ToolName.Contains(toolNameFragment, StringComparison.OrdinalIgnoreCase) &&
               IsBudgetOrCancellationToolFailure(lastToolCall.Result);
    }

    private static bool AnyToolCallHadBudgetOrCancellation(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        params string[] toolNameFragments)
    {
        return toolCallsMade.Any(call =>
            !call.Success &&
            toolNameFragments.Any(fragment =>
                call.ToolName.Contains(fragment, StringComparison.OrdinalIgnoreCase)) &&
            IsBudgetOrCancellationToolFailure(call.Result));
    }

    private static bool AnyToolCallHadBudgetOrCancellationSince(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        int startIndex,
        params string[] toolNameFragments)
    {
        if (toolCallsMade.Count == 0 || startIndex >= toolCallsMade.Count)
            return false;

        if (startIndex < 0)
            startIndex = 0;

        for (var i = startIndex; i < toolCallsMade.Count; i++)
        {
            var call = toolCallsMade[i];
            if (call.Success)
                continue;

            if (!toolNameFragments.Any(fragment =>
                    call.ToolName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (IsBudgetOrCancellationToolFailure(call.Result))
                return true;
        }

        return false;
    }

    private async Task<AgentResponse?> TryBuildLocalBusinessBrowserFallbackAsync(
        string userMessage,
        string? locationContext,
        IReadOnlyList<SourceItem>? fallbackSources,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var location = string.IsNullOrWhiteSpace(locationContext)
            ? ResolveLocalBusinessLocationContext(userMessage)
            : locationContext;
        var hasExplicitLocation = !string.IsNullOrWhiteSpace(ExtractInlineLocationFromMessage(userMessage));
        var preferScopedRecovery =
            hasExplicitLocation &&
            !string.IsNullOrWhiteSpace(location) &&
            GetRequestedLocalBusinessLabel(userMessage).Equals("florists", StringComparison.OrdinalIgnoreCase);

        if (preferScopedRecovery)
        {
            var recoveredNames = await RecoverLocalBusinessNamesFromScopedFallbackSearchAsync(
                userMessage,
                location!,
                toolCallsMade,
                ct);
            if (recoveredNames.Count > 0)
                return BuildCleanedLocalBusinessResponse(userMessage, recoveredNames, location, toolCallsMade);
        }

        if ((fallbackSources is null || fallbackSources.Count == 0) &&
            !string.IsNullOrWhiteSpace(location) &&
            hasExplicitLocation)
        {
            fallbackSources = BuildDirectLocalBusinessDirectoryFallbackSources(userMessage, location);
        }

        var initialBrowseAttemptStart = toolCallsMade.Count;
        var names = await ExtractLocalBusinessNamesFromBrowsedSourcesAsync(
            userMessage,
            location,
            fallbackSources,
            toolCallsMade,
            ct);
        var browserToolStopped = AnyToolCallHadBudgetOrCancellationSince(
            toolCallsMade,
            initialBrowseAttemptStart,
            "browser");

        if (names.Count == 0 &&
            !browserToolStopped &&
            !string.IsNullOrWhiteSpace(location) &&
            hasExplicitLocation)
        {
            var directDirectorySources = BuildDirectLocalBusinessDirectoryFallbackSources(userMessage, location);
            if (directDirectorySources.Count > 0 &&
                !SameLocalBusinessFallbackSources(fallbackSources, directDirectorySources))
            {
                var directDirectoryAttemptStart = toolCallsMade.Count;
                names = await ExtractLocalBusinessNamesFromBrowsedSourcesAsync(
                    userMessage,
                    location,
                    directDirectorySources,
                    toolCallsMade,
                    ct);
                browserToolStopped = AnyToolCallHadBudgetOrCancellationSince(
                    toolCallsMade,
                    directDirectoryAttemptStart,
                    "browser");
            }
        }

        if (names.Count == 0 &&
            !preferScopedRecovery &&
            !string.IsNullOrWhiteSpace(location) &&
            hasExplicitLocation)
        {
            names = await RecoverLocalBusinessNamesFromScopedFallbackSearchAsync(
                userMessage,
                location,
                toolCallsMade,
                ct);
        }

        var shouldReusePriorSearchEvidence = browserToolStopped ||
            AnyToolCallHadBudgetOrCancellation(toolCallsMade, "browser", "web_search", "websearch", "places");

        if (names.Count == 0 && shouldReusePriorSearchEvidence)
        {
            names = ExtractLocalBusinessNamesFromPriorSearchToolCalls(
                toolCallsMade,
                userMessage,
                location);
        }

        if (names.Count == 0)
            return null;

        return BuildCleanedLocalBusinessResponse(userMessage, names, location, toolCallsMade);
    }

    private static bool SameLocalBusinessFallbackSources(
        IReadOnlyList<SourceItem>? existingSources,
        IReadOnlyList<SourceItem> candidateSources)
    {
        if (existingSources is null || existingSources.Count != candidateSources.Count)
            return false;

        for (var i = 0; i < existingSources.Count; i++)
        {
            if (!string.Equals(existingSources[i].Url, candidateSources[i].Url, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private async Task<IReadOnlyList<string>> RecoverLocalBusinessNamesFromScopedFallbackSearchAsync(
        string userMessage,
        string location,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var scopedQueries = BuildLocalBusinessScopedFallbackQueries(userMessage, location);
        if (scopedQueries.Count == 0)
            return [];

        var requireExactLocationMatch = !string.IsNullOrWhiteSpace(ExtractInlineLocationFromMessage(userMessage));
        var locationTokens = BuildLocalNewsLocationTokens(location);

        foreach (var query in scopedQueries)
        {
            var toolResult = await CallWebSearchAsync(
                query,
                "any",
                toolCallsMade,
                ct,
                originalUserMessage: null,
                maxResults: Math.Min(5, LocalBusinessFetchMaxResults));

            if (string.IsNullOrWhiteSpace(toolResult) ||
                LooksLikeNoResultsPayload(toolResult) ||
                WebToolFailureMapper.TryParseStructuredError(toolResult, out _, out _) ||
                WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is not null)
            {
                if (IsBudgetOrCancellationToolFailure(toolResult))
                    break;
                continue;
            }

            if (!requireExactLocationMatch ||
                locationTokens is null ||
                ContentMatchesLocalBusinessLocation(toolResult, locationTokens))
            {
                var textOnlyNames = ExtractLocalBusinessNamesFromTextOnlySearchResult(toolResult, userMessage);
                if (textOnlyNames.Count > 0)
                    return textOnlyNames;
            }

            var scopedSources = ParseSourcesFromToolResult(toolResult)
                .Where(source => !IsJunkBusinessSource(source))
                .ToList();
            var siteDomain = TryExtractScopedSearchDomain(query);
            if (!string.IsNullOrWhiteSpace(siteDomain))
            {
                scopedSources = scopedSources
                    .Where(source => MatchesLocalBusinessScopedFallbackDomain(source, [siteDomain]))
                    .ToList();
            }
            else
            {
                scopedSources = FilterGeneralLocalBusinessRecoverySources(
                    scopedSources,
                    userMessage,
                    location);
            }

            if (scopedSources.Count == 0)
                continue;

            var selectedSources = SelectLocalBusinessDiscoverySources(
                userMessage,
                scopedSources,
                LocalBusinessTargetResults,
                location);
            if (selectedSources.Count > 0)
            {
                scopedSources = [.. selectedSources];
            }
            else if (requireExactLocationMatch)
            {
                continue;
            }

            var searchSourceNames = ExtractLocalBusinessNamesFromSearchSources(scopedSources, userMessage);
            if (searchSourceNames.Count > 0)
            {
                return searchSourceNames;
            }

            var browsedNames = await ExtractLocalBusinessNamesFromBrowsedSourcesAsync(
                userMessage,
                location,
                scopedSources,
                toolCallsMade,
                ct);
            if (browsedNames.Count > 0)
                return browsedNames;
        }

        var bingRssNames = await RecoverFloristNamesFromBingRssAsync(
            userMessage,
            location,
            toolCallsMade,
            ct);
        if (bingRssNames.Count > 0)
            return bingRssNames;

        return [];
    }

    private static IReadOnlyList<string> ExtractLocalBusinessNamesFromPriorSearchToolCalls(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string userMessage,
        string? locationContext)
    {
        var requireExactLocationMatch = !string.IsNullOrWhiteSpace(ExtractInlineLocationFromMessage(userMessage));
        var locationTokens = BuildLocalNewsLocationTokens(locationContext);
        var names = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var toolCall in toolCallsMade)
        {
            if (!toolCall.Success ||
                !toolCall.ToolName.Contains("websearch", StringComparison.OrdinalIgnoreCase) &&
                !toolCall.ToolName.Contains("web_search", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(toolCall.Result) ||
                LooksLikeNoResultsPayload(toolCall.Result) ||
                WebToolFailureMapper.TryParseStructuredError(toolCall.Result, out _, out _))
            {
                continue;
            }

            var sources = ParseSourcesFromToolResult(toolCall.Result);
            var canUseTextOnly = true;
            if (sources.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(locationContext))
                {
                    var locationFiltered = FilterSourcesForLocalBusinessDiscovery(
                        userMessage,
                        sources,
                        locationContext,
                        requireExactLocationMatch);
                    if (locationFiltered.Count > 0)
                        sources = [.. locationFiltered];
                    else if (requireExactLocationMatch)
                        continue;
                }
            }
            else if (requireExactLocationMatch &&
                     locationTokens is not null &&
                     !ContentMatchesLocalBusinessLocation(toolCall.Result, locationTokens))
            {
                canUseTextOnly = false;
            }

            if (canUseTextOnly)
            {
                AddLocalBusinessNames(
                    names,
                    seen,
                    ExtractLocalBusinessNamesFromTextOnlySearchResult(toolCall.Result, userMessage),
                    userMessage);
            }

            if (sources.Count > 0)
            {
                AddLocalBusinessNames(
                    names,
                    seen,
                    ExtractLocalBusinessNamesFromSearchSources(sources, userMessage),
                    userMessage);
            }

            if (names.Count >= LocalBusinessTargetResults)
                break;
        }

        return names;
    }

    private async Task<IReadOnlyList<string>> RecoverFloristNamesFromBingRssAsync(
        string userMessage,
        string location,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (!GetRequestedLocalBusinessLabel(userMessage).Equals("florists", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(location))
        {
            return [];
        }

        var locationTerms = BuildLocalBusinessRecoveryLocationTerms(location).ToLowerInvariant();
        var rssUrl = $"https://www.bing.com/search?format=rss&q={Uri.EscapeDataString($"{locationTerms} florist gifts")}";
        var content = await FetchSingleUrlAsync(rssUrl, toolCallsMade, ct);
        if (string.IsNullOrWhiteSpace(content) || LooksLikeBotChallengePage(content))
            return [];

        return ExtractLocalBusinessNamesFromBingRssContent(content, userMessage);
    }

    private static IReadOnlyList<string> ExtractLocalBusinessNamesFromBingRssContent(
        string content,
        string userMessage)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var decoded = WebUtility.HtmlDecode(content);
        var names = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(
                     decoded,
                     @"<title>(?<title>[^<]+)</title>",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var title = match.Groups["title"].Value.Trim();
            if (string.IsNullOrWhiteSpace(title) ||
                title.Contains(" - Bing", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = ExtractBusinessNameFromSourceTitle(title, userMessage);
            if (!string.IsNullOrWhiteSpace(name))
                AddLocalBusinessNames(names, seen, [name], userMessage);
        }

        if (names.Count < LocalBusinessTargetResults)
        {
            AddLocalBusinessNames(
                names,
                seen,
                ExtractLocalBusinessNamesFromBingRssTitleText(decoded, userMessage),
                userMessage);
        }

        if (names.Count < LocalBusinessTargetResults)
        {
            AddLocalBusinessNames(
                names,
                seen,
                ExtractBusinessNamesFromArticles([decoded], userMessage),
                userMessage);
        }

        if (names.Count < LocalBusinessTargetResults)
        {
            AddLocalBusinessNames(
                names,
                seen,
                ExtractLocalBusinessNamesFromLooseText(decoded, userMessage),
                userMessage);
        }

        return names;
    }

    private static IReadOnlyList<string> ExtractLocalBusinessNamesFromBingRssTitleText(
        string content,
        string userMessage)
    {
        if (!GetRequestedLocalBusinessLabel(userMessage).Equals("florists", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddMatches(List<string> names, HashSet<string> seen, string text, string pattern)
        {
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var candidate = CleanExtractedBusinessName(match.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
                    continue;

                names.Add(candidate);
            }
        }

        AddMatches(
            names,
            seen,
            content,
            @"\b([A-Z][A-Za-z'&.\-]+(?:\s+(?:[A-Z][A-Za-z'&.\-]+|&|and|by|of|the)){0,6}\s+(?:Florist|Flowers|Floral|Blooms?|Gifts?|Petals?|Roses?|Gardens?))\b");
        AddMatches(
            names,
            seen,
            content,
            @"\b(Flowers?\s+by\s+[A-Z][A-Za-z'&.\-]+(?:\s+[A-Z][A-Za-z'&.\-]+){0,4})\b");

        return names;
    }

    private static List<SourceItem> FilterGeneralLocalBusinessRecoverySources(
        IReadOnlyList<SourceItem> sources,
        string userMessage,
        string location)
    {
        if (!GetRequestedLocalBusinessLabel(userMessage).Equals("florists", StringComparison.OrdinalIgnoreCase))
            return [.. sources];

        var locationTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryParseCityStateLocation(location, out var citySlug, out _))
        {
            foreach (var token in citySlug.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                locationTokens.Add(token);
        }

        return sources
            .Where(source => !IsDirectoryAggregatorSource(source))
            .Where(source =>
            {
                var haystack = $"{source.Domain} {source.Url}";
                if (haystack.Contains("florist", StringComparison.OrdinalIgnoreCase) ||
                    haystack.Contains("flower", StringComparison.OrdinalIgnoreCase) ||
                    haystack.Contains("floral", StringComparison.OrdinalIgnoreCase) ||
                    haystack.Contains("bloom", StringComparison.OrdinalIgnoreCase) ||
                    haystack.Contains("petal", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return locationTokens.Any(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    private static IReadOnlyList<string> ExtractLocalBusinessNamesFromTextOnlySearchResult(
        string toolResult,
        string userMessage)
    {
        var stripped = SanitizeLocalBusinessTextOnlySearchResult(
            StripSourcesJson(toolResult).Trim(),
            userMessage);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return [];
        }

        var names = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        AddLocalBusinessNames(
            names,
            seen,
            ExtractBusinessNamesFromArticles([stripped], userMessage),
            userMessage);

        if (names.Count < LocalBusinessTargetResults)
        {
            AddLocalBusinessNames(
                names,
                seen,
                ExtractLocalBusinessNamesFromLooseText(stripped, userMessage),
                userMessage);
        }

        return names;
    }

    private static string SanitizeLocalBusinessTextOnlySearchResult(string text, string userMessage)
    {
        if (!GetRequestedLocalBusinessLabel(userMessage).Equals("florists", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var blacklistedDomainMarkers = new[]
        {
            "florgeous.com",
            "seattleflowers.com",
            "terrysflorist.com",
            "1800flowers.com",
            "ftd.com"
        };

        var filteredBlocks = Regex.Split(text, @"(?:\r?\n){2,}")
            .Select(block => block.Trim())
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .Where(block => !blacklistedDomainMarkers.Any(marker =>
                block.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return filteredBlocks.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine + Environment.NewLine, filteredBlocks);
    }

    private static string? TryExtractScopedSearchDomain(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var match = Regex.Match(
            query,
            @"\bsite:(?<domain>[^\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups["domain"].Value.Trim()
            : null;
    }

    private static bool MatchesLocalBusinessScopedFallbackDomain(
        SourceItem source,
        IReadOnlyList<string> preferredDomains)
    {
        if (!string.IsNullOrWhiteSpace(source.Domain) &&
            preferredDomains.Any(domain => source.Domain.Contains(domain, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) &&
               preferredDomains.Any(domain =>
                   uri.Host.Contains(domain, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> BuildLocalBusinessScopedFallbackQueries(
        string userMessage,
        string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return [];

        var label = GetRequestedLocalBusinessLabel(userMessage);
        if (!label.Equals("florists", StringComparison.OrdinalIgnoreCase))
            return [];

        var locationTerms = BuildLocalBusinessRecoveryLocationTerms(location);

        return
        [
            $"site:chamberofcommerce.com {locationTerms} florist",
            $"site:loc8nearme.com {locationTerms} florist"
        ];
    }

    private static string BuildFloristRecoverySearchExclusionTerms()
    {
        return string.Join(
            ' ',
            new[]
            {
                "-doordash",
                "-flower.com",
                "-terrysflorist",
                "-ftd",
                "-1800flowers",
                "-teleflora",
                "-fromyouflowers",
                "-florgeous",
                "-seattleflowers",
                "-avasflowers",
                "-proflowers"
            });
    }

    private static string BuildLocalBusinessRecoveryLocationTerms(string location)
    {
        if (TryParseCityStateLocation(location, out var citySlug, out var stateSlug))
        {
            var city = string.Join(
                " ",
                citySlug.Split('-', StringSplitOptions.RemoveEmptyEntries)
                    .Select(token => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(token)));
            return $"{city} {stateSlug.ToUpperInvariant()}";
        }

        return location.Replace(",", " ", StringComparison.Ordinal)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static IReadOnlyList<SourceItem> BuildDirectLocalBusinessDirectoryFallbackSources(
        string userMessage,
        string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return [];

        var label = GetRequestedLocalBusinessLabel(userMessage);
        var locationText = location.Trim();
        var sources = new List<SourceItem>();

        if (TryBuildSuperpagesDirectoryUrl(label, locationText) is { Length: > 0 } superpagesUrl)
        {
            sources.Add(new SourceItem
            {
                Url = superpagesUrl,
                Title = $"{label} in {locationText} - Superpages",
                Domain = "superpages.com",
                Snippet = $"Local {label} listings in {locationText}."
            });
        }

        if (TryBuildYellowPagesDirectoryUrl(label, locationText) is { Length: > 0 } yellowPagesUrl)
        {
            sources.Add(new SourceItem
            {
                Url = yellowPagesUrl,
                Title = $"{label} in {locationText} - Yellow Pages",
                Domain = "yellowpages.com",
                Snippet = $"Local {label} listings in {locationText}."
            });
        }

        if (TryBuildRestaurantJiDirectoryUrl(label, locationText) is { Length: > 0 } restaurantJiUrl)
        {
            sources.Add(new SourceItem
            {
                Url = restaurantJiUrl,
                Title = $"Best {label} near {locationText} - Restaurantji",
                Domain = "restaurantji.com",
                Snippet = $"Local {label} listings in {locationText}."
            });
        }

        var yelpCategorySlug = GetYelpCategorySlug(label);
        var yelpUrl = string.IsNullOrWhiteSpace(yelpCategorySlug)
            ? $"https://www.yelp.com/search?find_desc={Uri.EscapeDataString(label)}&find_loc={Uri.EscapeDataString(locationText)}"
            : $"https://www.yelp.com/search?cflt={Uri.EscapeDataString(yelpCategorySlug)}&find_loc={Uri.EscapeDataString(locationText)}";

        sources.Add(new SourceItem
        {
            Url = yelpUrl,
            Title = $"Best {label} in {locationText}",
            Domain = "yelp.com",
            Snippet = $"Local {label} listings in {locationText}."
        });

        return sources;
    }

    private static string GetYelpCategorySlug(string businessLabel)
    {
        return businessLabel.Trim().ToLowerInvariant() switch
        {
            "delis" => "delis",
            "florists" => "florists",
            "bakeries" => "bakeries",
            "coffee shops" => "coffee",
            _ => string.Empty
        };
    }

    private static string? GetStaticDirectoryCategorySlug(string businessLabel)
    {
        return businessLabel.Trim().ToLowerInvariant() switch
        {
            "florists" => "florists",
            _ => null
        };
    }

    private static string? TryBuildSuperpagesDirectoryUrl(string businessLabel, string location)
    {
        var categorySlug = GetStaticDirectoryCategorySlug(businessLabel);
        if (string.IsNullOrWhiteSpace(categorySlug) ||
            !TryParseCityStateLocation(location, out var citySlug, out var stateSlug))
        {
            return null;
        }

        return $"https://www.superpages.com/{citySlug}-{stateSlug}/{categorySlug}";
    }

    private static string? TryBuildYellowPagesDirectoryUrl(string businessLabel, string location)
    {
        var categorySlug = GetStaticDirectoryCategorySlug(businessLabel);
        if (string.IsNullOrWhiteSpace(categorySlug) ||
            !TryParseCityStateLocation(location, out var citySlug, out var stateSlug))
        {
            return null;
        }

        return $"https://www.yellowpages.com/{citySlug}-{stateSlug}/{categorySlug}";
    }

    private static string? TryBuildRestaurantJiDirectoryUrl(string businessLabel, string location)
    {
        if (!businessLabel.Equals("delis", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!TryParseCityStateLocation(location, out var citySlug, out var stateSlug))
            return null;

        return $"https://www.restaurantji.com/{stateSlug}/{citySlug}/deli/";
    }

    private static bool TryParseCityStateLocation(string location, out string citySlug, out string stateSlug)
    {
        citySlug = string.Empty;
        stateSlug = string.Empty;

        var locationMatch = Regex.Match(
            location,
            @"^(?<city>[A-Za-z][A-Za-z\s'.-]+),\s*(?<state>[A-Za-z]{2})$",
            RegexOptions.CultureInvariant);
        if (!locationMatch.Success)
            return false;

        citySlug = Regex.Replace(locationMatch.Groups["city"].Value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        stateSlug = locationMatch.Groups["state"].Value.Trim().ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(citySlug) && !string.IsNullOrWhiteSpace(stateSlug);
    }

    private async Task<IReadOnlyList<string>> ExtractLocalBusinessNamesFromBrowsedSourcesAsync(
        string userMessage,
        string? locationContext,
        IReadOnlyList<SourceItem>? fallbackSources,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (fallbackSources is null || fallbackSources.Count == 0)
            return [];

        var requireExactLocationMatch = !string.IsNullOrWhiteSpace(ExtractInlineLocationFromMessage(userMessage));
        var locationTokens = BuildLocalNewsLocationTokens(locationContext);
        var filteredSources = fallbackSources
            .Where(source => !IsJunkBusinessSource(source) && !IsJunkUrl(source.Url))
            .ToList();
        if (filteredSources.Count == 0)
            return [];

        if (!string.IsNullOrWhiteSpace(locationContext))
        {
            var locationFiltered = FilterSourcesForLocalBusinessDiscovery(
                userMessage,
                filteredSources,
                locationContext,
                requireExactLocationMatch);
            if (locationFiltered.Count > 0)
                filteredSources = [.. locationFiltered];
            else if (requireExactLocationMatch)
                return [];
        }

        var browsableSources = ResolveSourceUrls(filteredSources)
            .Where(source => !IsJunkUrl(source.Url))
            .OrderBy(source => IsDirectoryAggregatorSource(source) ? 0 : 1)
            .Take(LocalBusinessMaxBrowserFallbackFetches)
            .ToList();
        if (browsableSources.Count == 0)
            return [];

        var names = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in browsableSources)
        {
            var content = await FetchSingleUrlAsync(source.Url, toolCallsMade, ct);
            if (string.IsNullOrWhiteSpace(content))
            {
                if (LastToolCallWasBudgetOrCancellation(toolCallsMade, "browser"))
                    break;
                continue;
            }

            if (LooksLikeBotChallengePage(content))
                continue;

            if (requireExactLocationMatch &&
                locationTokens is not null &&
                !ContentMatchesLocalBusinessLocation(content, locationTokens))
            {
                continue;
            }

            IEnumerable<string> articleCandidates = ExtractBusinessNamesFromArticles([content], userMessage);
            if (requireExactLocationMatch && locationTokens is not null)
            {
                articleCandidates = FilterLocalBusinessCandidatesByContentLocation(
                    articleCandidates,
                    content,
                    locationTokens);
            }

            AddLocalBusinessNames(
                names,
                seen,
                articleCandidates,
                userMessage);

            if (names.Count < LocalBusinessTargetResults)
            {
                IEnumerable<string> looseCandidates = ExtractLocalBusinessNamesFromLooseText(content, userMessage);
                if (requireExactLocationMatch && locationTokens is not null)
                {
                    looseCandidates = FilterLocalBusinessCandidatesByContentLocation(
                        looseCandidates,
                        content,
                        locationTokens);
                }

                AddLocalBusinessNames(
                    names,
                    seen,
                    looseCandidates,
                    userMessage);
            }

            if (names.Count >= LocalBusinessTargetResults)
                break;
        }

        return names;
    }

    private static void AddLocalBusinessNames(
        List<string> names,
        Dictionary<string, int> seen,
        IEnumerable<string> candidates,
        string userMessage)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (LooksLikeLocationOnlyBusinessCandidate(candidate, userMessage))
                continue;
            if (LooksLikeGenericLocalBusinessCategoryPhrase(candidate, userMessage))
                continue;
            if (LooksLikeDirectoryHeadingCandidate(candidate, userMessage))
                continue;
            if (LooksLikeWeakExtractedLocalBusinessName(candidate, userMessage))
                continue;
            if (LooksLikeBlacklistedFloristBrandCandidate(candidate, userMessage))
                continue;
            if (LooksLikeFloristProductCatalogCandidate(candidate, userMessage))
                continue;

            var canonicalName = CanonicalizeLocalBusinessName(candidate, userMessage);
            if (string.IsNullOrWhiteSpace(canonicalName))
                continue;

            if (seen.TryGetValue(canonicalName, out var existingIndex))
            {
                if (candidate.Length > names[existingIndex].Length)
                    names[existingIndex] = candidate;
                continue;
            }

            seen[canonicalName] = names.Count;
            names.Add(candidate);
            if (names.Count >= LocalBusinessTargetResults)
                break;
        }
    }

    private static bool LooksLikeWeakExtractedLocalBusinessName(string candidate, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return true;

        if (candidate.Contains('.', StringComparison.Ordinal))
            return true;

        var tokens = Regex.Matches(candidate, @"[A-Za-z][A-Za-z'&-]*")
            .Select(match => match.Value)
            .ToList();
        if (tokens.Count == 0)
            return true;

        if (GetRequestedLocalBusinessLabel(userMessage).Equals("florists", StringComparison.OrdinalIgnoreCase))
        {
            var floristSignalTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "florist",
                "floral",
                "flower",
                "flowers",
                "bloom",
                "blooms",
                "petal",
                "petals",
                "gift",
                "gifts",
                "rose",
                "roses",
                "garden",
                "gardens"
            };

            if (!tokens.Any(token => floristSignalTokens.Contains(token)))
                return true;
        }

        var keywordTokens = GetLocalBusinessMatchKeywords(userMessage)
            .SelectMany(keyword => Regex.Matches(keyword, @"[A-Za-z0-9']+")
                .Select(match => match.Value.ToLowerInvariant()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        static bool MatchesKeyword(string token, IReadOnlySet<string> keywordTokens)
        {
            var normalized = token.ToLowerInvariant();
            if (keywordTokens.Contains(normalized))
                return true;
            if (normalized.EndsWith("ies", StringComparison.Ordinal) && normalized.Length > 3)
                return keywordTokens.Contains(normalized[..^3] + "y");
            if (normalized.EndsWith("s", StringComparison.Ordinal) && normalized.Length > 2)
                return keywordTokens.Contains(normalized[..^1]);
            return false;
        }

        var containsBusinessKeyword = tokens.Any(token => MatchesKeyword(token, keywordTokens));
        if (tokens.Count == 1)
            return !containsBusinessKeyword;

        var connectorTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and",
            "by",
            "of",
            "the",
            "for"
        };

        var capitalizedSignificantTokens = tokens.Count(token =>
            !connectorTokens.Contains(token) &&
            token.Length > 0 &&
            char.IsUpper(token[0]));

        return !containsBusinessKeyword && capitalizedSignificantTokens < 2;
    }

    private static bool LooksLikeDirectoryHeadingCandidate(string candidate, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Regex.IsMatch(candidate, @"^(?:the\s+)?(?:best|top)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        var keywords = GetLocalBusinessMatchKeywords(userMessage);
        if (keywords.Count == 0 ||
            !keywords.Any(keyword => candidate.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return Regex.IsMatch(candidate, @"\b(?:in|near)\b", RegexOptions.IgnoreCase) ||
               !string.IsNullOrWhiteSpace(ExtractInlineLocationFromMessage(userMessage));
    }

    private static bool LooksLikeBlacklistedFloristBrandCandidate(string candidate, string userMessage)
    {
        if (!GetRequestedLocalBusinessLabel(userMessage).Equals("florists", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var lower = candidate.ToLowerInvariant();
        return lower.Contains("avas", StringComparison.Ordinal) ||
               lower.Contains("terry", StringComparison.Ordinal) ||
               lower.Contains("ftd", StringComparison.Ordinal) ||
               lower.Contains("teleflora", StringComparison.Ordinal) ||
               lower.Contains("1-800-flowers", StringComparison.Ordinal) ||
               lower.Contains("1800flowers", StringComparison.Ordinal) ||
               lower.Contains("fromyouflowers", StringComparison.Ordinal) ||
               lower.Contains("proflowers", StringComparison.Ordinal);
    }

    private static IEnumerable<string> FilterLocalBusinessCandidatesByContentLocation(
        IEnumerable<string> candidates,
        string content,
        LocalNewsLocationTokens location)
    {
        var original = candidates.ToList();
        if (original.Count == 0)
            return original;

        var filtered = new List<string>();
        foreach (var candidate in original)
        {
            if (CandidateContextMatchesLocalBusinessLocation(content, candidate, location))
                filtered.Add(candidate);
        }

        return filtered.Count > 0 || original.Count == 1 ? filtered.Count > 0 ? filtered : original : filtered;
    }

    private static bool CandidateContextMatchesLocalBusinessLocation(
        string content,
        string candidate,
        LocalNewsLocationTokens location)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(candidate))
            return false;

        foreach (Match match in Regex.Matches(content, Regex.Escape(candidate), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var windowStart = Math.Max(0, match.Index - 192);
            var windowLength = Math.Min(content.Length - windowStart, candidate.Length + 384);
            var window = content.Substring(windowStart, windowLength);
            if (ContentMatchesLocalBusinessLocation(window, location))
                return true;
        }

        return false;
    }

    private static string CanonicalizeLocalBusinessName(string candidate, string userMessage)
    {
        var normalized = Regex.Replace(candidate, @"\s+#\d+\b", string.Empty, RegexOptions.IgnoreCase)
            .Trim();

        if (GetRequestedLocalBusinessLabel(userMessage).Equals("delis", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Regex.Replace(
                normalized,
                @"\s+(?:deli|delicatessen)\b$",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();
        }

        return Regex.Replace(normalized, @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static bool ContentMatchesLocalBusinessLocation(string content, LocalNewsLocationTokens location)
    {
        var rawContent = content ?? string.Empty;
        var normalized = NormalizeLocalNewsText(content);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var cityMatch = ContainsNormalizedTerm(normalized, location.CityPhrase) ||
                        location.CityTokens.Any(token => ContainsNormalizedTerm(normalized, token));
        if (!cityMatch)
            return false;

        return !MentionsConflictingState(rawContent, normalized, location);
    }

    private static bool LooksLikeBotChallengePage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return (content.Contains("One last step", StringComparison.OrdinalIgnoreCase) &&
                content.Contains("solve the challenge", StringComparison.OrdinalIgnoreCase)) ||
               content.Contains("If you're having trouble accessing Google Search", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("CloudflareHandleCaptcha", StringComparison.OrdinalIgnoreCase) ||
               (content.Contains("captcha", StringComparison.OrdinalIgnoreCase) &&
                (content.Contains("Google Search", StringComparison.OrdinalIgnoreCase) ||
                 content.Contains("Bing", StringComparison.OrdinalIgnoreCase) ||
                 content.Contains("turnstile", StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> ExtractLocalBusinessNamesFromLooseText(string text, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var suffixPattern = GetLocalBusinessLooseSuffixPattern(userMessage);
        if (string.IsNullOrWhiteSpace(suffixPattern))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     text,
                     $@"\b([A-Z][A-Za-z0-9'&.-]+(?:\s+[A-Z][A-Za-z0-9'&.-]+){{0,4}}\s+(?:{suffixPattern}))\b",
                     RegexOptions.CultureInvariant))
        {
            var candidate = match.Groups[1].Value.Trim();
            var normalized = ExtractBusinessNameFromSourceTitle(candidate, userMessage) ?? candidate;
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(normalized) ||
                IsGenericNonBusinessName(normalized) ||
                LooksLikeGenericLocalBusinessCategoryPhrase(normalized, userMessage) ||
                LooksLikeChainDepartmentCandidate(normalized, userMessage) ||
                !seen.Add(normalized))
            {
                continue;
            }

            yield return normalized;
        }
    }

    private static IReadOnlyList<string> ExtractLocalBusinessNamesFromSearchSources(
        IReadOnlyList<SourceItem> sources,
        string userMessage)
    {
        var names = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var titleName = ExtractBusinessNameFromSourceTitle(source.Title, userMessage);
            if (!string.IsNullOrWhiteSpace(titleName))
            {
                AddLocalBusinessNames(
                    names,
                    seen,
                    [titleName],
                    userMessage);
            }

            var combinedText = string.Join(
                "\n",
                new[] { source.Title ?? string.Empty, source.Snippet ?? string.Empty }
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            if (string.IsNullOrWhiteSpace(combinedText))
                continue;

            AddLocalBusinessNames(
                names,
                seen,
                ExtractBusinessNamesFromArticles([combinedText], userMessage),
                userMessage);

            if (names.Count < LocalBusinessTargetResults)
            {
                AddLocalBusinessNames(
                    names,
                    seen,
                    ExtractLocalBusinessNamesFromLooseText(combinedText, userMessage),
                    userMessage);
            }

            if (names.Count >= LocalBusinessTargetResults)
                break;
        }

        return names;
    }

    private static string GetLocalBusinessLooseSuffixPattern(string userMessage)
    {
        return GetRequestedLocalBusinessLabel(userMessage) switch
        {
            "delis" => "(?:Deli|Delicatessen|Sandwich\\s+Shop|Sub(?:s|\\s+Shop))",
            "florists" => "(?:Florist|Flowers|Flower\\s+Shop|Blooms?)",
            "bakeries" => "(?:Bakery|Patisserie|Pastry\\s+Shop)",
            "coffee shops" => "(?:Cafe|Coffee|Roastery|Espresso\\s+Bar)",
            _ => string.Empty
        };
    }

    private async Task<PlacesDiscoveryResult?> DiscoverOpenPlacesAsync(
        string userMessage,
        string? locationContext,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct,
        int radiusMeters,
        int maxResults)
    {
        var args = JsonSerializer.Serialize(new
        {
            query = userMessage,
            userLocationHint = locationContext ?? UserLocationHint,
            maxResults,
            radiusMeters,
            locale = "en-US"
        });

        string? result = null;
        var resolvedToolName = PlacesDiscoverToolName;

        try
        {
            result = await _mcp.CallToolAsync(PlacesDiscoverToolName, args, ct);
        }
        catch
        {
            try
            {
                resolvedToolName = PlacesDiscoverToolNameAlt;
                result = await _mcp.CallToolAsync(PlacesDiscoverToolNameAlt, args, ct);
            }
            catch (Exception ex)
            {
                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName = resolvedToolName,
                    Arguments = args,
                    Result = $"Error: {ex.Message}",
                    Success = false
                });
                return null;
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = resolvedToolName,
            Arguments = args,
            Result = result!.Length > 200 ? result[..200] + "…" : result,
            Success = true
        });

        return ParsePlacesDiscoveryResult(result!);
    }

    private static PlacesDiscoveryResult MergePlacesDiscoveryResults(
        PlacesDiscoveryResult primary,
        PlacesDiscoveryResult expanded)
    {
        var merged = new List<PlaceDiscoveryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string Key(PlaceDiscoveryCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Id))
                return candidate.Id;

            var name = candidate.Name?.Trim().ToLowerInvariant() ?? string.Empty;
            var address = candidate.Address?.Trim().ToLowerInvariant() ?? string.Empty;
            return name + "|" + address;
        }

        void AddRange(IEnumerable<PlaceDiscoveryCandidate> candidates)
        {
            foreach (var candidate in candidates)
            {
                var key = Key(candidate);
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                    continue;

                merged.Add(candidate);
                if (merged.Count >= LocalBusinessTargetResults)
                    break;
            }
        }

        AddRange(primary.Results);
        if (merged.Count < LocalBusinessTargetResults)
            AddRange(expanded.Results);

        return primary with
        {
            ResolvedLocation = string.IsNullOrWhiteSpace(primary.ResolvedLocation)
                ? expanded.ResolvedLocation
                : primary.ResolvedLocation,
            Results = merged
        };
    }

    private static readonly Regex NumberedListRegex = new(
        @"^\s*\d{1,2}[.)]\s+(.+)",
        RegexOptions.Compiled);

    private static readonly Regex DashBulletRegex = new(
        @"^\s*[-–—•]\s+(.+)",
        RegexOptions.Compiled);

    private static readonly Regex HeadingStyleNameRegex = new(
        @"^([A-Z][A-Za-z''&\-]+(?:\s+(?:[A-Z][A-Za-z''&\-]+|&|and|by|of|the)){0,6})\s*$",
        RegexOptions.Compiled);

    // "Our current favorites are: 1: Left Bank Pastry, 2: Gotti Sweets..."
    private static readonly Regex InlineNumberedRegex = new(
        @"\d+:\s*([A-Z][A-Za-z''&\s\-]+?)(?:,\s*\d+:|$)",
        RegexOptions.Compiled);

    private static readonly Regex MarkdownLinkNameRegex = new(
        @"^(?:#+\s*)?(?:\d{1,2}[.)]\s*)?\[(?<name>[^\]]+)\]\([^\)]+\)",
        RegexOptions.Compiled);

    internal static IReadOnlyList<string> ExtractBusinessNamesFromArticles(
        IReadOnlyList<string> articleTexts,
        string userMessage)
    {
        var keywords = GetLocalBusinessMatchKeywords(userMessage);
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var text in articleTexts)
        {
            // Try inline numbered format first ("1: Name, 2: Name, 3: Name").
            var inlineMatches = InlineNumberedRegex.Matches(text);
            foreach (Match m in inlineMatches)
            {
                var inlineName = CleanExtractedBusinessName(m.Groups[1].Value);
                if (inlineName.Length >= 3 && inlineName.Length <= 60
                    && !IsGenericNonBusinessName(inlineName)
                    && !LooksLikeChainDepartmentCandidate(inlineName, userMessage)
                    && !seen.Contains(inlineName))
                {
                    seen.Add(inlineName);
                    candidates.Add(inlineName);
                }
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line) || line.Length < 3)
                    continue;

                string? name = null;

                // Pattern 1: Numbered list items "1. Left Bank Pastry"
                var numberedMatch = NumberedListRegex.Match(line);
                if (numberedMatch.Success)
                {
                    name = CleanExtractedBusinessName(numberedMatch.Groups[1].Value);
                }

                // Pattern 2: Dash/bullet list items "- Left Bank Pastry" or "• Left Bank Pastry"
                if (name is null)
                {
                    var dashMatch = DashBulletRegex.Match(line);
                    if (dashMatch.Success && line.Length <= 80)
                    {
                        var candidate = CleanExtractedBusinessName(dashMatch.Groups[1].Value);
                        // Only accept if it looks like a proper name (starts with uppercase).
                        if (candidate.Length >= 3 && candidate.Length <= 60
                            && char.IsUpper(candidate[0]))
                            name = candidate;
                    }
                }

                // Pattern 2b: Markdown heading/list links from directory pages.
                if (name is null)
                {
                    var markdownLinkMatch = MarkdownLinkNameRegex.Match(line);
                    if (markdownLinkMatch.Success)
                        name = CleanExtractedBusinessName(markdownLinkMatch.Groups["name"].Value);
                }

                // Pattern 3: Short standalone line that looks like a proper name,
                // followed by a detail line (address, rating, description).
                if (name is null && line.Length <= 60 && HeadingStyleNameRegex.IsMatch(line))
                {
                    var nextLine = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
                    if (LooksLikeBusinessDetailLine(nextLine))
                        name = CleanExtractedBusinessName(line);
                }

                if (name is null)
                    continue;

                // Filter out non-business names (generic phrases, location names, etc.).
                if (name.Length < 3 || name.Length > 60)
                    continue;
                if (IsGenericNonBusinessName(name))
                    continue;
                if (LooksLikeChainDepartmentCandidate(name, userMessage))
                    continue;
                if (seen.Contains(name))
                    continue;

                seen.Add(name);
                candidates.Add(name);
            }
        }

        return candidates;
    }

    private static string CleanExtractedBusinessName(string raw)
    {
        // Strip trailing punctuation, HTML artifacts, numbering.
        var cleaned = raw.Trim().Trim('"', '\'', '“', '”').Trim();
        cleaned = cleaned.TrimEnd('.', ':', '-', '–', '—');
        cleaned = Regex.Replace(cleaned, @"^#+\s*", string.Empty).Trim();
        cleaned = Regex.Replace(cleaned, @"^\d{1,2}[.)]\s*", string.Empty).Trim();
        cleaned = Regex.Replace(cleaned, @"^\[(?<text>[^\]]+)\]\([^\)]+\)$", "${text}").Trim();
        cleaned = Regex.Replace(cleaned, @"^\[(?<text>[^\]]+)\].*$", "${text}").Trim();
        cleaned = Regex.Replace(cleaned, @"\s*[-–—|]\s+.*$", "").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+\d+(\.\d+)?\s*stars?$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s*\(.*\)\s*$", "").Trim();
        cleaned = cleaned.Trim('"', '\'', '“', '”').Trim();
        return StripLocalBusinessNamePrefixNoise(cleaned);
    }

    private static string StripLocalBusinessNamePrefixNoise(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var cleaned = Regex.Replace(
            name,
            @"^(?:(?:you\s+)?(?:claimed|unclaimed)|about|shop(?:\s+by)?|contact|home|welcome(?:\s+to)?)\s+",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();

        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private static bool LooksLikeBusinessDetailLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        // Address patterns: digits + street types.
        if (Regex.IsMatch(line, @"\d+\s+\w+\s+(St|Ave|Blvd|Dr|Rd|Ln|Way|Ct|Pl|Hwy)", RegexOptions.IgnoreCase))
            return true;
        // Rating patterns: "4.5 stars", "★", "⭐"
        if (Regex.IsMatch(line, @"\d+\.\d+\s*stars?|★|⭐|\brating\b", RegexOptions.IgnoreCase))
            return true;
        // Phone patterns.
        if (Regex.IsMatch(line, @"\(\d{3}\)\s*\d{3}-\d{4}"))
            return true;
        // Price/description patterns.
        if (Regex.IsMatch(line, @"\$+|price range|open|closed|hours", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private static bool IsGenericNonBusinessName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"^(?:city|county|town|village)\s+of\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(lower, @"\bchamber\s+of\s+commerce\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(lower, @"\bbusiness\s+directory\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(lower, @"^(?:where|how)\s+to\s+(?:get|find)\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(lower, @"\b(?:recipe|recipes|idea|ideas|photo|photos|video|videos|tips?)\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(lower, @"\blocations?\s+in\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(lower, @"\bhours?\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(lower, @"\b(?:grocery|store|shop|bakery|bakeries|restaurant|cafe|coffee|deli|florist|salon|pharmacy|dentist|dessert)\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(lower, @"^(?:local\s+)?(?:baker(?:y|ies)|florists?|restaurants?|delis?|salons?|cafes?|coffee\s+shops?|stores?|pharmacies|groceries|dentists?)\s+(?:locations?|hours?)\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(lower, @"\b(?:guide|roundup|directory)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(lower, @"\b(?:baker(?:y|ies)|florists?|restaurants?|delis?|salons?|cafes?|coffee\s+shops?|stores?|pharmacies|groceries|dentists?|desserts?)\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(lower, @"^(?:local\s+)?(?:baker(?:y|ies)|florists?|restaurants?|delis?|salons?|cafes?|coffee\s+shops?|stores?|pharmacies|groceries|dentists?)\s+(?:in|near)\b", RegexOptions.IgnoreCase))
            return true;

        ReadOnlySpan<string> skip =
        [
            "best", "top", "the best", "our picks", "see also",
            "related", "nearby", "more", "advertisement",
            "featured", "sponsored", "about", "contact",
            "map", "directions", "overview", "reviews",
            "read more", "view all", "show more", "menu",
            "skip to content", "search", "home", "back to top"
        ];
        foreach (var s in skip)
        {
            if (lower.Equals(s, StringComparison.Ordinal) ||
                lower.StartsWith(s + " ", StringComparison.Ordinal))
                return true;
        }
        // All-lowercase single words are unlikely business names.
        if (!name.Any(char.IsUpper) && !name.Contains(' '))
            return true;
        return false;
    }

    private async Task<EnrichedBusiness?> LookupPlaceAsync(
        string query,
        string? locationHint,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var args = JsonSerializer.Serialize(new
        {
            query,
            timezone = "America/Los_Angeles",
            locale = "en-US",
            userLocationHint = locationHint ?? UserLocationHint,
            maxReviewSnippets = 1
        });

        string? result = null;
        var resolvedToolName = PlacesLookupToolName;

        void RecordFailure(string payload)
        {
            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName = resolvedToolName,
                Arguments = args,
                Result = payload,
                Success = false
            });
        }

        try
        {
            result = await _mcp.CallToolAsync(PlacesLookupToolName, args, ct);
            if (WebToolFailureMapper.TryParseStructuredError(result, out _, out _) ||
                result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                RecordFailure(result);
                return null;
            }
        }
        catch (OperationCanceledException ex)
        {
            RecordFailure($"Error: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ContainsToolBudgetOrCancellationMarker(ex.Message))
        {
            RecordFailure($"Error: {ex.Message}");
            return null;
        }
        catch
        {
            try
            {
                resolvedToolName = PlacesLookupToolNameAlt;
                result = await _mcp.CallToolAsync(PlacesLookupToolNameAlt, args, ct);
                if (WebToolFailureMapper.TryParseStructuredError(result, out _, out _) ||
                    result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                {
                    RecordFailure(result);
                    return null;
                }
            }
            catch (OperationCanceledException ex)
            {
                RecordFailure($"Error: {ex.Message}");
                return null;
            }
            catch (Exception ex) when (ContainsToolBudgetOrCancellationMarker(ex.Message))
            {
                RecordFailure($"Error: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                RecordFailure($"Error: {ex.Message}");
                return null;
            }
        }

        // If the result is a permanent config error (e.g. missing API key),
        // record as failed and return null — no point retrying.
        if (result is not null && LooksLikePlacesConfigError(result))
        {
            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName  = resolvedToolName,
                Arguments = args,
                Result    = "[Places provider unavailable]",
                Success   = false
            });
            return null;
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = resolvedToolName,
            Arguments = args,
            Result    = result!.Length > 200 ? result[..200] + "…" : result,
            Success   = true
        });

        return ParsePlaceLookupResult(result!);
    }

    private static bool LooksLikePlacesConfigError(string result) =>
        result.Contains("API key is not configured", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("API key not set", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("provider is not configured", StringComparison.OrdinalIgnoreCase);

    private async Task<AgentResponse?> TryBuildLocalBusinessDirectPlaceFallbackAsync(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct,
        bool surfaceConfigMessage = true)
    {
        if (HarnessDisallowsPlacesTools())
            return null;

        var safeUserMessage = userMessage ?? string.Empty;
        var lowerMessage = safeUserMessage.ToLowerInvariant();
        if (!IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerMessage) &&
            !IntentFeatureExtractor.LooksLikeGenericLocalBusinessDiscovery(lowerMessage) &&
            !IsSpecificLocalBusinessVerificationRequest(safeUserMessage))
        {
            return null;
        }

        var location = ResolveLocalBusinessLocationContext(safeUserMessage);
        var label = GetRequestedLocalBusinessLabel(safeUserMessage);
        var singular = SingularizeBusinessLabel(label);

        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(location))
        {
            queries.Add($"{label} near {location}");

            foreach (var alias in GetLocalBusinessRetryAliases(safeUserMessage))
            {
                queries.Add($"{alias} in {location}");
                queries.Add($"{alias} near {location}");
            }

            queries.Add($"{label} in {location}");
        }
        else
        {
            queries.Add($"{label} near me");

            foreach (var alias in GetLocalBusinessRetryAliases(safeUserMessage))
            {
                queries.Add($"{alias} near me");
                queries.Add(alias);
            }

            queries.Add(label);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enriched = new List<EnrichedBusiness>();
        foreach (var query in queries)
        {
            var business = await LookupPlaceAsync(query, location, toolCallsMade, ct);
            if (business is null)
            {
                // If the last tool call was a permanent config failure, stop retrying.
                if (toolCallsMade.Count > 0 &&
                    !toolCallsMade[^1].Success &&
                    toolCallsMade[^1].ToolName.Contains("places", StringComparison.OrdinalIgnoreCase))
                    break;
                continue;
            }
            if (IsGenericNonBusinessName(business.Name))
                continue;
            if (!seen.Add(business.Name))
                continue;

            enriched.Add(business);
            if (enriched.Count >= LocalBusinessTargetResults)
                break;
        }

        if (enriched.Count == 0)
        {
            var actionableConfigMessage = TryBuildPlacesConfigErrorMessage(toolCallsMade);
            if (surfaceConfigMessage && !string.IsNullOrWhiteSpace(actionableConfigMessage))
            {
                return new AgentResponse
                {
                    Text = actionableConfigMessage,
                    Success = true,
                    ToolCallsMade = toolCallsMade.ToList(),
                    LlmRoundTrips = 0
                };
            }

            return null;
        }

        var sourceItems = enriched
            .Where(b => !string.IsNullOrWhiteSpace(b.Name))
            .Select(b => new SourceItem
            {
                SourceId = SourceItem.ComputeSourceId($"direct-place::{b.Name}"),
                Url = string.IsNullOrWhiteSpace(b.Website) ? $"about:places/{Uri.EscapeDataString(b.Name)}" : b.Website!,
                Title = b.Name,
                Domain = "places_lookup",
                Snippet = b.Address ?? ""
            })
            .ToList();

        Session.RecordSearchResults(
            SearchMode.WebFactFind,
            safeUserMessage,
            "any",
            sourceItems,
            DateTimeOffset.UtcNow);
        Session.LastWasLocalBusinessDiscovery = true;
        Session.RecordLocalBusinessCandidates(label, sourceItems);

        return BuildEnrichedLocalBusinessResponse(safeUserMessage, enriched, location, toolCallsMade, includesSupplementalSpots: false);
    }

    private AgentResponse BuildDirectoryLocalBusinessResponse(
        string userMessage,
        IReadOnlyList<SourceItem> sources,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var businessLabel = GetRequestedLocalBusinessLabel(userMessage);
        var location = ResolveLocalBusinessLocationContext(userMessage)?.Trim();
        var locText = string.IsNullOrWhiteSpace(location) ? " nearby" : $" in {location}";

        var renderedSources = sources
            .Where(source => !IsJunkBusinessSource(source))
            .Where(source => !IsLowTrustLocalBusinessSource(source, userMessage))
            .Select(source => new
            {
                Title = StripTitleSuffix((source.Title ?? string.Empty).Trim()),
                source.Domain,
                source.Snippet
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .Where(item => !LooksLikeGenericLocalBusinessCategoryPhrase(item.Title, userMessage))
            .DistinctBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, LocalBusinessTargetResults))
            .ToList();

        if (renderedSources.Count == 0)
            return BuildNoResultsResponse(userMessage, toolCallsMade);

        var sb = new StringBuilder();
        sb.AppendLine($"Here are the live {businessLabel} results I found{locText}:");
        sb.AppendLine();

        foreach (var source in renderedSources)
        {
            sb.Append("- **");
            sb.Append(source.Title);
            sb.Append("**");

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(source.Snippet))
                details.Add(TrimSentence(source.Snippet, 160));
            if (!string.IsNullOrWhiteSpace(source.Domain))
                details.Add($"source: {source.Domain}");

            if (details.Count > 0)
            {
                sb.Append(" — ");
                sb.Append(string.Join(" · ", details));
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append("Pick any of these that catches your eye and I can dig deeper — hours, reviews, directions, the works.");

        return new AgentResponse
        {
            Text = sb.ToString(),
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0
        };
    }

    private static bool ShouldTryLocalBusinessBrowserFallback(
        string userMessage,
        IReadOnlyList<SourceItem> sources)
    {
        if (sources.Count == 0)
            return false;

        var explicitLocation = ExtractInlineLocationFromMessage(userMessage);
        if (!string.IsNullOrWhiteSpace(explicitLocation) &&
            FilterSourcesForLocalBusinessLocation(sources, explicitLocation, requireMatch: true).Count == 0)
        {
            return true;
        }

        return sources.All(source =>
            IsDirectoryAggregatorSource(source) ||
            IsLowTrustLocalBusinessSource(source, userMessage) ||
            string.IsNullOrWhiteSpace(ExtractBusinessNameFromSourceTitle(source.Title, userMessage)));
    }

    private static bool IsLowTrustLocalBusinessSource(SourceItem source, string userMessage)
    {
        if (LooksLikeChainDepartmentCandidate(source.Title ?? string.Empty, userMessage))
            return true;

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        var explicitBrand = ExtractBrandKeyword((userMessage ?? string.Empty).ToLowerInvariant()) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(explicitBrand) &&
            host.Contains(explicitBrand.Replace("'", string.Empty), StringComparison.Ordinal))
        {
            return false;
        }

        if (host.Contains("ubereats.com", StringComparison.Ordinal) ||
            host.Contains("postmates.com", StringComparison.Ordinal) ||
            host.Contains("doordash.com", StringComparison.Ordinal) ||
            host.Contains("grubhub.com", StringComparison.Ordinal) ||
            host.Contains("slice.life", StringComparison.Ordinal))
        {
            return true;
        }

        return host.Contains("walmart.com", StringComparison.Ordinal) ||
               host.Contains("albertsons.com", StringComparison.Ordinal) ||
               host.Contains("safeway.com", StringComparison.Ordinal) ||
               host.Contains("fredmeyer.com", StringComparison.Ordinal) ||
               host.Contains("kroger.com", StringComparison.Ordinal) ||
               host.Contains("target.com", StringComparison.Ordinal) ||
               host.Contains("costco.com", StringComparison.Ordinal);
    }

    private static string? TryBuildPlacesConfigErrorMessage(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var placesFailure = toolCallsMade.LastOrDefault(call =>
            call.ToolName.Contains("places", StringComparison.OrdinalIgnoreCase) &&
            (!call.Success || (call.Result ?? string.Empty).Contains("API key", StringComparison.OrdinalIgnoreCase)));
        if (placesFailure is null)
            return null;

        var result = placesFailure.Result ?? "";
        if (!result.Contains("Places provider unavailable", StringComparison.OrdinalIgnoreCase) &&
            !result.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return "Google Places provider is missing an API key. " +
               "Set ST_DEEPDIVE_PLACES_API_KEY and retry, or share a nearby neighborhood, ZIP code, or major street so I can rerun a tighter local recommendation pass.";
    }

    private static EnrichedBusiness? ParsePlaceLookupResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            json.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            json.StartsWith("Tool error:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
                return null;

            if (!root.TryGetProperty("place", out var place) ||
                place.ValueKind != JsonValueKind.Object)
                return null;

            var name = place.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var address = place.TryGetProperty("address", out var a) ? a.GetString() : null;
            var phone = place.TryGetProperty("phone", out var p) ? p.GetString() : null;
            var website = place.TryGetProperty("website", out var w) ? w.GetString() : null;
            bool? openNow = place.TryGetProperty("openNow", out var o) && o.ValueKind == JsonValueKind.True
                ? true
                : place.TryGetProperty("openNow", out var o2) && o2.ValueKind == JsonValueKind.False ? false : null;
            double? rating = place.TryGetProperty("rating", out var r) && r.TryGetDouble(out var rv)
                ? rv : null;
            int? totalRatings = place.TryGetProperty("userRatingsTotal", out var t) && t.TryGetInt32(out var tv)
                ? tv : null;

            // Extract a review snippet if available.
            string? snippet = null;
            if (place.TryGetProperty("reviews", out var reviews) &&
                reviews.ValueKind == JsonValueKind.Array)
            {
                foreach (var review in reviews.EnumerateArray())
                {
                    if (review.TryGetProperty("text", out var rt) && !string.IsNullOrWhiteSpace(rt.GetString()))
                    {
                        snippet = rt.GetString();
                        break;
                    }
                }
            }

            return new EnrichedBusiness(name!, address, phone, website, openNow, rating, totalRatings, snippet);
        }
        catch
        {
            return null;
        }
    }

    private static PlacesDiscoveryResult? ParsePlacesDiscoveryResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            json.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            json.StartsWith("Tool error:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("provider", out var providerElement) ||
                !root.TryGetProperty("results", out var resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var results = new List<PlaceDiscoveryCandidate>();
            foreach (var item in resultsElement.EnumerateArray())
            {
                var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in tagsElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        {
                            tags[property.Name] = property.Value.GetString()!;
                        }
                    }
                }

                results.Add(new PlaceDiscoveryCandidate
                {
                    Id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty,
                    Name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                    Address = item.TryGetProperty("address", out var addressElement) ? addressElement.GetString() ?? string.Empty : string.Empty,
                    Category = item.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() ?? string.Empty : string.Empty,
                    Latitude = item.TryGetProperty("latitude", out var latElement) && latElement.TryGetDouble(out var lat) ? lat : 0,
                    Longitude = item.TryGetProperty("longitude", out var lonElement) && lonElement.TryGetDouble(out var lon) ? lon : 0,
                    DistanceMeters = item.TryGetProperty("distanceMeters", out var distanceElement) && distanceElement.TryGetInt32(out var distance) ? distance : null,
                    OsmUrl = item.TryGetProperty("osmUrl", out var osmUrlElement) ? osmUrlElement.GetString() ?? string.Empty : string.Empty,
                    Tags = tags
                });
            }

            var errors = new List<string>();
            if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var errorElement in errorsElement.EnumerateArray())
                {
                    if (errorElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(errorElement.GetString()))
                    {
                        errors.Add(errorElement.GetString()!);
                    }
                }
            }

            PlacesDiscoveryCenter? center = null;
            if (root.TryGetProperty("center", out var centerElement) && centerElement.ValueKind == JsonValueKind.Object)
            {
                center = new PlacesDiscoveryCenter
                {
                    Label = centerElement.TryGetProperty("label", out var labelElement) ? labelElement.GetString() ?? string.Empty : string.Empty,
                    Latitude = centerElement.TryGetProperty("latitude", out var centerLatElement) && centerLatElement.TryGetDouble(out var centerLat) ? centerLat : 0,
                    Longitude = centerElement.TryGetProperty("longitude", out var centerLonElement) && centerLonElement.TryGetDouble(out var centerLon) ? centerLon : 0
                };
            }

            var options = new PlaceDiscoveryOptions();
            if (root.TryGetProperty("options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Object)
            {
                options = new PlaceDiscoveryOptions
                {
                    MaxResults = optionsElement.TryGetProperty("maxResults", out var maxResultsElement) && maxResultsElement.TryGetInt32(out var maxResults) ? maxResults : 10,
                    RadiusMeters = optionsElement.TryGetProperty("radiusMeters", out var radiusElement) && radiusElement.TryGetInt32(out var radiusMeters) ? radiusMeters : 4_000,
                    Locale = optionsElement.TryGetProperty("locale", out var localeElement) ? localeElement.GetString() ?? "en-US" : "en-US"
                };
            }

            var cache = new PlacesCacheMetadata();
            if (root.TryGetProperty("cache", out var cacheElement) && cacheElement.ValueKind == JsonValueKind.Object)
            {
                cache = new PlacesCacheMetadata
                {
                    Hit = cacheElement.TryGetProperty("hit", out var hitElement) && hitElement.ValueKind == JsonValueKind.True,
                    AgeSeconds = cacheElement.TryGetProperty("ageSeconds", out var ageElement) && ageElement.TryGetInt32(out var ageSeconds) ? ageSeconds : 0
                };
            }

            return new PlacesDiscoveryResult
            {
                Provider = providerElement.GetString() ?? string.Empty,
                Query = root.TryGetProperty("query", out var queryElement) ? queryElement.GetString() ?? string.Empty : string.Empty,
                UserLocationHint = root.TryGetProperty("userLocationHint", out var userLocationElement) ? userLocationElement.GetString() ?? string.Empty : string.Empty,
                ResolvedLocation = root.TryGetProperty("resolvedLocation", out var resolvedElement) ? resolvedElement.GetString() ?? string.Empty : string.Empty,
                Center = center,
                Options = options,
                Results = results,
                Errors = errors,
                Cache = cache
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<SourceItem> BuildSourceItemsFromPlacesDiscovery(
        PlacesDiscoveryResult discovery,
        string? preferredUnits)
    {
        return discovery.Results
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .Select(candidate =>
            {
                var url = string.IsNullOrWhiteSpace(candidate.OsmUrl)
                    ? $"https://www.openstreetmap.org/?mlat={candidate.Latitude.ToString("F6", CultureInfo.InvariantCulture)}&mlon={candidate.Longitude.ToString("F6", CultureInfo.InvariantCulture)}"
                    : candidate.OsmUrl;
                return new SourceItem
                {
                    SourceId = SourceItem.ComputeSourceId(url),
                    Url = url,
                    Title = candidate.Name,
                    Domain = "openstreetmap.org",
                    Snippet = BuildOpenPlaceSnippet(candidate, preferredUnits)
                };
            })
            .ToList();
    }

    private static string BuildOpenPlaceSnippet(
        PlaceDiscoveryCandidate candidate,
        string? preferredUnits)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(candidate.Address))
            parts.Add(candidate.Address);
        if (!string.IsNullOrWhiteSpace(candidate.Category))
            parts.Add(candidate.Category);
        if (candidate.DistanceMeters.HasValue)
            parts.Add(FormatPlaceDistance(candidate.DistanceMeters.Value, preferredUnits));
        return string.Join(" · ", parts);
    }

    private static string FormatPlaceDistance(int distanceMeters, string? preferredUnits)
    {
        var normalizedUnits = NormalizePreferredUnits(preferredUnits);

        if (normalizedUnits == "metric")
        {
            if (distanceMeters < 1_000)
                return $"{distanceMeters} m away";

            var kilometers = distanceMeters / 1_000.0;
            return kilometers >= 10
                ? $"{Math.Round(kilometers):0} km away"
                : $"{kilometers:F1} km away";
        }

        var miles = distanceMeters / 1609.344;
        return miles >= 10
            ? $"{Math.Round(miles):0} mi away"
            : $"{miles:F1} mi away";
    }

    private AgentResponse BuildEnrichedLocalBusinessResponse(
        string userMessage,
        IReadOnlyList<EnrichedBusiness> businesses,
        string? locationContext,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        bool includesSupplementalSpots = false,
        IReadOnlyList<SourceItem>? sources = null)
    {
        var businessLabel = GetRequestedLocalBusinessLabel(userMessage);
        var locText = string.IsNullOrWhiteSpace(locationContext)
            ? " nearby"
            : $" nearby in {locationContext}";

        Session.LastWasLocalBusinessDiscovery = true;
        Session.RecordLocalBusinessCandidates(
            businessLabel,
            businesses
                .Where(business => !string.IsNullOrWhiteSpace(business.Name))
                .Select(business => new SourceItem
                {
                    SourceId = SourceItem.ComputeSourceId($"local-business::{business.Name}"),
                    Url = string.IsNullOrWhiteSpace(business.Website)
                        ? $"about:local-business/{Uri.EscapeDataString(business.Name)}"
                        : business.Website!,
                    Title = business.Name,
                    Domain = string.IsNullOrWhiteSpace(business.Website) ? "local-business" : "website",
                    Snippet = business.Address ?? string.Empty
                })
                .ToList());

        var sb = new StringBuilder();
        if (includesSupplementalSpots)
        {
            sb.Append($"Here are {businesses.Count} options I found{locText} (including nearby spots beyond strict {businessLabel}):");
        }
        else
        {
            sb.Append(businesses.Count == 1
                ? $"Here's a {SingularizeBusinessLabel(businessLabel)} I found{locText}:"
                : $"Here are {businesses.Count} {businessLabel} I found{locText}:");
        }
        sb.AppendLine();
        sb.AppendLine();

        foreach (var biz in businesses)
        {
            sb.Append("- **");
            sb.Append(biz.Name);
            sb.Append("**");

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(biz.Address))
                details.Add(biz.Address);
            if (biz.Rating.HasValue)
            {
                var ratingText = biz.TotalRatings.HasValue
                    ? $"{biz.Rating:F1}\u2605 ({biz.TotalRatings:N0} reviews)"
                    : $"{biz.Rating:F1}\u2605";
                details.Add(ratingText);
            }
            if (biz.OpenNow.HasValue)
                details.Add(biz.OpenNow.Value ? "Open now" : "Closed now");

            if (details.Count > 0)
            {
                sb.Append(" — ");
                sb.Append(string.Join(" · ", details));
            }

            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(biz.Snippet))
            {
                sb.Append("  _\"");
                sb.Append(TrimSentence(biz.Snippet, 120));
                sb.Append("\"_");
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        if (includesSupplementalSpots)
        {
            sb.Append($"If you want, I can narrow this down to strict {businessLabel} only, or pull more details on any one of these spots.");
        }
        else
        {
            sb.Append("If you want, I can bring up more info on any one of these ");
            sb.Append(businessLabel);
            sb.Append('.');
        }

        return new AgentResponse
        {
            Text = sb.ToString(),
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0,
            Sources = ToAgentSources(sources)
        };
    }

    private static IReadOnlyList<AgentSource> ToAgentSources(IReadOnlyList<SourceItem>? sources)
    {
        if (sources is null || sources.Count == 0)
            return [];

        return sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Url))
            .Select(source => new AgentSource
            {
                Url = source.Url,
                Title = string.IsNullOrWhiteSpace(source.Title) ? null : source.Title,
                Domain = string.IsNullOrWhiteSpace(source.Domain) ? null : source.Domain,
                Excerpt = string.IsNullOrWhiteSpace(source.Snippet) ? null : source.Snippet
            })
            .ToList();
    }

    /// <summary>
    /// Fallback when we extracted business names but places_lookup is unavailable.
    /// Presents a clean list of just the names without article fluff.
    /// </summary>
    private AgentResponse BuildCleanedLocalBusinessResponse(
        string userMessage,
        IReadOnlyList<string> businessNames,
        string? locationContext,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var businessLabel = GetRequestedLocalBusinessLabel(userMessage);
        var locText = string.IsNullOrWhiteSpace(locationContext)
            ? " nearby"
            : $" nearby in {locationContext}";

        var names = businessNames.Take(LocalBusinessTargetResults).ToList();
        Session.LastWasLocalBusinessDiscovery = true;
        Session.RecordLocalBusinessCandidates(
            businessLabel,
            names
                .Select(name => new SourceItem
                {
                    SourceId = SourceItem.ComputeSourceId($"local-business::{name}"),
                    Url = $"about:local-business/{Uri.EscapeDataString(name)}",
                    Title = name,
                    Domain = "local-business"
                })
                .ToList());

        var detailByName = ExtractLocalBusinessDirectoryDetails(names, toolCallsMade);
        var sb = new StringBuilder();
        sb.Append(names.Count == 1
            ? $"Here's a {SingularizeBusinessLabel(businessLabel)}{locText} that came up:"
            : $"Here are {names.Count} {businessLabel}{locText} that came up:");

        if (TryBuildLocalBusinessDirectoryEvidenceNote(toolCallsMade, locationContext) is { Length: > 0 } evidenceNote)
            sb.Append(evidenceNote);

        sb.AppendLine();
        sb.AppendLine();

        foreach (var name in names)
        {
            sb.Append("- **");
            sb.Append(name);
            sb.Append("**");

            if (detailByName.TryGetValue(name, out var detail) && !string.IsNullOrWhiteSpace(detail))
            {
                sb.Append(" — ");
                sb.Append(detail);
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

    private static Dictionary<string, string> ExtractLocalBusinessDirectoryDetails(
        IReadOnlyList<string> businessNames,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var detailByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (businessNames.Count == 0 || toolCallsMade.Count == 0)
            return detailByName;

        var browserCalls = toolCallsMade
            .Where(call => call.Success &&
                (call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
                 call.ToolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(call.Result))
            .ToList();
        if (browserCalls.Count == 0)
            return detailByName;

        foreach (var name in businessNames)
        {
            var matchingChunks = browserCalls
                .Where(call => call.Result.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                               call.Arguments.Contains(name, StringComparison.OrdinalIgnoreCase))
                .Select(call => call.Result)
                .ToList();
            if (matchingChunks.Count == 0)
                continue;

            var extraction = DeepDiveWebExtractor.Extract(matchingChunks);
            var detail = NormalizeLocalBusinessDirectoryDetail(extraction.Address);
            if (!string.IsNullOrWhiteSpace(detail))
                detailByName[name] = detail;
        }

        return detailByName;
    }

    private static string? NormalizeLocalBusinessDirectoryDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        return normalized.Length <= 120
            ? normalized
            : normalized[..117] + "...";
    }

    private static string? TryBuildLocalBusinessDirectoryEvidenceNote(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string? locationContext)
    {
        if (toolCallsMade.Count == 0)
            return null;

        var hosts = new List<string>();
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? yearToken = null;
        var location = BuildLocalNewsLocationTokens(locationContext);

        foreach (var call in toolCallsMade)
        {
            if (!call.Success ||
                (!call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
                 !call.ToolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (location is not null &&
                !ContentMatchesLocalBusinessLocation(call.Result ?? string.Empty, location))
            {
                continue;
            }

            yearToken ??= Regex.Match(call.Result ?? string.Empty, @"\b20\d{2}\b", RegexOptions.CultureInvariant) is { Success: true } match
                ? match.Value
                : null;

            var host = TryExtractLocalBusinessDirectoryHost(call.Arguments);
            if (!string.IsNullOrWhiteSpace(host) && seenHosts.Add(host))
                hosts.Add(host);
        }

        if (hosts.Count == 0 && string.IsNullOrWhiteSpace(yearToken))
            return null;

        string sourceText;
        if (hosts.Count == 0)
        {
            sourceText = "local directory pages I checked";
        }
        else if (hosts.Count == 1)
        {
            sourceText = $"{hosts[0]} directory page";
        }
        else if (hosts.Count == 2)
        {
            sourceText = $"directory pages on {hosts[0]} and {hosts[1]}";
        }
        else
        {
            sourceText = $"directory pages on {string.Join(", ", hosts.Take(hosts.Count - 1))}, and {hosts[^1]}";
        }

        if (!string.IsNullOrWhiteSpace(yearToken))
        {
            return hosts.Count <= 1
                ? $" based on a {yearToken} {sourceText}"
                : $" based on {yearToken} {sourceText}";
        }

        return hosts.Count == 1
            ? $" based on the {sourceText} I checked"
            : $" based on the {sourceText}";
    }

    private static string? TryExtractLocalBusinessDirectoryHost(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty("url", out var urlElement) &&
                urlElement.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri))
            {
                return NormalizeLocalBusinessDirectoryHost(uri.Host);
            }
        }
        catch
        {
        }

        var match = Regex.Match(arguments, @"https?://([^/""\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? NormalizeLocalBusinessDirectoryHost(match.Groups[1].Value) : null;
    }

    private static string NormalizeLocalBusinessDirectoryHost(string host)
    {
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return host[4..];

        return host;
    }
}
