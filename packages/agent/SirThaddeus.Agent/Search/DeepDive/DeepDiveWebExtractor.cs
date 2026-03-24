using System.Text.RegularExpressions;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Search.DeepDive;

/// <summary>
/// Deterministic extraction of structured business signals from raw web
/// content chunks (snippets, page text, search result excerpts).
///
/// This is the fallback intelligence layer when Places API is unavailable.
/// Every regex is bounded and compiled — no backtracking traps.
/// </summary>
public static class DeepDiveWebExtractor
{
    // ── Phone ──────────────────────────────────────────────────────
    // Matches US/CA formats: (208) 356-1234, 208-356-1234, +1 208 356 1234
    private static readonly Regex PhoneRegex = new(
        @"(?:\+?1[ .-]?)?\(?\d{3}\)?[ .\-]\d{3}[ .\-]\d{4}",
        RegexOptions.Compiled);

    // ── Address ────────────────────────────────────────────────────
    // Matches common US address: number + street + city, ST ZIP
    private static readonly Regex AddressRegex = new(
        @"\d{1,6}\s+[\w\s.]+(?:St|Street|Ave|Avenue|Blvd|Boulevard|Rd|Road|Dr|Drive|Ln|Lane|Way|Ct|Court|Pl|Place|Pkwy|Parkway|Cir|Circle|Hwy|Highway)\.?\s*,?\s*[\w\s]+,\s*[A-Z]{2}\s+\d{5}(?:-\d{4})?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Rating ─────────────────────────────────────────────────────
    // "4.5 stars", "rated 4.7/5", "4.7 out of 5", "rating: 4.5"
    private static readonly Regex RatingRegex = new(
        @"(?:rat(?:ed|ing)[:\s]+)?(?<score>[1-5](?:\.\d)?)\s*(?:\/\s*5|out\s+of\s+5|stars?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Review count ───────────────────────────────────────────────
    // "300 reviews", "1,234 ratings", "(52 reviews)"
    private static readonly Regex ReviewCountRegex = new(
        @"(?<count>[\d,]+)\s+(?:reviews?|ratings?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Website URL ────────────────────────────────────────────────
    // Picks up explicit "website: url" or "visit: url" patterns
    private static readonly Regex WebsiteRegex = new(
        @"(?:website|visit|official\s+site|homepage)[:\s]+(?<url>https?://[^\s""<>]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Scans multiple text chunks (snippets, page content) and returns
    /// the best signal for each field. First match wins for single-value
    /// fields; review snippets accumulate.
    /// </summary>
    public static WebExtractionResult Extract(
        IEnumerable<string> textChunks,
        IReadOnlyList<SourceItem>? sourceItems = null)
    {
        string? phone = null;
        string? address = null;
        string? website = null;
        double? rating = null;
        int? reviewCount = null;
        string? businessName = null;
        var reviewSnippets = new List<string>();

        // Source titles are often the best place for a clean business name.
        if (sourceItems is { Count: > 0 })
        {
            businessName = InferBusinessNameFromSources(sourceItems);
            address = InferAddressFromSources(sourceItems);
        }

        foreach (var chunk in textChunks)
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            // Phone — first clean match
            phone ??= NormalizeSingleLine(TryMatchFirst(PhoneRegex, chunk));

            // Address — first clean match
            address ??= TryMatchFirst(AddressRegex, chunk);

            // Website
            if (website is null)
            {
                var webMatch = WebsiteRegex.Match(chunk);
                if (webMatch.Success)
                    website = webMatch.Groups["url"].Value.TrimEnd('.', ',', ')');
            }

            // Rating
            if (!rating.HasValue)
            {
                var ratingMatch = RatingRegex.Match(chunk);
                if (ratingMatch.Success &&
                    double.TryParse(ratingMatch.Groups["score"].Value, out var parsed) &&
                    parsed is >= 1.0 and <= 5.0)
                {
                    rating = parsed;
                }
            }

            // Review count
            if (!reviewCount.HasValue)
            {
                var countMatch = ReviewCountRegex.Match(chunk);
                if (countMatch.Success &&
                    int.TryParse(
                        countMatch.Groups["count"].Value.Replace(",", ""),
                        out var parsedCount))
                {
                    reviewCount = parsedCount;
                }
            }

            // Collect review-like sentences (short, sentiment-bearing)
            CollectReviewSnippets(chunk, reviewSnippets);
        }

        return new WebExtractionResult
        {
            BusinessName = businessName,
            Address      = address,
            Phone        = phone,
            Website      = website,
            Rating       = rating,
            ReviewCount  = reviewCount,
            ReviewSnippets = reviewSnippets.Take(5).ToList()
        };
    }

    /// <summary>
    /// Builds review card bullets from extracted web signals.
    /// Returns meaningful content instead of static placeholders.
    /// </summary>
    public static List<string> BuildReviewBullets(WebExtractionResult extraction)
    {
        var bullets = new List<string>();

        if (extraction.Rating.HasValue)
        {
            var countSuffix = extraction.ReviewCount.HasValue
                ? $" across {extraction.ReviewCount.Value:N0} reviews"
                : "";
            bullets.Add($"Rating: {extraction.Rating.Value:0.0}/5{countSuffix} (from web sources).");
        }

        if (extraction.ReviewSnippets.Count > 0)
        {
            foreach (var snippet in extraction.ReviewSnippets.Take(3))
                bullets.Add(snippet);
        }

        if (bullets.Count == 0)
        {
            bullets.Add("No review data was found in the search results.");
            bullets.Add("Check the source links below for the latest customer reviews.");
        }

        return bullets;
    }

    /// <summary>
    /// Builds summary card bullets from extracted web signals.
    /// Puts real address, phone, website data in instead of placeholders.
    /// </summary>
    public static List<string> BuildSummaryBullets(WebExtractionResult extraction)
    {
        var bullets = new List<string>();

        if (!string.IsNullOrWhiteSpace(extraction.Address))
            bullets.Add($"Address: {extraction.Address}");

        if (!string.IsNullOrWhiteSpace(extraction.Phone))
            bullets.Add($"Phone: {extraction.Phone}");

        if (!string.IsNullOrWhiteSpace(extraction.Website))
            bullets.Add($"Website: {extraction.Website}");

        if (extraction.Rating.HasValue)
        {
            var countPart = extraction.ReviewCount.HasValue
                ? $" from {extraction.ReviewCount.Value:N0} reviews"
                : "";
            bullets.Add($"Reputation: {extraction.Rating.Value:0.0}/5{countPart}.");
        }

        if (bullets.Count == 0)
        {
            bullets.Add("Limited business details were found in search results.");
            bullets.Add("Use the source links below for the most current information.");
        }

        return bullets;
    }

    // ── Private helpers ────────────────────────────────────────────

    /// <summary>
    /// Infers the business name from source titles. Picks the shortest
    /// title fragment that appears before common separators like " - ",
    /// " | ", or " :: ". Business names are usually the leading segment.
    /// </summary>
    private static string? InferBusinessNameFromSources(IReadOnlyList<SourceItem> sources)
    {
        var candidates = new List<string>();

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Title))
                continue;

            // Titles often look like "McDonald's - 700 SW 5th Ave | Portland"
            var separators = new[] { " - ", " | ", " :: ", " — ", " – " };
            var title = source.Title;

            foreach (var sep in separators)
            {
                var idx = title.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 2)
                {
                    title = title[..idx].Trim();
                    break;
                }
            }

            if (title.Length >= 3 && title.Length <= 80)
                candidates.Add(title);
        }

