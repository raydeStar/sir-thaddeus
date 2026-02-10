namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Search Mode Router — Deterministic Intent Classification
//
// Pure heuristic classifier. No LLM call. Maps user messages to one of
// three pipeline modes: NEWS_AGGREGATE, WEB_FACTFIND, or FOLLOW_UP.
//
// Classification order:
//   1. FOLLOW_UP — if referential + session has recent results
//   2. NEWS_AGGREGATE — if news intent triggers match
//   3. WEB_FACTFIND — everything else
//
// Follow-up sub-classification (DeepDive vs MoreSources) is also here.
// ─────────────────────────────────────────────────────────────────────────

public static class SearchModeRouter
{
    // ── News intent triggers ─────────────────────────────────────────
    // Phrases that signal "show me aggregated news" vs "find me a fact."
    private static readonly string[] NewsTriggers =
    [
        "news", "headlines", "top headlines", "top stories",
        "breaking", "current events", "what's happening",
        "whats happening", "week in review", "month in review",
        "daily briefing", "news this week", "news last week",
        "latest news", "recent news", "news feed",
        "what happened", "what's going on", "whats going on"
    ];

    // ── Fact-find triggers ───────────────────────────────────────────
    // These suggest an encyclopedic / reference query, not news.
    private static readonly string[] FactFindTriggers =
    [
        "who is", "who was", "what is", "what was",
        "tell me about", "background on", "history of",
        "explain", "define", "biography", "overview of"
    ];

    // ── Follow-up depth phrases ──────────────────────────────────────
    private static readonly string[] FollowUpPhrases =
    [
        "tell me more", "more info", "more information",
        "more detail", "more details", "more about",
        "go deeper", "dig into", "elaborate",
        "expand on", "continue", "keep going",
        "what else", "anything else"
    ];

    // ── "More sources" phrases (triggers MoreSources branch) ─────────
    private static readonly string[] MoreSourcesPhrases =
    [
        "more sources", "other sources", "other coverage",
        "related articles", "find more", "other perspectives",
        "different sources", "additional coverage",
        "more coverage", "other reports"
    ];

    // ── Referential markers (points at prior context) ────────────────
    private static readonly string[] ReferentialMarkers =
    [
        "this ", "that ", "it ", "these ", "those ",
        "the first", "the second", "the third",
        "that one", "this one"
    ];

    /// <summary>
    /// Classifies a user message into a search pipeline mode.
    /// Purely deterministic — no LLM calls.
    /// </summary>
    public static SearchMode Classify(string userMessage, SearchSession session, DateTimeOffset now)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return SearchMode.WebFactFind;

        // ── 1. FOLLOW_UP: referential message + recent results ───────
        if (session.HasRecentResults(now) && IsFollowUpMessage(lower))
            return SearchMode.FollowUp;

        // ── 2. NEWS_AGGREGATE: news-intent triggers ──────────────────
        if (LooksLikeNewsIntent(lower))
            return SearchMode.NewsAggregate;

        // ── 3. Tie-break: referential but no session → WEB_FACTFIND
        //    (the user is asking about something new, not following up)
        return SearchMode.WebFactFind;
    }

    /// <summary>
    /// Sub-classifies a FOLLOW_UP message into DeepDive or MoreSources.
    /// </summary>
    public static FollowUpBranch ClassifyFollowUpBranch(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();

        foreach (var phrase in MoreSourcesPhrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal))
                return FollowUpBranch.MoreSources;
        }

        return FollowUpBranch.DeepDive;
    }

    /// <summary>
    /// Returns true if the message looks like a follow-up request
    /// (asks for depth, expansion, or continuation of prior content).
    /// </summary>
    public static bool IsFollowUpMessage(string lowerMessage)
    {
        // Check direct follow-up phrases
        foreach (var phrase in FollowUpPhrases)
        {
            if (lowerMessage.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

        // Short + starts with "more " is a strong follow-up signal
        if (lowerMessage.StartsWith("more ", StringComparison.Ordinal) &&
            lowerMessage.Length < 60)
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if the message has news-aggregation intent.
    /// </summary>
    public static bool LooksLikeNewsIntent(string lowerMessage)
    {
        foreach (var trigger in NewsTriggers)
        {
            if (lowerMessage.Contains(trigger, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the message has fact-find intent
    /// (encyclopedic, reference, background).
    /// </summary>
    public static bool LooksLikeFactFindIntent(string lowerMessage)
    {
        foreach (var trigger in FactFindTriggers)
        {
            if (lowerMessage.Contains(trigger, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the message references prior context
    /// (pronouns, ordinals, demonstratives).
    /// </summary>
    public static bool IsReferential(string lowerMessage)
    {
        foreach (var marker in ReferentialMarkers)
        {
            if (lowerMessage.Contains(marker, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
