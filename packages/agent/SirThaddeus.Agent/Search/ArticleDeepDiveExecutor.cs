using System.Text;
using System.Text.Json;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Article Deep-Dive Executor — Seam 6 Implementation (deepDiveArticle)
//
// Implements the article-level deep-dive seam for:
//   1. Explicit URL requests (user provides a URL)
//   2. Follow-up deep-dive from previously surfaced story references
//
// Pipeline:
//   Canonical URL resolution → browser_navigate fetch → quality assessment
//   → key point extraction → optional corroboration (2-4 sources)
//
// Extraction quality is EXPLICIT — never faked as full when it isn't:
//   Full              — article body extracted, sufficient word count
//   MetadataOnly      — title/source/date present, body incomplete
//   CorroboratedSummary — no direct extraction, built from other sources
//   Insufficient      — not enough content, explicit degraded output
// ─────────────────────────────────────────────────────────────────────────

public sealed class ArticleDeepDiveExecutor
{
    private const string BrowseToolName    = "browser_navigate";
    private const string BrowseToolNameAlt = "BrowserNavigate";
    private const string WebSearchToolName    = "web_search";
    private const string WebSearchToolNameAlt = "WebSearch";

    private const int MinFullExtractionWordCount  = 200;
    private const int MinMetadataExtractionLength  = 50;
    private const int MaxArticleChars              = 5000;
    private const int MaxCorroborationSources      = 4;
    private const int MinCorroborationSources      = 2;

    private readonly IMcpToolClient _mcp;
    private readonly IAuditLogger  _audit;

