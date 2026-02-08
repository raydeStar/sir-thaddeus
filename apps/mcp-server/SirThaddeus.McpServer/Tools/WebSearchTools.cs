using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using SirThaddeus.WebSearch;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Web Search Tool
//
// Searches the web, then auto-reads the top results and returns rich
// summaries. The LLM gets actual article content in one tool call —
// no chaining required, no "pick which one" menus.
//
// Output format:
//   - Concise text for the LLM (titles + excerpts, NO raw URLs)
//   - Instruction to summarize and synthesize
//   - <!-- SOURCES_JSON --> delimiter
//   - JSON array of structured source objects (for the UI to render
//     as rich cards with favicons, thumbnails, and clickable links)
//
// Bounds:
//   - Search timeout: 8 seconds
//   - Per-page fetch timeout: 10 seconds
//   - Max 5 pages auto-read, 3 concurrent fetches
//   - Excerpt truncated to ~500 chars per result
//   - Read-only (no side effects, safe to retry)
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class WebSearchTools
{
    private const int AutoReadCount     = 5;
    private const int ExcerptMaxChars   = 1000;
    private const int CardExcerptChars  = 250;
    private const int PageTimeoutSecs   = 10;
    internal const string SourcesDelimiter = "<!-- SOURCES_JSON -->";

    private static readonly Lazy<WebSearchRouter> Router = new(
        () => new WebSearchRouter(
            mode: Environment.GetEnvironmentVariable("WEBSEARCH_MODE") ?? "auto",
            searxngBaseUrl: Environment.GetEnvironmentVariable("WEBSEARCH_SEARXNG_URL")
                            ?? "http://localhost:8080"));

    [McpServerTool, Description(
        "Searches the web and returns rich summaries of the top results. " +
        "Automatically fetches and reads the top pages. Use this for any " +
        "question that needs current information: news, facts, prices, etc.")]
    public static async Task<string> WebSearch(
        [Description("The search query")] string query,
        [Description("Number of results to fetch, 1 to 10, default 5")] int maxResults = 5,
        [Description("Recency filter: day, week, month, or any (default)")] string recency = "any",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: Search query is required.";

        try
        {
            return await ExecuteSearchAsync(query, maxResults, recency, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return $"Error: Search for \"{query}\" was cancelled or timed out.";
        }
        catch (Exception ex)
        {
            return $"Error: WebSearch failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static async Task<string> ExecuteSearchAsync(
        string query, int maxResults, string recency, CancellationToken cancellationToken)
    {
        maxResults = Math.Clamp(maxResults, 1, 10);

        // Normalize recency to a known value
        recency = NormalizeRecency(recency);

        // Request extra results from the provider so that after domain
        // deduplication we still have enough unique sources to fill the carousel.
        var fetchCount = Math.Min(maxResults * 3, 15);

        // ── Phase 1: Search ──────────────────────────────────────────
        var searchResult = await Router.Value.SearchAsync(
            query,
            new WebSearchOptions
            {
                MaxResults = fetchCount,
                TimeoutMs  = 8_000,
                Recency    = recency
            },
            cancellationToken);

        if (searchResult.Results.Count == 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"No results found for \"{query}\".");
            sb.AppendLine($"Provider: {searchResult.Provider}");
            foreach (var err in searchResult.Errors)
                sb.AppendLine($"Warning: {err}");
            sb.AppendLine("Try a different query, or paste a URL for BrowserNavigate.");
            return sb.ToString();
        }

        // ── Phase 1.5: Deduplicate by domain ─────────────────────────
        // Prevents 3x of the same site dominating the card carousel.
        // Take only maxResults after dedup to keep output bounded.
        var dedupedResults = DeduplicateByDomain(searchResult.Results)
            .Take(maxResults)
            .ToList();

        // ── Phase 2: Auto-read top results ───────────────────────────
        var urlsToRead = dedupedResults
            .Take(AutoReadCount)
            .Select(r => r.Url)
            .Where(u => !string.IsNullOrEmpty(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var extractions = urlsToRead.Count > 0
            ? await ContentExtractor.ExtractManyAsync(
                urlsToRead,
                maxConcurrency: 3,
                perPageTimeoutSecs: PageTimeoutSecs,
                cancellationToken)
            : [];

        // ── Phase 3: Format output (text for LLM + JSON for UI) ──────
        return FormatResults(query, dedupedResults, extractions, urlsToRead);
    }

    /// <summary>
    /// Keeps at most one result per domain. First appearance wins.
    /// Prevents the carousel from showing 3x of the same website.
    /// </summary>
    private static List<SearchResult> DeduplicateByDomain(IReadOnlyList<SearchResult> results)
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<SearchResult>();

        foreach (var r in results)
        {
            var domain = ExtractDomainFromUrl(r.Url);
            if (seen.Add(domain))
                deduped.Add(r);
        }

        return deduped;
    }

    private static string ExtractDomainFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.Replace("www.", "");
        return url;
    }

    // ─────────────────────────────────────────────────────────────────
    // Output Formatting
    //
    // Two sections separated by SOURCES_JSON delimiter:
    //
    // 1. LLM TEXT — Clean, concise, no raw URLs. Just titles, sources,
    //    and article excerpts the LLM can synthesize into a summary.
    //    Ends with an instruction to summarize (not dump).
    //
    // 2. SOURCES JSON — Structured data for the UI to render as rich
    //    clickable cards. Contains URLs, favicons, thumbnails, etc.
    //    The LLM should NOT reference this section in its response.
    // ─────────────────────────────────────────────────────────────────

    private static string FormatResults(
        string query,
        IReadOnlyList<SearchResult> results,
        List<ContentExtractor.ExtractionResult> extractions,
        List<string> originalUrls)
    {
        var sb = new StringBuilder();

        // Build URL -> extraction lookup (keyed by both original and resolved URLs)
        var extractionMap = new Dictionary<string, ContentExtractor.ExtractionResult>(
            StringComparer.OrdinalIgnoreCase);

        for (var idx = 0; idx < originalUrls.Count && idx < extractions.Count; idx++)
            extractionMap[originalUrls[idx]] = extractions[idx];

        foreach (var e in extractions)
            extractionMap[e.Url] = e;

        // ── INSTRUCTIONS (kept short — small models parrot verbose rules) ─
        sb.AppendLine("Synthesize these sources into a comprehensive answer. " +
                      "Cross-reference where sources agree or differ. " +
                      "Lead with the bottom line, then provide detail. No URLs. " +
                      "ONLY state facts found in the sources below. " +
                      "If a detail is not in the sources, do NOT guess or make it up.");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var hasExtraction = extractionMap.TryGetValue(r.Url, out var ext) && ext.Succeeded;

            var title  = !string.IsNullOrWhiteSpace(r.Title)
                ? r.Title
                : (hasExtraction ? ext!.Title : "(untitled)");
            var source = hasExtraction ? ext!.Domain : r.Source;

            // No URLs, no brackets — just title, source, and excerpt
            sb.AppendLine($"{i + 1}. \"{title}\" — {source}");

            if (hasExtraction)
            {
                var excerpt = CleanExcerpt(
                    ContentExtractor.Truncate(ext!.TextContent.Trim(), ExcerptMaxChars));
                if (!string.IsNullOrWhiteSpace(excerpt))
                    sb.AppendLine($"   {excerpt}");
            }
            else if (!string.IsNullOrWhiteSpace(r.Snippet))
            {
                var excerpt = CleanExcerpt(r.Snippet);
                if (!string.IsNullOrWhiteSpace(excerpt))
                    sb.AppendLine($"   {excerpt}");
            }

            sb.AppendLine();
        }

        // ── Sources JSON Section (UI only — invisible to the user) ────
        sb.AppendLine();
        sb.AppendLine(SourcesDelimiter);

        var sources = new List<object>();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var hasExtraction = extractionMap.TryGetValue(r.Url, out var ext) && ext.Succeeded;

            var title     = !string.IsNullOrWhiteSpace(r.Title) ? r.Title : (hasExtraction ? ext!.Title : "(untitled)");
            var excerpt   = hasExtraction
                ? CleanExcerpt(ContentExtractor.Truncate(ext!.TextContent.Trim(), CardExcerptChars))
                : CleanExcerpt(r.Snippet);
            var favicon   = hasExtraction ? ext!.FaviconBase64 : null;
            var thumbnail = hasExtraction ? GetArticleThumbnail(ext!) : null;
            var url       = hasExtraction ? ext!.Url           : r.Url;
            var domain    = hasExtraction ? ext!.Domain         : r.Source;

            sources.Add(new
            {
                title,
                url,
                domain,
                excerpt   = excerpt ?? "",
                favicon   = favicon ?? "",
                thumbnail = thumbnail ?? ""
            });
        }

        sb.AppendLine(JsonSerializer.Serialize(sources, new JsonSerializerOptions
        {
            WriteIndented = false
        }));

        return sb.ToString();
    }

    /// <summary>
    /// Removes noisy boilerplate from extracted web text so the LLM gets
    /// something closer to an "article excerpt" instead of nav/ads/CTAs.
    /// </summary>
    private static string CleanExcerpt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        // Normalize newlines and split to filter line-level junk
        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // Drop obvious boilerplate / noise lines
        var filtered = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();

            // Pure URL lines
            if (lower.StartsWith("http://") || lower.StartsWith("https://") || lower.StartsWith("www."))
                continue;

            // Common CTA / nav / ad artifacts
            if (lower is "home" or "menu" or "subscribe" or "sign in" or "login" or "register")
                continue;

            if (lower.Contains("advertisement") || lower == "ad" || lower.StartsWith("ad "))
                continue;

            if (lower.Contains("cookie") && lower.Contains("privacy"))
                continue;

            if (lower.StartsWith("read full") || lower.StartsWith("learn more") || lower.StartsWith("read more"))
                continue;

            if (lower.StartsWith("not working for you") || lower.StartsWith("try again later"))
                continue;

            // Finance tickers / "TRENDING: XAU/USD ..." style junk
            if (lower.StartsWith("trending:") && line.Length < 140)
                continue;

            filtered.Add(line);
        }

        if (filtered.Count == 0)
            filtered = lines; // fallback to something rather than blank

        // Deduplicate adjacent repeats
        var deduped = new List<string>(filtered.Count);
        string? last = null;
        foreach (var line in filtered)
        {
            if (last is not null && string.Equals(last, line, StringComparison.OrdinalIgnoreCase))
                continue;
            deduped.Add(line);
            last = line;
        }

        // Join into a single paragraph-like excerpt
        var joined = string.Join(' ', deduped);
        joined = CollapseWhitespace(joined);

        // Keep excerpts tight — mirrors ExcerptMaxChars
        return joined.Length <= ExcerptMaxChars
            ? joined
            : joined[..ExcerptMaxChars] + "\u2026";
    }

    private static string CollapseWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        var prevWs = false;
        foreach (var ch in s)
        {
            var isWs = char.IsWhiteSpace(ch);
            if (isWs)
            {
                if (prevWs) continue;
                sb.Append(' ');
                prevWs = true;
            }
            else
            {
                sb.Append(ch);
                prevWs = false;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Returns the article thumbnail URL, or null if it looks like a generic
    /// brand/stock image rather than article-specific content.
    /// </summary>
    private static string? GetArticleThumbnail(ContentExtractor.ExtractionResult ext)
    {
        var url = ext.ThumbnailUrl;
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Skip thumbnails that are likely generic site branding, not article images.
        // These produce misleading cards (stock photo of a woman for a finance article, etc.)
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.ToLowerInvariant();

            // Very short paths are usually root-level brand images
            if (path.Length < 10)
                return null;

            // Common generic image path segments
            if (path.Contains("/logo") || path.Contains("/brand") ||
                path.Contains("/default") || path.Contains("/placeholder") ||
                path.Contains("/icon") || path.Contains("/social-share") ||
                path.Contains("/og-image") || path.Contains("/site-image") ||
                path.EndsWith(".svg"))
                return null;
        }

        return url;
    }

    /// <summary>
    /// Clamps the recency value to a known enum-like token.
    /// Accepts fuzzy inputs (e.g. "today" → "day") so callers
    /// don't have to be exact.
    /// </summary>
    private static string NormalizeRecency(string raw)
    {
        var r = (raw ?? "any").Trim().ToLowerInvariant();
        return r switch
        {
            "day" or "today" or "24h" or "1d"   => "day",
            "week" or "7d" or "1w"               => "week",
            "month" or "30d" or "1m"             => "month",
            _                                     => "any"
        };
    }
}
