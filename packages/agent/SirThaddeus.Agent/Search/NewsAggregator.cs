namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// News Aggregator — Seam 5 Implementation (aggregateNews)
//
// Deterministic digest assembly from scored/filtered results. Groups
// near-duplicates (reuses StoryClustering), enforces category balance,
// and produces structured NewsStory entries with confidence scores.
//
// Outputs:
//   - Ordered list of NewsStory (headline, source, date, url, confidence)
//   - CoverageConfidence (retrieval-level signal)
//   - AnswerConfidence (can the LLM build a useful summary from these?)
//   - FallbackMessage when confidence is too low for a polished digest
//
// Deterministic: same Retained input → same Stories output.
// ─────────────────────────────────────────────────────────────────────────

public static class NewsAggregator
{
    // ── Configuration ────────────────────────────────────────────────
    private const int    MaxStoriesPerDigest       = 8;
    private const int    MaxSourcesPerStory        = 3;
    private const double LowCoverageThreshold      = 0.4;
    private const double LowAnswerThreshold        = 0.35;
    private const double MinStoryConfidence         = 0.25;
    private const double DuplicateClusterThreshold  = 0.3; // matches StoryClustering default

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Assembles a news digest from scored and filtered results.
    /// Deduplicates, clusters, ranks by composite score, and produces
    /// structured story entries.
    /// </summary>
    public static AggregateNewsResult Aggregate(AggregateNewsRequest request)
    {
        var retained = request.Retained;

        if (retained.Count == 0)
        {
            return new AggregateNewsResult
            {
                Stories            = [],
                CoverageConfidence = 0.0,
                AnswerConfidence   = 0.0,
                IterationsUsed     = 1,
                FallbackMessage    = BuildFallbackMessage(request.Intent, request.TopicAnchor)
            };
        }

        // ── Step 1: Cluster retained results by title similarity ─────
        var sourcesForClustering = retained
            .OrderByDescending(r => r.CompositeScore)
            .Select(r => r.Source)
            .ToList();

        var clusters = StoryClustering.Cluster(sourcesForClustering, DuplicateClusterThreshold);

        // ── Step 2: Build stories from clusters ─────────────────────
        var stories = new List<NewsStory>();
        var retainedLookup = retained.ToDictionary(r => r.Source.SourceId, r => r);

        foreach (var cluster in clusters)
        {
            if (stories.Count >= MaxStoriesPerDigest)
                break;

            var story = BuildStoryFromCluster(cluster, retainedLookup);
            if (story is not null && story.Confidence >= MinStoryConfidence)
                stories.Add(story);
        }

        // ── Step 3: Compute confidence ──────────────────────────────
        var coverageConfidence = ComputeCoverageConfidence(stories, retained, request.Intent);
        var answerConfidence = ComputeAnswerConfidence(stories, coverageConfidence);

        // ── Step 4: Generate fallback if needed ─────────────────────
        string? fallbackMessage = null;
        if (coverageConfidence < LowCoverageThreshold)
        {
            fallbackMessage = BuildFallbackMessage(request.Intent, request.TopicAnchor);
        }

        return new AggregateNewsResult
        {
            Stories            = stories,
            CoverageConfidence = coverageConfidence,
            AnswerConfidence   = answerConfidence,
            IterationsUsed     = 1,
            FallbackMessage    = fallbackMessage
        };
    }

    /// <summary>
    /// Produces StoryReference records from the aggregate result for
    /// session persistence and follow-up deep-dive resolution.
    /// </summary>
    public static IReadOnlyList<StoryReference> BuildStoryReferences(
        AggregateNewsResult result)
    {
        var references = new List<StoryReference>();

        foreach (var story in result.Stories)
        {
            references.Add(new StoryReference
            {
                StoryId      = story.ClusterId ?? Guid.NewGuid().ToString("N")[..12],
                CanonicalUrl = story.Url,
                Headline     = story.Headline,
                Source       = story.Source,
                PublishedAt  = story.PublishedAt,
                ClusterId    = story.ClusterId
            });
        }

        return references;
    }

    // ─────────────────────────────────────────────────────────────────
    // Story Construction
    // ─────────────────────────────────────────────────────────────────