    public ArticleDeepDiveExecutor(IMcpToolClient mcp, IAuditLogger audit)
    {
        _mcp   = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a deep-dive on a single article. Fetches the canonical
    /// URL, assesses extraction quality, and optionally corroborates
    /// with additional sources.
    /// </summary>
    public async Task<DeepDiveArticleResult> ExecuteAsync(
        DeepDiveArticleRequest request,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var articleRef = request.ArticleRef;
        var url = articleRef.CanonicalUrl;

        _audit.Append(new AuditEvent
        {
            Actor  = "deep_dive",
            Action = "ARTICLE_DEEPDIVE_START",
            Result = $"url={url}, headline={Truncate(articleRef.Headline, 60)}"
        });

        // ── Step 1: Fetch the canonical article ─────────────────────
        var (articleContent, fetchSuccess) = await FetchArticleAsync(
            url, toolCallsMade, ct);

        // ── Step 2: Assess extraction quality ───────────────────────
        var quality = AssessExtractionQuality(articleContent, articleRef);

        // ── Step 3: Extract key points ──────────────────────────────
        var keyPoints = ExtractKeyPoints(articleContent, articleRef);

        // ── Step 4: Corroborate if needed ───────────────────────────
        var relatedCoverage = new List<RelatedCoverage>();
        string? corroboratedSummary = null;

        if (quality is ExtractionQuality.MetadataOnly or ExtractionQuality.Insufficient)
        {
            var (corrobSources, corrobSummary) = await CorroborateAsync(
                articleRef, toolCallsMade, ct);
            relatedCoverage.AddRange(corrobSources);
            corroboratedSummary = corrobSummary;

            // Upgrade quality if corroboration provided meaningful content.
            if (quality == ExtractionQuality.Insufficient &&
                relatedCoverage.Count >= MinCorroborationSources)
            {
                quality = ExtractionQuality.CorroboratedSummary;
            }
        }
        else if (fetchSuccess)
        {
            // Even for full extractions, fetch related coverage for context.
            var (corrobSources, _) = await CorroborateAsync(
                articleRef, toolCallsMade, ct);
            relatedCoverage.AddRange(corrobSources);
        }

        // ── Step 5: Build result ────────────────────────────────────
        var summary = quality switch
        {
            ExtractionQuality.Full => BuildFullSummary(articleContent, keyPoints),
            ExtractionQuality.MetadataOnly => BuildMetadataSummary(articleRef, articleContent, keyPoints),
            ExtractionQuality.CorroboratedSummary => corroboratedSummary ?? BuildDegradedSummary(articleRef),
            _ => BuildDegradedSummary(articleRef)
        };

        var answerConfidence = ComputeAnswerConfidence(quality, keyPoints.Count, relatedCoverage.Count);

        _audit.Append(new AuditEvent
        {
            Actor  = "deep_dive",
            Action = "ARTICLE_DEEPDIVE_DONE",
            Result = $"quality={quality}, keyPoints={keyPoints.Count}, related={relatedCoverage.Count}, confidence={answerConfidence:F2}"
        });

        return new DeepDiveArticleResult
        {
            Headline          = articleRef.Headline,
            Source            = articleRef.Source,
            PublishedAt       = articleRef.PublishedAt,
            Author            = ExtractAuthor(articleContent),
            KeyPoints         = keyPoints,
            Summary           = summary,
            OpenQuestions      = BuildOpenQuestions(quality, articleRef),
            RelatedCoverage   = relatedCoverage,
            ExtractionQuality = quality,
            AnswerConfidence  = answerConfidence
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Article Fetch
    // ─────────────────────────────────────────────────────────────────

    private async Task<(string? Content, bool Success)> FetchArticleAsync(
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

                _audit.Append(new AuditEvent
                {
                    Actor  = "deep_dive",
                    Action = "ARTICLE_FETCH_FAILED",
                    Result = $"url={url}, error={ex.Message}"
                });

                return (null, false);
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = resolvedToolName,
            Arguments = args,
            Result    = content!.Length > 200 ? content[..200] + "…" : content,
            Success   = true
        });

        // Truncate oversized content.
        if (content!.Length > MaxArticleChars)
            content = content[..MaxArticleChars] + "\n[…truncated]";

        return (content, true);
    }

    // ─────────────────────────────────────────────────────────────────
    // Extraction Quality Assessment
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Assesses the quality of extracted content. Never lies about
    /// what was actually extracted.
    /// </summary>
    private static ExtractionQuality AssessExtractionQuality(
        string? content,
        StoryReference articleRef)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ExtractionQuality.Insufficient;

        // Check for low-signal browser output (error pages, paywalls, etc.)
        if (WebSearchFollowUpSupport.IsLowSignalBrowserNavigateContent(content))
            return ExtractionQuality.Insufficient;

        var wordCount = content
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Length;

        if (wordCount >= MinFullExtractionWordCount)
            return ExtractionQuality.Full;

        if (wordCount >= MinMetadataExtractionLength &&
            !string.IsNullOrWhiteSpace(articleRef.Headline))
            return ExtractionQuality.MetadataOnly;

        return ExtractionQuality.Insufficient;
    }

