namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Search Seam Contracts — Typed DTOs For Each Pipeline Stage
//
// These contracts define the boundaries between pipeline stages:
//   routeSearchIntent → buildSearchPlan → performSearch →
//   scoreAndFilter → aggregateNews / deepDiveArticle → shouldRetry
//
// Every request/response pair carries traceability IDs and deterministic
// reason codes so failures identify the broken seam, not the whole pipeline.
//
// Internal-first: these live inside the agent search layer. The external
// router (Intents / RouterOutput) is mapped to SearchIntent at the
// orchestration boundary via SearchIntentMapper.
// ─────────────────────────────────────────────────────────────────────────

#region ── Traceability ────────────────────────────────────────────────

/// <summary>
/// Traceability context carried through every seam boundary.
/// </summary>
public sealed record SearchTraceContext
{
    /// <summary>Stable session-level ID (matches <see cref="SearchSession"/>).</summary>
    public required string SearchSessionId { get; init; }

    /// <summary>Per-user-turn request ID (unique per invocation).</summary>
    public required string RequestId { get; init; }

    /// <summary>Search iteration (1-based; increments on retry).</summary>
    public int Iteration { get; init; } = 1;

    /// <summary>Resolved intent for this request.</summary>
    public SearchIntent Intent { get; init; } = SearchIntent.GeneralWebSearch;

    /// <summary>How this request originated.</summary>
    public SearchOrigin Origin { get; init; } = SearchOrigin.UserPrompt;
}

/// <summary>
/// How the search request was initiated.
/// </summary>
public enum SearchOrigin
{
    /// <summary>Direct user prompt.</summary>
    UserPrompt,

    /// <summary>Follow-up deep-dive from a previously surfaced story.</summary>
    FollowUpStory,

    /// <summary>Explicit article URL provided by the user.</summary>
    ExplicitUrl
}

#endregion

#region ── Internal Intent Taxonomy ────────────────────────────────────

/// <summary>
/// Internal typed intent. Mapped from <see cref="Intents"/> constants
/// at the orchestration boundary. Each intent gets its own planner
/// constraints, retry strategy, and scoring profile.
/// </summary>
public enum SearchIntent
{
    /// <summary>Broad headline news — recency-first, multi-source, balanced topics.</summary>
    NewsHeadlines,

    /// <summary>Topic-anchored news — all queries anchored on the user's explicit topic.</summary>
    TopicNews,

    /// <summary>Single article deep-dive — grounded extraction + corroboration.</summary>
    ArticleDeepDive,

    /// <summary>Local business lookup — hours, reviews, contact info.</summary>
    LocalBusinessLookup,

    /// <summary>General web search — canonical answers, entity-focused.</summary>
    GeneralWebSearch,

    /// <summary>Retailer-aware shopping recommendations with candidate evidence.</summary>
    ProductRecommendation
}

#endregion

#region ── Seam 1: Route Search Intent ─────────────────────────────────

public sealed record RouteSearchIntentRequest
{
    public required string UserMessage { get; init; }
    public SearchSession?  Session     { get; init; }
    public SearchTraceContext? Trace    { get; init; }
}

public sealed record RouteSearchIntentResult
{
    public required SearchIntent Intent    { get; init; }
    public required double       Confidence { get; init; }
    public required string       ReasonCode { get; init; }

    /// <summary>Extracted topic anchor for TOPIC_NEWS (null for headlines).</summary>
    public string? TopicAnchor { get; init; }

    /// <summary>Extracted geo anchor when location-specific news is detected.</summary>
    public string? GeoAnchor { get; init; }

    /// <summary>Recency hint derived from the routing signal.</summary>
    public string Recency { get; init; } = "24h";

    public bool NeedsAggregation { get; init; }
    public bool NeedsDeepDive    { get; init; }
}

