namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Search Intent Patterns — Canonical Trigger Lists
//
// Single source of truth for news intent detection across the search
// stack. Both WebSearchRouter and WebSearchTools reference these arrays
// instead of maintaining their own duplicated copies.
// ─────────────────────────────────────────────────────────────────────────

public static class SearchIntentPatterns
{
    /// <summary>
    /// Phrases that indicate "show me aggregated news" — the user wants
    /// recent, multi-source coverage rather than a factual reference.
    /// Used by both the provider router and the MCP tool formatter.
    /// </summary>
    public static readonly string[] NewsIntentTriggers =
    [
        "news",
        "headlines",
        "top headlines",
        "top stories",
        "breaking",
        "current events",
        "what's happening",
        "whats happening",
        "week in review",
        "month in review",
        "daily briefing",
        "news this week",
        "news last week",
        "latest news",
        "recent news",
        "news feed",
        "what happened",
        "what's going on",
        "whats going on"
    ];

    /// <summary>
    /// Generic queries that return better results from a top-headlines
    /// feed than from a search endpoint. Used by GoogleNewsRssProvider
    /// to choose between headlines and search feeds.
    /// </summary>
    public static readonly string[] HeadlineTriggers =
    [
        "news", "headlines", "current events", "what's happening",
        "today", "breaking", "top stories", "latest", "recent news",
        "whats going on", "what is going on", "whats new", "news feed"
    ];

    /// <summary>
    /// Returns true if the query looks like a news-aggregation intent.
    /// </summary>
    public static bool LooksLikeNewsIntent(string query)
    {
        var lower = (query ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        foreach (var trigger in NewsIntentTriggers)
        {
            if (lower.Contains(trigger, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the query is a generic "show me the news" query
    /// with no specific topic.
    /// </summary>
    public static bool IsGenericHeadlineQuery(string query)
    {
        var normalized = (query ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        foreach (var trigger in HeadlineTriggers)
        {
            if (normalized.Equals(trigger, StringComparison.Ordinal) ||
                normalized.Contains(trigger, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