    private static NewsStory? BuildStoryFromCluster(
        StoryCluster cluster,
        Dictionary<string, RankedResult> retainedLookup)
    {
        if (cluster.Sources.Count == 0)
            return null;

        // Pick the best-scored source as the representative.
        var representative = cluster.Sources
            .Where(s => retainedLookup.ContainsKey(s.SourceId))
            .OrderByDescending(s => retainedLookup[s.SourceId].CompositeScore)
            .FirstOrDefault();

        if (representative is null)
            return null;

        var rankedRep = retainedLookup[representative.SourceId];
        var clusterId = GenerateClusterId(cluster);

        // Compute story confidence from the representative's composite score
        // plus a small boost for multi-source corroboration.
        var corroborationBoost = Math.Min(
            (cluster.Sources.Count - 1) * 0.05,
            0.15);

        var confidence = Math.Clamp(
            rankedRep.CompositeScore + corroborationBoost,
            0.0, 1.0);

        return new NewsStory
        {
            Headline     = representative.Title ?? cluster.RepresentativeTitle ?? "(untitled)",
            Source       = representative.Domain ?? "",
            PublishedAt  = representative.PublishedAt,
            Url          = representative.Url ?? "",
            WhyItMatters = null, // Populated downstream by LLM summarization
            Confidence   = confidence,
            ClusterId    = clusterId
        };
    }

    private static string GenerateClusterId(StoryCluster cluster)
    {
        // Deterministic ID from the first source's URL.
        var firstUrl = cluster.Sources.FirstOrDefault()?.Url ?? "";
        var hash = firstUrl.GetHashCode(StringComparison.Ordinal);
        return $"story_{Math.Abs(hash):x8}";
    }

    // ─────────────────────────────────────────────────────────────────
    // Confidence Computation
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Coverage confidence — how well do the assembled stories cover
    /// the search space? Based on story count, diversity, and quality.
    /// </summary>
    private static double ComputeCoverageConfidence(
        IReadOnlyList<NewsStory> stories,
        IReadOnlyList<RankedResult> retained,
        SearchIntent intent)
    {
        if (stories.Count == 0)
            return 0.0;

        // Story count signal (target: 4+ for headlines, 3+ for topic).
        var targetCount = intent == SearchIntent.NewsHeadlines ? 4.0 : 3.0;
        var countSignal = Math.Min(stories.Count / targetCount, 1.0);

        // Source diversity (unique domains across stories).
        var uniqueDomains = stories
            .Select(s => (s.Source ?? "").ToLowerInvariant())
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct()
            .Count();
        var diversitySignal = Math.Min(uniqueDomains / 3.0, 1.0);

        // Average story confidence.
        var avgConfidence = stories.Average(s => s.Confidence);

        return Math.Clamp(
            countSignal     * 0.35 +
            avgConfidence   * 0.40 +
            diversitySignal * 0.25,
            0.0, 1.0);
    }

    /// <summary>
    /// Answer confidence — can the LLM produce a useful summary?
    /// Separate from coverage: you can have good coverage but insufficient
    /// depth for a meaningful answer.
    /// </summary>
    private static double ComputeAnswerConfidence(
        IReadOnlyList<NewsStory> stories,
        double coverageConfidence)
    {
        if (stories.Count == 0)
            return 0.0;

        // Answer confidence tracks coverage but is weighted toward having
        // at least a few high-confidence stories.
        var highConfidenceCount = stories.Count(s => s.Confidence >= 0.6);
        var hasStrongSignal = highConfidenceCount >= 2;

        var answerBase = coverageConfidence * 0.7;
        var depthBonus = hasStrongSignal ? 0.2 : 0.0;
        var storyBonus = stories.Count >= 3 ? 0.1 : 0.0;

        return Math.Clamp(answerBase + depthBonus + storyBonus, 0.0, 1.0);
    }

    // ─────────────────────────────────────────────────────────────────
    // Fallback Messages
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an explicit degraded-mode fallback message instead of
    /// generating a polished summary over weak evidence.
    /// </summary>
    private static string BuildFallbackMessage(SearchIntent intent, string? topicAnchor)
    {
        if (intent == SearchIntent.TopicNews && !string.IsNullOrWhiteSpace(topicAnchor))
        {
            return $"I wasn't able to find enough reliable recent coverage on \"{topicAnchor}\" to " +
                   "assemble a confident briefing. The sources I found were either too thin, " +
                   "too old, or not clearly related to that topic. You could try a more specific " +
                   "query or check back later.";
        }

        return "I wasn't able to gather enough high-quality sources to build a confident " +
               "news briefing right now. The search returned limited or low-quality results. " +
               "You could try asking about a specific topic, or I can try again.";
    }
}
