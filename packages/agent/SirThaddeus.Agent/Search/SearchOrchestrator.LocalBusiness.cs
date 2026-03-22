using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;
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
            return BuildNoResultsResponse(userMessage, toolCallsMade);

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
                $"Web search returned no usable live local news results for {locationHint} right now. " +
                "Try asking for state news, naming a local outlet, or narrowing it to a topic like schools, crime, politics, or weather.",
            true =>
                "Web search returned no usable live local news results for that request right now. " +
                "Try including a city, naming a local outlet, or setting your location in Settings and trying again.",
            _ =>
                "Web search returned no usable live news results for that request right now. " +
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
            lower.Contains("bank", StringComparison.Ordinal) ||
            lower.Contains("banks", StringComparison.Ordinal) ||
            lower.Contains("park", StringComparison.Ordinal) ||
            lower.Contains("parks", StringComparison.Ordinal) ||
            lower.Contains("dentist", StringComparison.Ordinal) ||
            lower.Contains("clinic", StringComparison.Ordinal) ||
            lower.Contains("open", StringComparison.Ordinal) ||
            lower.Contains("hours", StringComparison.Ordinal);
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

            return BuildLocalBusinessDiscoveryResponse(userMessage, sources, toolCallsMade);
        }

        // ── Phase 2: Look up each candidate via places_lookup ──
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
            var supplementalNames = await FetchSupplementalLocalBusinessNamesAsync(
                userMessage,
                responseLocation,
                businesses.Select(b => b.Name),
                toolCallsMade,
                ct);

            foreach (var name in supplementalNames)
            {
                businesses.Add(new EnrichedBusiness(
                    name,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));

                if (businesses.Count >= LocalBusinessTargetResults)
                    break;
            }
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
            includesSupplementalSpots));
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

        var name = title!.Trim();

        // Strip everything after common separators (–, —, |, :) when
        // the trailing portion is a tagline or description.
        name = Regex.Replace(name, @"\s*[–—|]\s+.*$", "").Trim();
        name = Regex.Replace(name, @"\s*:\s+(?:Find|Shop|Order|Browse|Welcome|Home|About|Our).*$", "", RegexOptions.IgnoreCase).Trim();

        // Strip "(City, ST)" or location parenthetical.
        name = Regex.Replace(name, @"\s*\(.*\)\s*$", "").Trim();

        // Strip trailing "- <anything>" — covers site names, city names,
        // directory brands ("- The Real Yellow Pages", "- MapQuest", etc.).
        name = Regex.Replace(name, @"\s+-\s+.+$", "").Trim();

        // If what's left is too short or is a generic directory phrase, skip.
        if (name.Length < 3 || name.Length > 60)
            return null;
        if (IsGenericNonBusinessName(name))
            return null;

        // Skip if the name starts with "r/" (Reddit subreddit).
        if (name.StartsWith("r/", StringComparison.Ordinal))
            return null;

        // Skip titles that are clearly aggregator-style ("Best X in Y", "Top 10 X").
        if (Regex.IsMatch(name, @"^(?:Best|Top)\s+\d*\s*", RegexOptions.IgnoreCase))
            return null;

        // Skip if the name matches or starts with the business category label
        // (e.g. "Bakeries", "Bakeries in Olympia").
        var label = GetRequestedLocalBusinessLabel(userMessage);
        if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase))
            return null;
        var singular = SingularizeBusinessLabel(label);
        if (Regex.IsMatch(name, $@"^{Regex.Escape(label)}\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, $@"^{Regex.Escape(singular)}\b", RegexOptions.IgnoreCase))
            return null;

        return name;
    }

    private async Task<string?> FetchSingleUrlAsync(
        string url,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var args = JsonSerializer.Serialize(new { url });
        string? content = null;
        var resolvedToolName = BrowseToolName;

        try
        {
            content = await _mcp.CallToolAsync(BrowseToolName, args, ct);
        }
        catch
        {
            try
            {
                resolvedToolName = BrowseToolNameAlt;
                content = await _mcp.CallToolAsync(BrowseToolNameAlt, args, ct);
            }
            catch (Exception ex)
            {
                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName  = resolvedToolName,
                    Arguments = args,
                    Result    = $"Error: {ex.Message}",
                    Success   = false
                });
                return null;
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = resolvedToolName,
            Arguments = args,
            Result    = content!.Length > 200 ? content[..200] + "…" : content,
            Success   = true
        });

        if (content!.Length > MaxArticleChars)
            content = content[..MaxArticleChars];

        return content;
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
        @"^([A-Z][A-Za-z''&\-]+(?:\s+[A-Za-z''&\-]+){0,5})\s*$",
        RegexOptions.Compiled);

    // "Our current favorites are: 1: Left Bank Pastry, 2: Gotti Sweets..."
    private static readonly Regex InlineNumberedRegex = new(
        @"\d+:\s*([A-Z][A-Za-z''&\s\-]+?)(?:,\s*\d+:|$)",
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
        var cleaned = raw.Trim().TrimEnd('.', ':', '-', '–', '—');
        cleaned = Regex.Replace(cleaned, @"\s*[-–—|]\s+.*$", "").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+\d+(\.\d+)?\s*stars?$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s*\(.*\)\s*$", "").Trim();
        return cleaned;
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
        if (Regex.IsMatch(lower, @"^(?:where|how)\s+to\s+(?:get|find)\b", RegexOptions.IgnoreCase))
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

        try
        {
            result = await _mcp.CallToolAsync(PlacesLookupToolName, args, ct);
        }
        catch
        {
            try
            {
                resolvedToolName = PlacesLookupToolNameAlt;
                result = await _mcp.CallToolAsync(PlacesLookupToolNameAlt, args, ct);
            }
            catch (Exception ex)
            {
                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName  = resolvedToolName,
                    Arguments = args,
                    Result    = $"Error: {ex.Message}",
                    Success   = false
                });
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
        CancellationToken ct)
    {
        var location = ResolveLocalBusinessLocationContext(userMessage);
        var label = GetRequestedLocalBusinessLabel(userMessage);
        var singular = SingularizeBusinessLabel(label);

        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(location))
        {
            queries.Add($"{label} near {location}");
            queries.Add($"{singular} near {location}");
            queries.Add($"{label} {location}");
        }
        else
        {
            queries.Add($"{label} near me");
            queries.Add($"{singular} near me");
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
            return null;

        if (enriched.Count < LocalBusinessMinimumDisplayResults)
        {
            var supplementalNames = await FetchSupplementalLocalBusinessNamesAsync(
                userMessage,
                location,
                enriched.Select(b => b.Name),
                toolCallsMade,
                ct);

            foreach (var name in supplementalNames)
            {
                enriched.Add(new EnrichedBusiness(
                    name,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));

                if (enriched.Count >= LocalBusinessTargetResults)
                    break;
            }
        }

        var includesSupplementalSpots = enriched.Any(biz =>
            string.IsNullOrWhiteSpace(biz.Address) &&
            !string.IsNullOrWhiteSpace(biz.Name));

        return BuildEnrichedLocalBusinessResponse(userMessage, enriched, location, toolCallsMade, includesSupplementalSpots);
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
        bool includesSupplementalSpots = false)
    {
        var businessLabel = GetRequestedLocalBusinessLabel(userMessage);
        var locText = string.IsNullOrWhiteSpace(locationContext)
            ? " nearby"
            : $" nearby in {locationContext}";

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
            LlmRoundTrips = 0
        };
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
        var sb = new StringBuilder();
        sb.Append(names.Count == 1
            ? $"Here's a {SingularizeBusinessLabel(businessLabel)}{locText} that came up:"
            : $"Here are {names.Count} {businessLabel}{locText} that came up:");
        sb.AppendLine();
        sb.AppendLine();

        foreach (var name in names)
        {
            sb.Append("- **");
            sb.Append(name);
            sb.Append("**");
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
}
