namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// News Result Scorer — Seam 4 Implementation (scoreAndFilter)
//
// Deterministic multi-dimensional scoring for search results. Each result
// gets scored on six independent dimensions, composited into a 0.0–1.0
// score, then filtered against intent-specific quality floors.
//
// Dimensions:
//   1. RecencyScore       — linear decay from publish date
//   2. NewsSourceScore    — domain authority tier
//   3. TitleRelevance     — query token coverage in title
//   4. DuplicatePenalty   — Jaccard similarity vs higher-scored results
//   5. NonNewsPenalty     — wiki/dictionary/reference detection
//   6. ThinContentPenalty — content length below threshold
//
// Hard drops (immediate rejection, not scored):
//   - Wiki reference pages
//   - Dictionary/thesaurus pages
//   - Help forums / Stack Overflow
//   - Blocked domains (from DomainPolicy)
//   - No publish date (for news intents only)
// ─────────────────────────────────────────────────────────────────────────

public static class NewsResultScorer
{
    // ── Composite weight profile for news intents ────────────────────
    private const double RecencyWeight       = 0.30;
    private const double NewsSourceWeight    = 0.20;
    private const double TitleRelevanceWeight = 0.25;
    private const double DuplicateWeight     = 0.10;
    private const double NonNewsWeight       = 0.10;
    private const double ThinContentWeight   = 0.05;

    // ── Quality floor per intent ─────────────────────────────────────
    private const double NewsQualityFloor = 0.30;
    private const double GeneralQualityFloor = 0.20;

    // ── Recency decay configuration ─────────────────────────────────
    private static readonly TimeSpan FullRecencyWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan ZeroRecencyWindow = TimeSpan.FromDays(30);

    // ── Thin content threshold ──────────────────────────────────────
    private const int MinNewsWordCount = 40;

    // ── Duplicate similarity threshold ──────────────────────────────
    private const double DuplicateThreshold = 0.5;

    // ── Domain tiers ────────────────────────────────────────────────

