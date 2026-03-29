using SirThaddeus.Agent.Routing;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// News Intent Router — Seam 1 Implementation
//
// Wraps the existing deterministic SearchModeRouter heuristics
// and emits a typed RouteSearchIntentResult with:
//   - Internal SearchIntent (NEWS_HEADLINES / TOPIC_NEWS / etc.)
//   - Confidence + reason code
//   - Topic anchor (for TOPIC_NEWS anchoring in the planner)
//   - Geo anchor (for local-scoped news queries)
//   - Recency hint
//
// No LLM calls. Purely deterministic.
// ─────────────────────────────────────────────────────────────────────────

public static class NewsIntentRouter
{
    /// <summary>
    /// Routes a user message through the deterministic intent classification
    /// pipeline and returns a typed <see cref="RouteSearchIntentResult"/>.
    /// </summary>
    public static RouteSearchIntentResult Route(RouteSearchIntentRequest request)
    {
        var userMessage = request.UserMessage ?? "";
        var session = request.Session;
        var now = DateTimeOffset.UtcNow;

        // ── Delegate to existing SearchModeRouter for base classification ──
        var mode = SearchModeRouter.Classify(userMessage, session ?? new SearchSession(), now);

        // ── Map to internal intent ──
        var intent = SearchIntentMapper.FromSearchMode(mode, userMessage, session);

        // ── Extract anchors and build reason ──
        var lower = userMessage.Trim().ToLowerInvariant();
        var topicAnchor = ExtractTopicAnchor(intent, lower);
        var geoAnchor = ExtractGeoAnchor(lower);
        var recency = ResolveRecencyHint(intent, lower);
        var reasonCode = DetermineReasonCode(mode, intent, lower, session);
        var confidence = ComputeConfidence(mode, intent, lower, session);

        return new RouteSearchIntentResult
        {
            Intent          = intent,
            Confidence      = confidence,
            ReasonCode      = reasonCode,
            TopicAnchor     = topicAnchor,
            GeoAnchor       = geoAnchor,
            Recency         = recency,
            NeedsAggregation = intent is SearchIntent.NewsHeadlines or SearchIntent.TopicNews,
            NeedsDeepDive    = intent == SearchIntent.ArticleDeepDive
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Topic Anchor Extraction
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the topic anchor for TOPIC_NEWS intent. Returns null
    /// for headlines (no topic anchoring needed).
    /// </summary>
    internal static string? ExtractTopicAnchor(SearchIntent intent, string lowerMessage)
    {
        if (intent != SearchIntent.TopicNews)
            return null;

        // Strip news-request framing to isolate the topic.
        var topic = StripNewsFraming(lowerMessage);

        if (string.IsNullOrWhiteSpace(topic) || topic.Length < 2)
            return null;

        return NormalizeTopic(topic);
    }

    /// <summary>
    /// Strips news request framing phrases to isolate the core topic.
    /// "latest news about AI regulation" → "ai regulation"
    /// "what's happening with tesla stock" → "tesla stock"
    /// </summary>
    private static string StripNewsFraming(string lower)
    {
        // Order matters: longer phrases first to avoid partial stripping.
        ReadOnlySpan<string> framingPhrases =
        [
            "what's the latest news on",
            "whats the latest news on",
            "what's the latest on",
            "whats the latest on",
            "latest news about",
            "latest news on",
            "news coverage of",
            "news coverage on",
            "news coverage about",
            "news update on",
            "news update about",
            "news updates on",
            "news updates about",
            "what's happening with",
            "whats happening with",
            "what's going on with",
            "whats going on with",
            "any news about",
            "any news on",
            "news about",
            "news on",
            "news regarding",
            "updates on",
            "updates about",
            "update on",
            "update about",
            "coverage on",
            "coverage about",
            "coverage of"
        ];

        ReadOnlySpan<string> prefixPhrases =
        [
            "bring me the latest",
            "bring me latest",
            "bring me the news",
            "bring me news about",
            "bring me news on",
            "give me news about",
            "give me news on",
            "give me the news on",
            "give me the latest on",
            "show me news about",
            "show me news on",
            "show me the latest",
            "find news about",
            "find news on",
            "pull up news about",
            "pull up news on",
            "pull up the latest",
            "get me news about",
            "get me news on",
            "can you find news",
            "can you get news"
        ];

        var result = lower;

        foreach (var phrase in prefixPhrases)
        {
            if (result.StartsWith(phrase, StringComparison.Ordinal))
            {
                result = result[phrase.Length..].Trim();
                break;
            }
        }

        foreach (var phrase in framingPhrases)
        {
            var idx = result.IndexOf(phrase, StringComparison.Ordinal);
            if (idx >= 0)
            {
                // Take everything after the framing phrase.
                result = result[(idx + phrase.Length)..].Trim();
                break;
            }
        }

        // Strip trailing noise.
        result = StripTrailingNoise(result);

        return result;
    }

    private static string StripTrailingNoise(string topic)
    {
        ReadOnlySpan<string> trailingNoise =
        [
            " please", " pls", " for me", " right now",
            " today", " lately", " recently"
        ];

        foreach (var noise in trailingNoise)
        {
            if (topic.EndsWith(noise, StringComparison.Ordinal))
                topic = topic[..^noise.Length].Trim();
        }

        // Strip leading articles.
        if (topic.StartsWith("the ", StringComparison.Ordinal))
            topic = topic[4..];

        return topic.Trim();
    }

    private static string NormalizeTopic(string topic)
    {
        // Collapse whitespace and trim.
        var normalized = System.Text.RegularExpressions.Regex.Replace(topic, @"\s+", " ").Trim();

        // Cap at 60 chars.
        if (normalized.Length > 60)
            normalized = normalized[..60].Trim();

        return normalized;
    }

    // ─────────────────────────────────────────────────────────────────
    // Geo Anchor Extraction
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts geographic anchor from the user message when a location
    /// is explicitly mentioned with news intent.
    /// </summary>
    internal static string? ExtractGeoAnchor(string lowerMessage)
    {
        // Look for "[location] news" or "news in [location]" patterns.
        ReadOnlySpan<string> geoPrepositions = ["news in ", "news from ", "news near "];

        foreach (var prep in geoPrepositions)
        {
            var idx = lowerMessage.IndexOf(prep, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var after = lowerMessage[(idx + prep.Length)..].Trim();
                var geo = ExtractLocationToken(after);
                if (!string.IsNullOrWhiteSpace(geo))
                    return geo;
            }
        }

        // "Portland news", "Seattle latest news"
        ReadOnlySpan<string> geoSuffixes = [" news", " local news", " headlines"];
        foreach (var suffix in geoSuffixes)
        {
            var idx = lowerMessage.IndexOf(suffix, StringComparison.Ordinal);
            if (idx > 0)
            {
                var before = lowerMessage[..idx].Trim();
                var words = before.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length <= 3 && words.Length > 0)
                {
                    var candidate = string.Join(' ', words);
                    if (LooksLikeLocationName(candidate))
                        return candidate;
                }
            }
        }

        return null;
    }

    private static string ExtractLocationToken(string after)
    {
        // Take up to 3 words as a location name.
        var words = after.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var locationWords = words.TakeWhile((w, i) => i < 3 && !IsNewsKeyword(w)).ToArray();
        return locationWords.Length > 0 ? string.Join(' ', locationWords) : "";
    }

    private static bool LooksLikeLocationName(string candidate)
    {
        // Very basic heuristic: reject pure noise words. Real geo
        // resolution is done downstream by EntityResolver.
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        ReadOnlySpan<string> noiseWords =
        [
            "the", "latest", "breaking", "top", "recent",
            "my", "some", "any", "all", "more"
        ];

        foreach (var noise in noiseWords)
        {
            if (candidate.Equals(noise, StringComparison.Ordinal))
                return false;
        }

        return candidate.Length >= 2;
    }

    private static bool IsNewsKeyword(string word) =>
        word is "news" or "headlines" or "headline" or "stories"
            or "breaking" or "latest" or "recent" or "top"
            or "update" or "updates" or "current" or "events";

    // ─────────────────────────────────────────────────────────────────
    // Recency Resolution
    // ─────────────────────────────────────────────────────────────────

    private static string ResolveRecencyHint(SearchIntent intent, string lowerMessage)
    {
        // Explicit temporal markers take precedence.
        if (lowerMessage.Contains("today") || lowerMessage.Contains("breaking") ||
            lowerMessage.Contains("right now") || lowerMessage.Contains("just happened"))
            return "day";

        if (lowerMessage.Contains("this week") || lowerMessage.Contains("past week") ||
            lowerMessage.Contains("last few days"))
            return "week";

        if (lowerMessage.Contains("this month") || lowerMessage.Contains("past month") ||
            lowerMessage.Contains("last month"))
            return "month";

        // Default by intent.
        return intent switch
        {
            SearchIntent.NewsHeadlines  => "day",
            SearchIntent.TopicNews      => "week",
            SearchIntent.ArticleDeepDive => "any",
            _ => "any"
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Reason Code + Confidence
    // ─────────────────────────────────────────────────────────────────

    private static string DetermineReasonCode(
        SearchMode mode,
        SearchIntent intent,
        string lower,
        SearchSession? session)
    {
        if (intent == SearchIntent.ArticleDeepDive &&
            session?.LastMode == SearchMode.NewsAggregate)
            return RouteReasons.FollowUpStory;

        if (mode == SearchMode.FollowUp)
            return RouteReasons.SessionFollowUp;

        if (intent == SearchIntent.LocalBusinessLookup)
            return RouteReasons.LocalBusinessMatch;

        if (intent == SearchIntent.NewsHeadlines)
            return RouteReasons.HeadlinePhrase;

        if (intent == SearchIntent.TopicNews)
            return RouteReasons.TopicNewsPhrase;

        return RouteReasons.FactFindFallback;
    }

    private static double ComputeConfidence(
        SearchMode mode,
        SearchIntent intent,
        string lower,
        SearchSession? session)
    {
        // High confidence when the router found strong signal.
        if (mode == SearchMode.NewsAggregate)
            return 0.9;

        if (mode == SearchMode.FollowUp && session?.HasRecentResults(DateTimeOffset.UtcNow) == true)
            return 0.85;

        if (intent == SearchIntent.LocalBusinessLookup)
            return 0.85;

        // WebFactFind is the fallback bucket — moderate confidence.
        return 0.6;
    }
}
