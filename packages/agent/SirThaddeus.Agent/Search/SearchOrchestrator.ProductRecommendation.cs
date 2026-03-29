using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Search;

public sealed partial class SearchOrchestrator
{
    private const int ProductPlanMaxQueries = 4;
    private const int ProductHydrationCount = 2;
    private const int ProductDefaultCount = 3;

    private static readonly string[] ProductRetailerDomains =
    [
        "amazon.com",
        "walmart.com",
        "ebay.com",
        "etsy.com"
    ];

    private static readonly Regex ProductRatingRegex = new(
        @"\b([1-5](?:\.\d)?)\s*(?:/5|out of 5|stars?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProductReviewCountRegex = new(
        @"\b(\d{1,3}(?:,\d{3})*|\d+(?:\.\d+)?k)\s+(?:reviews?|ratings?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProductPriceRegex = new(
        @"\$(\d{1,4}(?:\.\d{2})?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum ProductResponseMode
    {
        FullRecommendation,
        QualifiedShortlist,
        HonestDegraded
    }

    private sealed class ProductCandidate
    {
        public required string Title { get; init; }
        public required string Url { get; init; }
        public required string Domain { get; init; }
        public required string Retailer { get; init; }
        public required string Snippet { get; init; }
        public double Score { get; init; }
        public string? RatingText { get; set; }
        public string? ReviewCountText { get; set; }
        public string? PriceText { get; set; }
        public ExtractionQuality? HydrationQuality { get; set; }
    }

    private sealed record ProductConstraints(
        string ProductType,
        IReadOnlyList<string> RetailerDomains,
        int RequestedCount);

    private async Task<AgentResponse> ExecuteProductRecommendationAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        // Skip entity resolution for product recommendations — the product
        // name is already in the user message and the canonicalization web
        // search wastes a precious tool-call / time slot on non-product results.
        var constraints = BuildProductConstraints(userMessage ?? string.Empty, entity: null);
        var plan = BuildProductSearchPlan(constraints);

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "PRODUCT_RECOMMENDATION_START",
            Result = constraints.ProductType,
            Details = new Dictionary<string, object>
            {
                ["retailers"] = string.Join(", ", constraints.RetailerDomains),
                ["query_count"] = plan.Count
            }
        });

        var allSources = new List<SourceItem>();
        var sawStructuredFailure = false;

        foreach (var plannedQuery in plan)
        {
            var toolResult = await CallWebSearchAsync(
                plannedQuery,
                "any",
                toolCallsMade,
                ct,
                originalUserMessage: userMessage,
                maxResults: 8,
                categories: "general");

            if (WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is not null)
            {
                sawStructuredFailure = true;
                continue;
            }

            allSources.AddRange(ParseSourcesFromToolResult(toolResult));
        }

        var filteredSources = FilterProductSources(allSources, constraints.ProductType);
        var rankedCandidates = RankProductCandidates(filteredSources, constraints);
        await HydrateTopCandidatesAsync(rankedCandidates, toolCallsMade, ct);

        var now = DateTimeOffset.UtcNow;
        Session.RecordSearchResults(
            SearchMode.ProductRecommendation,
            string.Join(" | ", plan),
            "any",
            filteredSources.Take(20).ToList(),
            now);
        Session.LastWasLocalBusinessDiscovery = false;
        Session.ClearLocalBusinessCandidates();

        var mode = SelectProductResponseMode(rankedCandidates, sawStructuredFailure);
        var responseText = BuildProductResponseText(rankedCandidates, constraints, mode);

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "PRODUCT_RECOMMENDATION_DONE",
            Result = mode.ToString(),
            Details = new Dictionary<string, object>
            {
                ["source_count"] = filteredSources.Count,
                ["candidate_count"] = rankedCandidates.Count,
                ["saw_structured_failure"] = sawStructuredFailure
            }
        });

        return new AgentResponse
        {
            Text = responseText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = 0
        };
    }

    private static ProductConstraints BuildProductConstraints(
        string userMessage,
        EntityResolver.ResolvedEntity? entity)
    {
        var lower = userMessage.Trim().ToLowerInvariant();

        var retailers = new List<string>();
        foreach (var domain in ProductRetailerDomains)
        {
            var token = domain.Replace(".com", string.Empty, StringComparison.Ordinal);
            if (lower.Contains(token, StringComparison.Ordinal))
                retailers.Add(domain);
        }

        if (retailers.Count == 0)
            retailers.AddRange(ProductRetailerDomains);

        var requestedCount = ProductDefaultCount;
        var countMatch = Regex.Match(lower, @"\b(?:top|best|recommend)\s+(\d)\b", RegexOptions.IgnoreCase);
        if (countMatch.Success && int.TryParse(countMatch.Groups[1].Value, out var parsedCount))
            requestedCount = Math.Clamp(parsedCount, 1, 5);

        var productType = !string.IsNullOrWhiteSpace(entity?.CanonicalName)
            ? entity.CanonicalName.Trim()
            : ExtractProductTypeFromMessage(userMessage);

        if (string.IsNullOrWhiteSpace(productType))
            productType = "product";

        return new ProductConstraints(productType, retailers.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), requestedCount);
    }

    private static string ExtractProductTypeFromMessage(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return "product";

        var extracted = userMessage;
        extracted = Regex.Replace(extracted, @"\b(can\s+you|could\s+you|please|recommend|suggest|find|show\s+me|best|good)\b", " ", RegexOptions.IgnoreCase);
        extracted = Regex.Replace(extracted, @"\b(on|at)\s+(amazon(?:\.com)?|walmart(?:\.com)?|ebay|etsy)\b", " ", RegexOptions.IgnoreCase);
        extracted = Regex.Replace(extracted, @"\?", " ", RegexOptions.IgnoreCase);
        extracted = Regex.Replace(extracted, @"\b(a|an|the)\b", " ", RegexOptions.IgnoreCase);
        extracted = Regex.Replace(extracted, @"\s+", " ").Trim();

        return extracted;
    }

    private static IReadOnlyList<string> BuildProductSearchPlan(ProductConstraints constraints)
    {
        var queries = new List<string>();

        foreach (var retailer in constraints.RetailerDomains)
        {
            queries.Add($"site:{retailer} {constraints.ProductType} reviews ratings");
        }

        queries.Add($"best {constraints.ProductType} reviews ratings");
        queries.Add($"{constraints.ProductType} buying guide");

        return queries
            .Select(q => q.Trim())
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ProductPlanMaxQueries)
            .ToList();
    }

    private static IReadOnlyList<SourceItem> FilterProductSources(
        IReadOnlyList<SourceItem> sources,
        string productType)
    {
        if (sources.Count == 0)
            return [];

        var tokens = Tokenize(productType);

        return sources
            .Where(source =>
            {
                var combined = $"{source.Title} {source.Snippet}";
                var lower = combined.ToLowerInvariant();

                if (source.Url.Contains("wikipedia", StringComparison.OrdinalIgnoreCase) ||
                    source.Url.Contains("dictionary", StringComparison.OrdinalIgnoreCase) ||
                    source.Url.Contains("thesaurus", StringComparison.OrdinalIgnoreCase) ||
                    source.Url.Contains("merriam-webster", StringComparison.OrdinalIgnoreCase) ||
                    source.Url.Contains("grammar", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var hasRetailerAnchor = ProductRetailerDomains.Any(domain =>
                    source.Domain.Contains(domain, StringComparison.OrdinalIgnoreCase) ||
                    source.Url.Contains(domain, StringComparison.OrdinalIgnoreCase));

                var hasProductAnchor = tokens.Count == 0 || tokens.Any(token =>
                    lower.Contains(token, StringComparison.OrdinalIgnoreCase));

                return hasRetailerAnchor || hasProductAnchor;
            })
            .GroupBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static List<ProductCandidate> RankProductCandidates(
        IReadOnlyList<SourceItem> sources,
        ProductConstraints constraints)
    {
        var candidates = new List<ProductCandidate>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var retailer = InferRetailerLabel(source);
            var combined = $"{source.Title} {source.Snippet}";

            var rating = TryMatch(ProductRatingRegex, combined);
            var reviewCount = TryMatch(ProductReviewCountRegex, combined);
            var price = TryMatch(ProductPriceRegex, combined, "$1");

            var score = ScoreProductCandidate(source, constraints.ProductType, constraints.RetailerDomains, rating, reviewCount, price);
            if (score <= 0.0)
                continue;

            var dedupeKey = BuildCandidateKey(source.Title, retailer);
            if (!seenKeys.Add(dedupeKey))
                continue;

            candidates.Add(new ProductCandidate
            {
                Title = StripTitleSuffix(source.Title),
                Url = source.Url,
                Domain = source.Domain,
                Retailer = retailer,
                Snippet = source.Snippet,
                Score = score,
                RatingText = rating,
                ReviewCountText = reviewCount,
                PriceText = price
            });
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .Take(5)
            .ToList();
    }

    private async Task HydrateTopCandidatesAsync(
        List<ProductCandidate> candidates,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return;

        var deepDive = new ArticleDeepDiveExecutor(_mcp, _audit);

        foreach (var candidate in candidates.Take(ProductHydrationCount))
        {
            var articleRef = new StoryReference
            {
                StoryId = SourceItem.ComputeSourceId(candidate.Url),
                CanonicalUrl = candidate.Url,
                Headline = candidate.Title,
                Source = candidate.Retailer,
                PublishedAt = null,
                ClusterId = null
            };

            var deepDiveResult = await deepDive.ExecuteAsync(
                new DeepDiveArticleRequest
                {
                    ArticleRef = articleRef,
                    UserMessage = "hydrate product listing"
                },
                toolCallsMade,
                ct);

            candidate.HydrationQuality = deepDiveResult.ExtractionQuality;

            var hydrateText = string.Join(" ", deepDiveResult.KeyPoints) + " " + (deepDiveResult.Summary ?? string.Empty);
            if (string.IsNullOrWhiteSpace(candidate.RatingText))
                candidate.RatingText = TryMatch(ProductRatingRegex, hydrateText);
            if (string.IsNullOrWhiteSpace(candidate.ReviewCountText))
                candidate.ReviewCountText = TryMatch(ProductReviewCountRegex, hydrateText);
            if (string.IsNullOrWhiteSpace(candidate.PriceText))
                candidate.PriceText = TryMatch(ProductPriceRegex, hydrateText, "$1");
        }
    }

    private static ProductResponseMode SelectProductResponseMode(
        IReadOnlyList<ProductCandidate> candidates,
        bool sawStructuredFailure)
    {
        if (candidates.Count == 0)
            return ProductResponseMode.HonestDegraded;

        var hydratedStrong = candidates.Count(candidate =>
            candidate.HydrationQuality is ExtractionQuality.Full or ExtractionQuality.MetadataOnly);

        if (candidates.Count >= 3 && hydratedStrong >= 2)
            return ProductResponseMode.FullRecommendation;

        if (candidates.Count >= 2)
            return ProductResponseMode.QualifiedShortlist;

        return sawStructuredFailure
            ? ProductResponseMode.HonestDegraded
            : ProductResponseMode.QualifiedShortlist;
    }

    private static string BuildProductResponseText(
        IReadOnlyList<ProductCandidate> candidates,
        ProductConstraints constraints,
        ProductResponseMode mode)
    {
        if (mode == ProductResponseMode.HonestDegraded)
        {
            if (candidates.Count == 0)
            {
                return "Reliable retailer listing evidence is still missing for this product request. " +
                       "Direct listing pages (not generic editorial snippets) are needed before naming a defensible recommendation. " +
                       $"I can retry using retailer-focused queries across {string.Join(", ", constraints.RetailerDomains)} and return a shortlist once I have concrete candidates.";
            }

            var fallbackNames = string.Join("; ", candidates.Take(2).Select(candidate => candidate.Title));
            return "A single best pick is not established yet, but these plausible candidates are a useful starting point: " +
                   fallbackNames + ". " +
                   "I still need stronger listing evidence (ratings/review counts/availability) to promote one as the top recommendation.";
        }

        var sb = new StringBuilder();
        sb.Append(mode == ProductResponseMode.FullRecommendation
            ? $"Bottom line: based on the evidence I could verify, these are the strongest {constraints.ProductType} options right now."
            : $"A single best {constraints.ProductType} winner is not yet established, but here is a qualified shortlist from the evidence I found.");

        var limit = Math.Min(constraints.RequestedCount, candidates.Count);
        for (var i = 0; i < limit; i++)
        {
            var candidate = candidates[i];
            var evidence = BuildEvidenceClause(candidate);
            sb.AppendLine();
            sb.Append($"{i + 1}. {candidate.Title} ({candidate.Retailer})");
            if (!string.IsNullOrWhiteSpace(candidate.Url))
                sb.Append($" — {candidate.Url}");
            sb.Append($" — {evidence}");
        }

        sb.AppendLine();
        sb.Append("Caveat: treat marketplace listings as dynamic. Recheck current rating, review count, and ingredient/spec details before purchase.");

        return sb.ToString().Trim();
    }

    private static string BuildEvidenceClause(ProductCandidate candidate)
    {
        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(candidate.RatingText))
            details.Add($"rating signal: {candidate.RatingText}");
        if (!string.IsNullOrWhiteSpace(candidate.ReviewCountText))
            details.Add($"review signal: {candidate.ReviewCountText}");
        if (!string.IsNullOrWhiteSpace(candidate.PriceText))
            details.Add($"price cue: {candidate.PriceText}");

        if (candidate.HydrationQuality is ExtractionQuality.Full or ExtractionQuality.MetadataOnly)
            details.Add($"page quality: {candidate.HydrationQuality}");

        if (details.Count == 0)
            details.Add("candidate extracted from retailer-anchored search evidence");

        return string.Join(", ", details);
    }

    private static double ScoreProductCandidate(
        SourceItem source,
        string productType,
        IReadOnlyList<string> retailerDomains,
        string? rating,
        string? reviewCount,
        string? price)
    {
        var score = 0.0;

        if (retailerDomains.Any(domain =>
                source.Domain.Contains(domain, StringComparison.OrdinalIgnoreCase) ||
                source.Url.Contains(domain, StringComparison.OrdinalIgnoreCase)))
        {
            score += 3.0;
        }

        var listingSignal =
            source.Url.Contains("/dp/", StringComparison.OrdinalIgnoreCase) ||
            source.Url.Contains("/gp/product/", StringComparison.OrdinalIgnoreCase) ||
            source.Url.Contains("/ip/", StringComparison.OrdinalIgnoreCase) ||
            source.Url.Contains("/itm/", StringComparison.OrdinalIgnoreCase) ||
            source.Url.Contains("/listing/", StringComparison.OrdinalIgnoreCase);
        if (listingSignal)
            score += 1.5;

        var titleLower = source.Title.ToLowerInvariant();
        foreach (var token in Tokenize(productType))
        {
            if (titleLower.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 0.6;
        }

        if (!string.IsNullOrWhiteSpace(rating))
            score += 0.8;
        if (!string.IsNullOrWhiteSpace(reviewCount))
            score += 0.8;
        if (!string.IsNullOrWhiteSpace(price))
            score += 0.4;

        var lowValueSignal =
            source.Url.Contains("wikipedia", StringComparison.OrdinalIgnoreCase) ||
            source.Url.Contains("dictionary", StringComparison.OrdinalIgnoreCase) ||
            source.Url.Contains("thesaurus", StringComparison.OrdinalIgnoreCase) ||
            source.Title.Contains("definition", StringComparison.OrdinalIgnoreCase);
        if (lowValueSignal)
            score -= 3.0;

        return score;
    }

    private static string InferRetailerLabel(SourceItem source)
    {
        var lower = $"{source.Domain} {source.Url}".ToLowerInvariant();
        if (lower.Contains("amazon", StringComparison.Ordinal))
            return "Amazon";
        if (lower.Contains("walmart", StringComparison.Ordinal))
            return "Walmart";
        if (lower.Contains("ebay", StringComparison.Ordinal))
            return "eBay";
        if (lower.Contains("etsy", StringComparison.Ordinal))
            return "Etsy";

        return string.IsNullOrWhiteSpace(source.Domain)
            ? "Web"
            : source.Domain;
    }

    private static string BuildCandidateKey(string title, string retailer)
    {
        var normalized = Regex.Replace((title ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return $"{retailer.ToLowerInvariant()}::{normalized}";
    }

    private static string? TryMatch(Regex regex, string text, string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = regex.Match(text);
        if (!match.Success)
            return null;

        if (match.Groups.Count < 2)
            return match.Value;

        var value = match.Groups[1].Value;
        return string.IsNullOrWhiteSpace(prefix) ? value : prefix + value;
    }

    private static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .ToLowerInvariant()
            .Split([' ', '-', ',', '.', '/', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }
}