/// <summary>Reason codes for route decisions — stable, loggable.</summary>
public static class RouteReasons
{
    public const string HeadlinePhrase     = "headline_phrase_match";
    public const string TopicNewsPhrase    = "topic_news_phrase_match";
    public const string ExplicitUrlInput   = "explicit_url_input";
    public const string LocalBusinessMatch = "local_business_heuristic";
    public const string FollowUpStory      = "follow_up_story_ref";
    public const string FactFindFallback   = "fact_find_fallback";
    public const string SessionFollowUp    = "session_follow_up";
    public const string ProductRecommendationPhrase = "product_recommendation_phrase";
}

#endregion

#region ── Seam 2: Build Search Plan ───────────────────────────────────

public sealed record BuildSearchPlanRequest
{
    public required SearchIntent  Intent       { get; init; }
    public required string        UserMessage  { get; init; }
    public string?                TopicAnchor  { get; init; }
    public string?                GeoAnchor    { get; init; }
    public string                 Recency      { get; init; } = "day";
    public SearchTraceContext?    Trace         { get; init; }
}

public sealed record SearchPlanQuery
{
    public required string Query     { get; init; }
    public required string Freshness { get; init; }
    public string          Vertical  { get; init; } = "news";
}

public sealed record BuildSearchPlanResult
{
    public required SearchIntent               Intent           { get; init; }
    public required IReadOnlyList<SearchPlanQuery> Queries      { get; init; }
    public IReadOnlyList<string>               BlockedPatterns  { get; init; } = [];
    public int                                 MinResults       { get; init; } = 8;
    public int                                 MaxIterations    { get; init; } = 3;

    /// <summary>Validation outcome — null when plan is valid.</summary>
    public string? ValidationFailure { get; init; }
}

/// <summary>
/// Planner hard-stop validation codes.
/// </summary>
public static class PlanValidationCodes
{
    public const string ContainsBlockedPattern  = "plan_contains_blocked_pattern";
    public const string ExceedsMaxQueries       = "plan_exceeds_max_queries";
    public const string MissingFreshness        = "plan_missing_freshness_on_news";
    public const string TopicDrift              = "plan_topic_drift";
    public const string MissingNewsVertical     = "plan_missing_news_vertical";
    public const string Valid                   = "plan_valid";
}

#endregion

#region ── Seam 3: Perform Search ──────────────────────────────────────

public sealed record PerformSearchRequest
{
    public required BuildSearchPlanResult Plan  { get; init; }
    public SearchTraceContext?            Trace { get; init; }
}

public sealed record PerformSearchResult
{
    public required IReadOnlyList<SourceItem> RawResults { get; init; }

    /// <summary>Total items returned across all queries in the plan.</summary>
    public int TotalReturned { get; init; }

    /// <summary>Per-query hit counts for diagnostics.</summary>
    public IReadOnlyList<int> PerQueryCounts { get; init; } = [];
}

#endregion

#region ── Seam 4: Score and Filter Results ────────────────────────────

public sealed record ScoreAndFilterRequest
{
    public required SearchIntent                Intent      { get; init; }
    public required IReadOnlyList<SourceItem>   RawResults  { get; init; }
    public string?                              TopicAnchor { get; init; }
    public DomainPolicy?                        DomainPolicy { get; init; }
    public SearchTraceContext?                   Trace       { get; init; }
}

/// <summary>
/// A result with deterministic quality scores attached.
/// </summary>
public sealed record RankedResult
{
    public required SourceItem Source { get; init; }

    /// <summary>
    /// Composite score (0.0–1.0). Items below the intent's quality floor
    /// are dropped before aggregation.
    /// </summary>
    public double CompositeScore { get; init; }

    // ── Individual dimensions ────────────────────────────────────────
    public double RecencyScore       { get; init; }
    public double NewsSourceScore    { get; init; }
    public double TitleRelevance     { get; init; }
    public double DuplicatePenalty   { get; init; }
    public double NonNewsPenalty     { get; init; }
    public double ThinContentPenalty { get; init; }

    /// <summary>Reason the item was dropped, or null if retained.</summary>
    public string? DropReason { get; init; }

    public bool IsDropped => DropReason is not null;
}

public sealed record ScoreAndFilterResult
{
    public required IReadOnlyList<RankedResult> Ranked { get; init; }

    /// <summary>Items that survived scoring.</summary>
    public IReadOnlyList<RankedResult> Retained =>
        Ranked.Where(r => !r.IsDropped).ToList();