    /// <summary>Tier 1 — major wire services and prestige outlets.</summary>
    private static readonly HashSet<string> Tier1Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        "apnews.com", "reuters.com", "bbc.com", "bbc.co.uk",
        "nytimes.com", "washingtonpost.com", "theguardian.com",
        "wsj.com", "bloomberg.com", "npr.org", "pbs.org",
        "aljazeera.com", "economist.com", "ft.com"
    };

    /// <summary>Tier 2 — major national/international outlets.</summary>
    private static readonly HashSet<string> Tier2Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        "cnn.com", "cnbc.com", "nbcnews.com", "abcnews.go.com",
        "cbsnews.com", "foxnews.com", "usatoday.com",
        "politico.com", "thehill.com", "axios.com",
        "time.com", "newsweek.com", "huffpost.com",
        "arstechnica.com", "techcrunch.com", "theverge.com",
        "wired.com", "engadget.com", "zdnet.com",
        "nature.com", "science.org", "scientificamerican.com",
        "space.com", "cnet.com"
    };

    /// <summary>Domains that are hard-blocked for non-news content disguised as results.</summary>
    private static readonly HashSet<string> BlockedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "wikipedia.org", "en.wikipedia.org",
        "wiktionary.org", "en.wiktionary.org",
        "merriam-webster.com", "dictionary.com",
        "thesaurus.com", "vocabulary.com",
        "quizlet.com", "studylib.net",
        "pinterest.com", "pinterest.co.uk"
    };

    // ── Non-news URL patterns ───────────────────────────────────────

    private static readonly string[] NonNewsUrlPatterns =
    [
        "/wiki/",
        "/dictionary/",
        "/thesaurus/",
        "/definition/",
        "/help/",
        "/faq/",
        "/support/",
        "/forum/",
        "/questions/",   // Stack Overflow / SE
        "/answer/"
    ];

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scores and filters a set of raw search results. Returns all
    /// results with scores attached (including dropped items with
    /// their drop reason).
    /// </summary>
    public static ScoreAndFilterResult Score(ScoreAndFilterRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var qualityFloor = GetQualityFloor(request.Intent);
        var topicTokens = ExtractTopicTokens(request.TopicAnchor);
        var domainPolicy = request.DomainPolicy ?? DomainPolicy.Default;

        var ranked = new List<RankedResult>(request.RawResults.Count);

        // Score each result independently first.
        foreach (var source in request.RawResults)
        {
            // ── Hard drops ───────────────────────────────────────────
            var dropReason = CheckHardDrop(source, request.Intent, domainPolicy);
            if (dropReason is not null)
            {
                ranked.Add(new RankedResult
                {
                    Source     = source,
                    DropReason = dropReason
                });
                continue;
            }

            // ── Score dimensions ─────────────────────────────────────
            var recencyScore       = ScoreRecency(source.PublishedAt, now);
            var newsSourceScore    = ScoreNewsSource(source.Domain);
            var titleRelevance     = ScoreTitleRelevance(source.Title, topicTokens);
            var nonNewsPenalty     = ScoreNonNewsPenalty(source.Url, source.Title);
            var thinContentPenalty = ScoreThinContent(source.Snippet, source.ExtractedWordCount ?? 0);

            // Duplicate penalty is computed in a second pass (needs other scores).
            ranked.Add(new RankedResult
            {
                Source             = source,
                RecencyScore       = recencyScore,
                NewsSourceScore    = newsSourceScore,
                TitleRelevance     = titleRelevance,
                NonNewsPenalty     = nonNewsPenalty,
                ThinContentPenalty = thinContentPenalty,
                DuplicatePenalty   = 0.0 // filled in next pass
            });
        }

        // ── Second pass: duplicate penalty + composite score ─────────
        for (int i = 0; i < ranked.Count; i++)
        {
            if (ranked[i].IsDropped) continue;

            var dupPenalty = ComputeDuplicatePenalty(ranked[i], ranked, i);

            var composite = ComputeComposite(
                ranked[i].RecencyScore,
                ranked[i].NewsSourceScore,
                ranked[i].TitleRelevance,
                dupPenalty,
                ranked[i].NonNewsPenalty,
                ranked[i].ThinContentPenalty);

            // Apply quality floor.
            string? floorDrop = composite < qualityFloor
                ? DropReasons.BelowQualityFloor
                : null;

            ranked[i] = ranked[i] with
            {
                DuplicatePenalty = dupPenalty,
                CompositeScore   = composite,
                DropReason       = floorDrop
            };
        }

        // ── Compute retrieval confidence ─────────────────────────────
        var retained = ranked.Where(r => !r.IsDropped).ToList();
        var retrievalConfidence = ComputeRetrievalConfidence(retained, request.Intent);

        return new ScoreAndFilterResult
        {
            Ranked              = ranked,
            RetrievalConfidence = retrievalConfidence
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Hard Drop Checks
    // ─────────────────────────────────────────────────────────────────

    private static string? CheckHardDrop(
        SourceItem source,
        SearchIntent intent,
        DomainPolicy domainPolicy)
    {
        var domain = (source.Domain ?? "").Trim().ToLowerInvariant();
        var url = (source.Url ?? "").ToLowerInvariant();
        var title = (source.Title ?? "").ToLowerInvariant();

        // ── Blocked domain (built-in) ────────────────────────────────
        if (BlockedDomains.Contains(domain))
        {
            if (url.Contains("/wiki/", StringComparison.Ordinal))
                return DropReasons.WikiReference;
            return DropReasons.BlockedDomain;
        }

        // ── Blocked domain (policy) ─────────────────────────────────
        foreach (var blocked in domainPolicy.BlockedDomains)
        {
            if (domain.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                return DropReasons.BlockedDomain;
        }

        // ── Blocked URL patterns (policy) ────────────────────────────
        foreach (var pattern in domainPolicy.BlockedUrlPatterns)
        {
            if (url.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return DropReasons.BlockedDomain;
        }

        // ── Wiki reference ──────────────────────────────────────────
        if (url.Contains("/wiki/", StringComparison.Ordinal) &&
            (domain.Contains("wikipedia", StringComparison.Ordinal) ||
             domain.Contains("fandom.com", StringComparison.Ordinal)))
            return DropReasons.WikiReference;

        // ── Dictionary / thesaurus ──────────────────────────────────
        if (url.Contains("/definition/", StringComparison.Ordinal) ||
            url.Contains("/thesaurus/", StringComparison.Ordinal) ||
            url.Contains("/dictionary/", StringComparison.Ordinal) ||
            title.Contains("definition of ", StringComparison.Ordinal) ||
            title.Contains("synonyms for ", StringComparison.Ordinal))
            return DropReasons.DictionaryThesaurus;

        // ── Help forum ──────────────────────────────────────────────
        if (url.Contains("/questions/", StringComparison.Ordinal) &&
            (domain.Contains("stackoverflow", StringComparison.Ordinal) ||
             domain.Contains("stackexchange", StringComparison.Ordinal)))
            return DropReasons.HelpForum;

        if (url.Contains("/forum/", StringComparison.Ordinal) ||
            url.Contains("/community/", StringComparison.Ordinal) &&
            url.Contains("/question", StringComparison.Ordinal))
            return DropReasons.HelpForum;

        // ── Missing publish date (news intents only) ────────────────
        if (intent is SearchIntent.NewsHeadlines or SearchIntent.TopicNews &&
            source.PublishedAt is null)
            return DropReasons.NoPublishDate;

        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    // Scoring Dimensions
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Linear decay from 1.0 (within FullRecencyWindow) to 0.0
    /// (at or beyond ZeroRecencyWindow).
    /// </summary>
    internal static double ScoreRecency(DateTimeOffset? publishedAt, DateTimeOffset now)
    {
        if (publishedAt is null)
            return 0.3; // Unknown date gets partial credit, not zero.

        var age = now - publishedAt.Value;
        if (age <= TimeSpan.Zero)
            return 1.0; // Future date (clock skew) gets full score.

        if (age <= FullRecencyWindow)
            return 1.0;

        if (age >= ZeroRecencyWindow)
            return 0.0;

        // Linear interpolation.
        var range = ZeroRecencyWindow - FullRecencyWindow;
        var elapsed = age - FullRecencyWindow;
        return 1.0 - (elapsed / range);
    }

    /// <summary>
    /// Domain authority tier scoring.
    /// Tier1 = 1.0, Tier2 = 0.7, unknown = 0.4.
    /// </summary>
    internal static double ScoreNewsSource(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return 0.3;

        var normalized = domain.Trim().ToLowerInvariant();

        // Strip "www." prefix.
        if (normalized.StartsWith("www.", StringComparison.Ordinal))
            normalized = normalized[4..];

        if (Tier1Domains.Contains(normalized))
            return 1.0;

        if (Tier2Domains.Contains(normalized))
            return 0.7;

        // Policy-boosted domains could be checked here if DomainPolicy
        // is extended to carry boost tiers.

        return 0.4;
    }

    /// <summary>
    /// Measures what fraction of topic tokens appear in the title.
    /// Returns 0.5 baseline when no topic anchor is specified.
    /// </summary>
    internal static double ScoreTitleRelevance(string? title, HashSet<string> topicTokens)
    {
        if (topicTokens.Count == 0)
            return 0.5; // No anchor → neutral baseline.

        if (string.IsNullOrWhiteSpace(title))
            return 0.0;

        var titleTokens = Tokenize(title);
        if (titleTokens.Count == 0)
            return 0.0;

        var matches = topicTokens.Count(t =>
            titleTokens.Contains(t));

        return (double)matches / topicTokens.Count;
    }

    /// <summary>
    /// Detects non-news content in search results.
    /// Returns 0.0 (no penalty) to 1.0 (definitely non-news).
    /// </summary>
    internal static double ScoreNonNewsPenalty(string? url, string? title)
    {
        var urlLower = (url ?? "").ToLowerInvariant();
        var titleLower = (title ?? "").ToLowerInvariant();

        double penalty = 0.0;

        // URL path pattern penalties.
        foreach (var pattern in NonNewsUrlPatterns)
        {
            if (urlLower.Contains(pattern, StringComparison.Ordinal))
            {
                penalty += 0.5;
                break;
            }
        }

        // Title-based penalties.
        if (titleLower.Contains("how to ", StringComparison.Ordinal) ||
            titleLower.Contains("tutorial", StringComparison.Ordinal) ||
            titleLower.Contains("recipe ", StringComparison.Ordinal))
            penalty += 0.3;

        if (titleLower.Contains("definition", StringComparison.Ordinal) ||
            titleLower.Contains("meaning of", StringComparison.Ordinal) ||
            titleLower.Contains("synonym", StringComparison.Ordinal))
            penalty += 0.5;

        return Math.Min(penalty, 1.0);
    }

    /// <summary>
    /// Penalizes results with very thin or absent content signals.
    /// Returns 0.0 (adequate) to 1.0 (very thin).
    /// </summary>
    internal static double ScoreThinContent(string? snippet, int extractedWordCount)
    {
        // If extracted word count is available and adequate, no penalty.
        if (extractedWordCount >= MinNewsWordCount)
            return 0.0;

        // Fall back to snippet length as proxy.
        var snippetWords = (snippet ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Length;

        if (snippetWords >= MinNewsWordCount)
            return 0.0;

        if (snippetWords < 10)
            return 1.0;

        // Linear scale from 10 words (1.0 penalty) to MinNewsWordCount (0.0).
        return 1.0 - ((double)(snippetWords - 10) / (MinNewsWordCount - 10));
    }

    /// <summary>
    /// Computes duplicate penalty for a result against all higher-ranked
    /// non-dropped results. Uses Jaccard similarity on title tokens.
    /// </summary>
    private static double ComputeDuplicatePenalty(
        RankedResult current,
        List<RankedResult> allResults,
        int currentIndex)
    {
        var currentTokens = Tokenize(current.Source.Title ?? "");
        if (currentTokens.Count == 0)
            return 0.0;

        double maxSimilarity = 0.0;

        for (int j = 0; j < currentIndex; j++)
        {
            if (allResults[j].IsDropped) continue;

            var otherTokens = Tokenize(allResults[j].Source.Title ?? "");
            if (otherTokens.Count == 0) continue;

            var similarity = JaccardSimilarity(currentTokens, otherTokens);
            if (similarity > maxSimilarity)
                maxSimilarity = similarity;
        }

        if (maxSimilarity >= DuplicateThreshold)
            return maxSimilarity; // High similarity → high penalty.

        return 0.0;
    }

    // ─────────────────────────────────────────────────────────────────
    // Composite Score
    // ─────────────────────────────────────────────────────────────────

    private static double ComputeComposite(
        double recency,
        double newsSource,
        double titleRelevance,
        double duplicatePenalty,
        double nonNewsPenalty,
        double thinContentPenalty)
    {
        // Positive signals.
        double score =
            recency        * RecencyWeight +
            newsSource     * NewsSourceWeight +
            titleRelevance * TitleRelevanceWeight;

        // Penalties (subtracted).
        score -= duplicatePenalty   * DuplicateWeight;
        score -= nonNewsPenalty     * NonNewsWeight;
        score -= thinContentPenalty * ThinContentWeight;

        return Math.Clamp(score, 0.0, 1.0);
    }

    // ─────────────────────────────────────────────────────────────────
    // Retrieval Confidence
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes retrieval-level confidence (distinct from answer confidence).
    /// Low retrieval confidence should trigger retries or fallback messaging.
    /// </summary>
    private static double ComputeRetrievalConfidence(
        IReadOnlyList<RankedResult> retained,
        SearchIntent intent)
    {
        if (retained.Count == 0)
            return 0.0;

        // Base confidence from count.
        var countSignal = intent switch
        {
            SearchIntent.NewsHeadlines => Math.Min(retained.Count / 8.0, 1.0),
            SearchIntent.TopicNews     => Math.Min(retained.Count / 5.0, 1.0),
            _                          => Math.Min(retained.Count / 3.0, 1.0)
        };

        // Average quality of retained results.
        var avgScore = retained.Average(r => r.CompositeScore);

        // Source diversity — number of unique domains.
        var uniqueDomains = retained
            .Select(r => (r.Source.Domain ?? "").ToLowerInvariant())
            .Distinct()
            .Count();
        var diversitySignal = Math.Min(uniqueDomains / 4.0, 1.0);

        // Weighted combination.
        return Math.Clamp(
            countSignal   * 0.35 +
            avgScore      * 0.40 +
            diversitySignal * 0.25,
            0.0, 1.0);
    }

    // ─────────────────────────────────────────────────────────────────
    // Quality Floor
    // ─────────────────────────────────────────────────────────────────

    private static double GetQualityFloor(SearchIntent intent)
    {
        return intent switch
        {
            SearchIntent.NewsHeadlines   => NewsQualityFloor,
            SearchIntent.TopicNews       => NewsQualityFloor,
            SearchIntent.ArticleDeepDive => GeneralQualityFloor,
            _                            => GeneralQualityFloor
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Token Helpers
    // ─────────────────────────────────────────────────────────────────

    private static HashSet<string> ExtractTopicTokens(string? topicAnchor)
    {
        if (string.IsNullOrWhiteSpace(topicAnchor))
            return [];

        return Tokenize(topicAnchor);
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = text
            .ToLowerInvariant()
            .Split([' ', '-', '—', ':', ',', '.', '!', '?', '\'', '"'],
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && !IsStopWord(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return tokens;
    }

    private static bool IsStopWord(string word) =>
        word is "the" or "a" or "an" or "is" or "are" or "was" or "were"
            or "in" or "on" or "at" or "to" or "for" or "of" or "and"
            or "or" or "but" or "not" or "with" or "by" or "from"
            or "as" or "it" or "its" or "this" or "that" or "be"
            or "has" or "had" or "have" or "do" or "does" or "did";

    private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        var intersection = a.Count(x => b.Contains(x));
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }
}