        if (candidates.Count == 0)
            return null;

        // The name that appears most frequently across sources is most likely correct.
        return candidates
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Length)
            .First().Key;
    }

    private static string? InferAddressFromSources(IReadOnlyList<SourceItem> sources)
    {
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Title))
                continue;

            var title = source.Title.Trim();
            var normalized = title;

            var separators = new[] { " | ", " :: ", " — ", " – ", " - " };
            foreach (var sep in separators)
            {
                var idx = normalized.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0)
                {
                    normalized = normalized[..idx].Trim();
                    break;
                }
            }

            var firstComma = normalized.IndexOf(',');
            if (firstComma >= 0 && firstComma + 1 < normalized.Length)
                normalized = normalized[(firstComma + 1)..].Trim();

            normalized = Regex.Replace(normalized, @",\s*us$", "", RegexOptions.IgnoreCase).Trim();

            var match = Regex.Match(
                normalized,
                @"\d{1,6}\s+[^,]+(?:,\s*[^,]+){1,2},\s*[A-Z]{2}\s+\d{5}(?:-\d{4})?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var candidate = Regex.Replace(match.Value, @"\s+", " ").Trim().TrimEnd(',', '.');
            if (candidate.Length > 0)
                return candidate;
        }

        return null;
    }

    private static string? TryMatchFirst(Regex regex, string input)
    {
        var match = regex.Match(input);
        return match.Success ? match.Value.Trim() : null;
    }

    private static string? NormalizeSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// Extracts short sentences that look like review snippets.
    /// Filters for sentiment-bearing text between 20-200 characters.
    /// </summary>
    private static void CollectReviewSnippets(string chunk, List<string> collector)
    {
        if (collector.Count >= 5)
            return;

        var sentinels = new[]
        {
            "great", "excellent", "amazing", "terrible", "horrible",
            "friendly", "rude", "fast", "slow", "clean", "dirty",
            "love", "hate", "best", "worst", "recommend", "avoid",
            "worth", "overpriced", "cheap", "good", "bad"
        };

        // Split on sentence-ending punctuation
        var sentences = chunk.Split(
            ['.', '!', '?'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var sentence in sentences)
        {
            if (sentence.Length < 20 || sentence.Length > 200)
                continue;

            if (collector.Count >= 5)
                break;

            var hasSentiment = sentinels.Any(s =>
                sentence.Contains(s, StringComparison.OrdinalIgnoreCase));

            if (hasSentiment)
            {
                // Clean up and quote it
                var cleaned = sentence.Trim();
                if (!cleaned.EndsWith('.'))
                    cleaned += ".";
                collector.Add($"\"{cleaned}\"");
            }
        }
    }
}

/// <summary>
/// Aggregated extraction result from web content scanning.
/// All fields are nullable — absence means "not found."
/// </summary>
public sealed record WebExtractionResult
{
    public string?  BusinessName   { get; init; }
    public string?  Address        { get; init; }
    public string?  Phone          { get; init; }
    public string?  Website        { get; init; }
    public double?  Rating         { get; init; }
    public int?     ReviewCount    { get; init; }
    public IReadOnlyList<string> ReviewSnippets { get; init; } = [];
}