    /// <summary>
    /// Retrieval-level confidence (0.0–1.0). Distinct from answer confidence.
    /// Low retrieval confidence should trigger retries or fallback messaging,
    /// not polished summaries over weak sources.
    /// </summary>
    public double RetrievalConfidence { get; init; }
}

/// <summary>Hard-drop reasons for the scorer.</summary>
public static class DropReasons
{
    public const string WikiReference       = "wiki_reference_page";
    public const string DictionaryThesaurus = "dictionary_thesaurus";
    public const string HelpForum           = "help_forum_page";
    public const string NoPublishDate       = "no_publish_date";
    public const string DuplicateAboveThreshold = "duplicate_above_threshold";
    public const string BelowQualityFloor   = "below_quality_floor";
    public const string BlockedDomain       = "blocked_domain_match";
    public const string ThinContent         = "thin_content";
}

#endregion

#region ── Seam 5: Aggregate News ──────────────────────────────────────

public sealed record AggregateNewsRequest
{
    public required IReadOnlyList<RankedResult> Retained  { get; init; }
    public required SearchIntent                Intent    { get; init; }
    public string?                              TopicAnchor { get; init; }
    public SearchTraceContext?                   Trace     { get; init; }
}

/// <summary>
/// A single story in the news digest.
/// </summary>
public sealed record NewsStory
{
    public required string  Headline     { get; init; }
    public required string  Source       { get; init; }
    public DateTimeOffset?  PublishedAt  { get; init; }
    public required string  Url          { get; init; }
    public string?          WhyItMatters { get; init; }
    public double           Confidence   { get; init; }
    public string?          ClusterId    { get; init; }
}

public sealed record AggregateNewsResult
{
    public required IReadOnlyList<NewsStory> Stories { get; init; }

    /// <summary>Overall digest coverage confidence (0.0–1.0).</summary>
    public double CoverageConfidence { get; init; }

    /// <summary>Separate answer-level confidence (0.0–1.0).</summary>
    public double AnswerConfidence   { get; init; }

    /// <summary>Number of search iterations consumed.</summary>
    public int IterationsUsed { get; init; } = 1;

    /// <summary>
    /// When confidence is low, a fallback summary is returned instead
    /// of a polished digest.
    /// </summary>
    public string? FallbackMessage { get; init; }

    public bool IsLowConfidence => CoverageConfidence < 0.4;
}

#endregion

#region ── Seam 6: Deep-Dive Article ───────────────────────────────────

/// <summary>
/// A durable reference to a previously surfaced story. Used to resolve
/// follow-up deep-dives without relying on snippet text alone.
/// </summary>
public sealed record StoryReference
{
    public required string  StoryId      { get; init; }
    public required string  CanonicalUrl { get; init; }
    public required string  Headline     { get; init; }
    public string           Source       { get; init; } = "";
    public DateTimeOffset?  PublishedAt  { get; init; }
    public string?          ClusterId    { get; init; }
}

public sealed record DeepDiveArticleRequest
{
    /// <summary>Resolved story reference (from session or explicit URL).</summary>
    public required StoryReference ArticleRef { get; init; }

    /// <summary>Original user message triggering the deep dive.</summary>
    public string? UserMessage { get; init; }

    public SearchTraceContext? Trace { get; init; }
}

/// <summary>
/// Explicit extraction quality — never hidden behind a normal-looking success.
/// </summary>
public enum ExtractionQuality
{
    /// <summary>Full article body extracted successfully.</summary>
    Full,

    /// <summary>Partial extraction — metadata present, body incomplete.</summary>
    MetadataOnly,

    /// <summary>No direct extraction; summary built from corroborating sources.</summary>
    CorroboratedSummary,

    /// <summary>Insufficient content — explicit degraded output returned.</summary>
    Insufficient
}