    // ─────────────────────────────────────────────────────────────────
    // Key Point Extraction
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts key points from article content using sentence-level
    /// heuristics. No LLM call — deterministic.
    /// </summary>
    private static IReadOnlyList<string> ExtractKeyPoints(
        string? content,
        StoryReference articleRef)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var sentences = SplitIntoSentences(content);
        var keyPoints = new List<string>();
        var seenNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sentence in sentences)
        {
            if (keyPoints.Count >= 5)
                break;

            var trimmed = sentence.Trim();
            if (trimmed.Length < 30 || trimmed.Length > 300)
                continue;

            // Skip navigation/boilerplate-looking sentences.
            if (LooksLikeBoilerplate(trimmed))
                continue;

            // Deduplicate by normalized form.
            var normalized = trimmed.ToLowerInvariant();
            if (!seenNormalized.Add(normalized))
                continue;

            // Prefer sentences with concrete detail signals.
            if (HasConcreteDetail(trimmed))
                keyPoints.Add(trimmed);
        }

        // If we didn't find enough concrete-detail sentences,
        // take the first few non-boilerplate sentences.
        if (keyPoints.Count < 2)
        {
            keyPoints.Clear();
            foreach (var sentence in sentences)
            {
                if (keyPoints.Count >= 3)
                    break;

                var trimmed = sentence.Trim();
                if (trimmed.Length < 20 || trimmed.Length > 300)
                    continue;
                if (LooksLikeBoilerplate(trimmed))
                    continue;
                if (seenNormalized.Add(trimmed.ToLowerInvariant()))
                    keyPoints.Add(trimmed);
            }
        }

        return keyPoints;
    }

    // ─────────────────────────────────────────────────────────────────
    // Corroboration
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches corroborating coverage from other sources to supplement
    /// weak or absent primary extraction.
    /// </summary>
    private async Task<(List<RelatedCoverage>, string?)> CorroborateAsync(
        StoryReference articleRef,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var related = new List<RelatedCoverage>();
        var query = $"{articleRef.Headline} news";

        // Truncate long headlines for query.
        if (query.Length > 80)
            query = query[..80];

        var args = JsonSerializer.Serialize(new
        {
            query,
            maxResults = MaxCorroborationSources + 2, // fetch extra to filter
            recency = "week"
        });

        string? searchResult = null;
        try
        {
            searchResult = await _mcp.CallToolAsync(WebSearchToolName, args, ct);
        }
        catch
        {
            try
            {
                searchResult = await _mcp.CallToolAsync(WebSearchToolNameAlt, args, ct);
            }
            catch
            {
                return (related, null);
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = WebSearchToolName,
            Arguments = args,
            Result    = Truncate(searchResult ?? "", 200),
            Success   = !string.IsNullOrWhiteSpace(searchResult)
        });

        if (string.IsNullOrWhiteSpace(searchResult))
            return (related, null);

        // Parse related coverage from search results.
        var corrobSummary = new StringBuilder();
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(searchResult);
            if (parsed.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in parsed.EnumerateArray())
                {
                    if (related.Count >= MaxCorroborationSources)
                        break;

                    var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var source = item.TryGetProperty("source", out var s) ? s.GetString() : null;
                    var snippet = item.TryGetProperty("snippet", out var sn) ? sn.GetString() : null;

                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
                        continue;

                    // Skip the same article we're deep-diving.
                    if (url!.Equals(articleRef.CanonicalUrl, StringComparison.OrdinalIgnoreCase))
                        continue;

                    related.Add(new RelatedCoverage
                    {
                        Title  = title!,
                        Source = source ?? ExtractDomain(url),
                        Url    = url
                    });

                    if (!string.IsNullOrWhiteSpace(snippet))
                        corrobSummary.AppendLine($"- {title}: {snippet}");
                }
            }
        }
        catch
        {
            // Best-effort parsing; don't fail the deep-dive on JSON issues.
        }

        var summaryText = corrobSummary.Length > 0 ? corrobSummary.ToString() : null;
        return (related, summaryText);
    }

    // ─────────────────────────────────────────────────────────────────
    // Summary Builders
    // ─────────────────────────────────────────────────────────────────

    private static string BuildFullSummary(string? content, IReadOnlyList<string> keyPoints)
    {
        if (keyPoints.Count == 0)
            return content ?? "";

        var sb = new StringBuilder();
        sb.AppendLine("Key points:");
        foreach (var point in keyPoints)
        {
            sb.AppendLine($"• {point}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildMetadataSummary(
        StoryReference articleRef,
        string? content,
        IReadOnlyList<string> keyPoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**{articleRef.Headline}**");
        if (!string.IsNullOrWhiteSpace(articleRef.Source))
            sb.AppendLine($"Source: {articleRef.Source}");
        if (articleRef.PublishedAt.HasValue)
            sb.AppendLine($"Published: {articleRef.PublishedAt.Value:yyyy-MM-dd}");

        sb.AppendLine();
        sb.AppendLine("*Note: I was only able to extract partial content from this article.*");

        if (keyPoints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("From what was available:");
            foreach (var point in keyPoints)
            {
                sb.AppendLine($"• {point}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildDegradedSummary(StoryReference articleRef)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**{articleRef.Headline}**");
        if (!string.IsNullOrWhiteSpace(articleRef.Source))
            sb.AppendLine($"Source: {articleRef.Source}");
        sb.AppendLine();
        sb.AppendLine("I wasn't able to extract the full content of this article. " +
                       "The page may be behind a paywall, require JavaScript, or use a format " +
                       "I can't parse. Here's what I know from the search results and related coverage.");
        return sb.ToString().TrimEnd();
    }

    // ─────────────────────────────────────────────────────────────────
    // Open Questions
    // ─────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildOpenQuestions(
        ExtractionQuality quality,
        StoryReference articleRef)
    {
        var questions = new List<string>();

        if (quality == ExtractionQuality.Insufficient)
        {
            questions.Add("Full article content was not available — details may be incomplete.");
        }
        else if (quality == ExtractionQuality.MetadataOnly)
        {
            questions.Add("Only partial content was extracted — some details may be missing.");
        }
        else if (quality == ExtractionQuality.CorroboratedSummary)
        {
            questions.Add("This summary is built from related coverage, not the original article.");
        }

        return questions;
    }

    // ─────────────────────────────────────────────────────────────────
    // Answer Confidence
    // ─────────────────────────────────────────────────────────────────

    private static double ComputeAnswerConfidence(
        ExtractionQuality quality,
        int keyPointCount,
        int relatedCount)
    {
        var baseConfidence = quality switch
        {
            ExtractionQuality.Full                => 0.85,
            ExtractionQuality.MetadataOnly        => 0.50,
            ExtractionQuality.CorroboratedSummary => 0.45,
            ExtractionQuality.Insufficient        => 0.15,
            _ => 0.2
        };

        // Bonus for key points.
        var keyPointBonus = Math.Min(keyPointCount * 0.03, 0.10);

        // Bonus for corroboration.
        var corrobBonus = Math.Min(relatedCount * 0.03, 0.10);

        return Math.Clamp(baseConfidence + keyPointBonus + corrobBonus, 0.0, 1.0);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string? ExtractAuthor(string? content)
    {
        // Simple "By [Name]" extraction from article content.
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Take(10))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("By ", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length > 3 && trimmed.Length < 60)
            {
                return trimmed[3..].Trim().TrimEnd(',', '.');
            }
        }

        return null;
    }

    private static IReadOnlyList<string> SplitIntoSentences(string content)
    {
        // Basic sentence splitting on period + space boundaries.
        var sentences = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in content)
        {
            current.Append(ch);

            if (ch is '.' or '!' or '?' && current.Length > 15)
            {
                sentences.Add(current.ToString().Trim());
                current.Clear();
            }
        }

        if (current.Length > 10)
            sentences.Add(current.ToString().Trim());

        return sentences;
    }

    private static bool LooksLikeBoilerplate(string sentence)
    {
        var lower = sentence.ToLowerInvariant();
        return lower.Contains("cookie") ||
               lower.Contains("privacy policy") ||
               lower.Contains("subscribe") ||
               lower.Contains("sign up") ||
               lower.Contains("advertisement") ||
               lower.Contains("read more at") ||
               lower.Contains("click here") ||
               lower.Contains("terms of service") ||
               lower.Contains("all rights reserved") ||
               lower.Contains("©");
    }

    private static bool HasConcreteDetail(string sentence)
    {
        // Concrete detail signals: numbers, dates, names (capitalized words),
        // quotes, specific actions.
        return sentence.Any(char.IsDigit) ||
               sentence.Contains('"') ||
               sentence.Contains("said", StringComparison.OrdinalIgnoreCase) ||
               sentence.Contains("according to", StringComparison.OrdinalIgnoreCase) ||
               sentence.Contains("announced", StringComparison.OrdinalIgnoreCase) ||
               sentence.Contains("reported", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host.Replace("www.", "");
        }
        catch
        {
            return "";
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