public sealed record DeepDiveArticleResult
{
    public required string              Headline          { get; init; }
    public required string              Source            { get; init; }
    public DateTimeOffset?              PublishedAt       { get; init; }
    public string?                      Author            { get; init; }
    public IReadOnlyList<string>        KeyPoints         { get; init; } = [];
    public string?                      Summary           { get; init; }
    public IReadOnlyList<string>        OpenQuestions      { get; init; } = [];
    public IReadOnlyList<RelatedCoverage> RelatedCoverage { get; init; } = [];

    /// <summary>Explicit extraction quality — never faked as full when it isn't.</summary>
    public required ExtractionQuality   ExtractionQuality { get; init; }

    /// <summary>Answer-level confidence for the deep dive output.</summary>
    public double AnswerConfidence { get; init; }
}

public sealed record RelatedCoverage
{
    public required string Title  { get; init; }
    public required string Source { get; init; }
    public required string Url    { get; init; }
}

#endregion

#region ── Seam 7: Retry Decision ──────────────────────────────────────

public sealed record ShouldRetryRequest
{
    public required BuildSearchPlanResult   Plan      { get; init; }
    public required ScoreAndFilterResult    Scored    { get; init; }
    public AggregateNewsResult?             Aggregate { get; init; }
    public SearchTraceContext?              Trace      { get; init; }
}

public sealed record RetryDecision
{
    public required bool   ShouldRetry { get; init; }
    public required string ReasonCode  { get; init; }

    /// <summary>Adjusted plan for the retry iteration (null if no retry).</summary>
    public BuildSearchPlanResult? AdjustedPlan { get; init; }
}

/// <summary>Retry decision reason codes.</summary>
public static class RetryReasons
{
    public const string Sufficient          = "sufficient_coverage";
    public const string MaxIterationsHit    = "max_iterations_reached";
    public const string BelowCoverageFloor  = "below_coverage_confidence";
    public const string TooFewUniqueStories = "too_few_unique_stories";
    public const string BelowQualityFloor   = "below_quality_floor";
}

#endregion

#region ── Domain / Source Policy (Configuration) ──────────────────────

/// <summary>
/// Domain-level policy for scoring and filtering. Intended to be loaded
/// from configuration rather than scattered across code.
/// </summary>
public sealed record DomainPolicy
{
    /// <summary>Domains boosted when scoring news results.</summary>
    public IReadOnlyList<string> BoostedNewsDomains { get; init; } = [];

    /// <summary>Domains penalized or blocked outright.</summary>
    public IReadOnlyList<string> BlockedDomains { get; init; } = [];

    /// <summary>URL patterns penalized or blocked.</summary>
    public IReadOnlyList<string> BlockedUrlPatterns { get; init; } = [];

    /// <summary>Recency thresholds per intent (intent name → max-age).</summary>
    public IReadOnlyDictionary<SearchIntent, TimeSpan> RecencyThresholds { get; init; } =
        new Dictionary<SearchIntent, TimeSpan>();

    /// <summary>Default policy — empty lists, no overrides.</summary>
    public static DomainPolicy Default { get; } = new();
}

#endregion

#region ── Intent Mapper (Router Compatibility) ────────────────────────

/// <summary>
/// Maps the external <see cref="Intents"/> taxonomy and
/// <see cref="SearchMode"/> into the internal <see cref="SearchIntent"/>
/// taxonomy. This is the compatibility bridge that lets us introduce
/// typed intents without modifying router contracts.
/// </summary>
public static class SearchIntentMapper
{
    /// <summary>
    /// Maps a <see cref="SearchMode"/> + user message to the internal
    /// <see cref="SearchIntent"/>. This is the primary mapping path
    /// used inside the orchestrator.
    /// </summary>
    public static SearchIntent FromSearchMode(
        SearchMode mode,
        string userMessage,
        SearchSession? session = null)
    {
        return mode switch
        {
            SearchMode.NewsAggregate  => ClassifyNewsSubIntent(userMessage),
            SearchMode.ProductRecommendation => SearchIntent.ProductRecommendation,
            SearchMode.DeepDiveBriefing => SearchIntent.LocalBusinessLookup,
            SearchMode.FollowUp       => ClassifyFollowUp(userMessage, session),
            SearchMode.WebFactFind    => SearchIntent.GeneralWebSearch,
            _ => SearchIntent.GeneralWebSearch
        };
    }

    /// <summary>
    /// Maps from the outer <see cref="Intents"/> string taxonomy.
    /// Used at the top-level orchestrator boundary.
    /// </summary>
    public static SearchIntent FromRouterIntent(string routerIntent, string userMessage)
    {
        return routerIntent switch
        {
            Intents.LookupNews    => ClassifyNewsSubIntent(userMessage),
            Intents.LookupProduct => SearchIntent.ProductRecommendation,
            Intents.LookupDeepDive => SearchIntent.ArticleDeepDive,
            Intents.LookupSearch  => SearchIntent.GeneralWebSearch,
            Intents.LookupFact    => SearchIntent.GeneralWebSearch,
            Intents.BrowseOnce    => SearchIntent.ArticleDeepDive,
            _                     => SearchIntent.GeneralWebSearch
        };
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static SearchIntent ClassifyNewsSubIntent(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();

        // If the message names a specific topic beyond generic headline
        // phrases, route to TOPIC_NEWS so the planner anchors every
        // query on that topic.
        if (HasExplicitNewsTopic(lower))
            return SearchIntent.TopicNews;

        return SearchIntent.NewsHeadlines;
    }

    private static SearchIntent ClassifyFollowUp(string userMessage, SearchSession? session)
    {
        // If the prior results were news clusters and the user asks to
        // go deeper, route to ArticleDeepDive.
        if (session?.LastMode == SearchMode.NewsAggregate &&
            session.LastClusters.Count > 0)
            return SearchIntent.ArticleDeepDive;

        // If the prior results were local business discovery, keep it there.
        if (session?.LastWasLocalBusinessDiscovery == true)
            return SearchIntent.LocalBusinessLookup;

        return SearchIntent.GeneralWebSearch;
    }

    /// <summary>
    /// Returns true if the message names a specific news topic beyond
    /// generic headline phrases. Used to decide NewsHeadlines vs TopicNews.
    /// </summary>
    internal static bool HasExplicitNewsTopic(string lowerMessage)
    {
        // Generic headline phrases — these do NOT indicate a topic.
        ReadOnlySpan<string> genericPhrases =
        [
            "headline news", "top news", "latest news",
            "news update", "breaking news", "current events",
            "what's happening today", "whats happening today",
            "what's happening", "whats happening",
            "what's going on", "whats going on",
            "daily briefing", "news feed",
            "bring me headline news", "bring me the news",
            "bring me news", "give me the news",
            "give me news", "show me news",
            "show me headlines", "give me headlines",
            "news please", "any news"
        ];

        foreach (var phrase in genericPhrases)
        {
            if (lowerMessage.Contains(phrase, StringComparison.Ordinal))
            {
                // If the entire message is essentially just the generic phrase
                // (plus noise), treat as headlines.
                var stripped = lowerMessage.Replace(phrase, "", StringComparison.Ordinal).Trim();
                var remainingSignificant = stripped
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2 &&
                                !IsNoiseWord(w))
                    .ToArray();

                if (remainingSignificant.Length == 0)
                    return false; // Pure headline request
            }
        }

        // If we get here, the message has news intent (routed by
        // SearchModeRouter) but isn't a pure headline request.
        // Check for topic-bearing words after stripping noise.
        var words = lowerMessage
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 &&
                        !IsNoiseWord(w) &&
                        !IsNewsKeyword(w))
            .ToArray();

        return words.Length > 0;
    }

    private static bool IsNoiseWord(string word) =>
        word is "the" or "me" or "some" or "any" or "hey" or "can"
            or "you" or "please" or "pls" or "now" or "today"
            or "give" or "bring" or "show" or "get" or "pull"
            or "find" or "look" or "what" or "whats" or "what's"
            or "tell" or "about" or "for" or "and" or "with";

    private static bool IsNewsKeyword(string word) =>
        word is "news" or "headlines" or "headline" or "stories"
            or "breaking" or "latest" or "recent" or "top"
            or "update" or "updates" or "current" or "events"
            or "briefing" or "happening" or "going" or "feed";
}

#endregion
