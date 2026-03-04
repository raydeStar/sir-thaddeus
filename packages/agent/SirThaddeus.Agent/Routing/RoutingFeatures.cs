namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Deterministic signals extracted from the user message before the
/// Footman router runs. These are cheap boolean features computed by
/// <see cref="IntentFeatureExtractor"/> and contextual state flags
/// from the orchestrator. The Footman receives a compact text summary
/// of these features — never the full chat history or tool menu.
/// </summary>
public sealed record RoutingFeatures
{
    // ── Heuristic signals from IntentFeatureExtractor ─────────────────

    public bool IsGreeting { get; init; }
    public bool IsLogicPuzzle { get; init; }
    public bool IsReasoningFollowUp { get; init; }
    public bool IsSearchFollowUp { get; init; }
    public bool LooksLikeFactLookup { get; init; }
    public bool LooksLikeNewsLookup { get; init; }
    public bool LooksLikeDeepDive { get; init; }
    public bool LooksLikeLocalBusiness { get; init; }
    public bool LooksLikeScreenRequest { get; init; }
    public bool LooksLikeFileRequest { get; init; }
    public bool LooksLikeSystemCommand { get; init; }
    public bool LooksLikeBrowseRequest { get; init; }
    public bool LooksLikeMemoryWrite { get; init; }
    public bool LooksLikeWebSearch { get; init; }
    public double WebLookupScore { get; init; }
    public string WebLookupReasonCode { get; init; } = "";

    // ── Contextual state from the orchestrator ────────────────────────

    /// <summary>Whether the user recently received a first-principles rationale.</summary>
    public bool HasRecentRationale { get; init; }

    /// <summary>Whether the search session has recent results.</summary>
    public bool HasRecentSearchResults { get; init; }

    /// <summary>Approximate word count of the user message.</summary>
    public int WordCount { get; init; }

    /// <summary>Whether the message contains a question mark.</summary>
    public bool HasQuestionMark { get; init; }

    /// <summary>Whether the message starts with a slash command.</summary>
    public bool IsSlashCommand { get; init; }

    // ── Factory ───────────────────────────────────────────────────────

    /// <summary>
    /// Extracts all deterministic features from a user message and
    /// contextual orchestrator state.
    /// </summary>
    public static RoutingFeatures Extract(
        string userMessage,
        bool hasRecentRationale = false,
        bool hasRecentSearchResults = false)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();

        var webEvidence = IntentFeatureExtractor.GetWebLookupHeuristicEvidence(lower);

        return new RoutingFeatures
        {
            IsGreeting              = IntentFeatureExtractor.LooksLikeGreeting(lower),
            IsLogicPuzzle           = IntentFeatureExtractor.LooksLikeLogicPuzzlePrompt(lower),
            IsReasoningFollowUp     = IntentFeatureExtractor.LooksLikeReasoningFollowUp(lower),
            IsSearchFollowUp        = Search.SearchModeRouter.IsFollowUpMessage(lower),
            LooksLikeFactLookup     = IntentFeatureExtractor.LooksLikeFactLookup(lower),
            LooksLikeNewsLookup     = IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower),
            LooksLikeDeepDive       = IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower),
            LooksLikeLocalBusiness  = IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lower),
            LooksLikeScreenRequest  = IntentFeatureExtractor.LooksLikeScreenRequest(lower),
            LooksLikeFileRequest    = IntentFeatureExtractor.LooksLikeFileRequest(lower),
            LooksLikeSystemCommand  = IntentFeatureExtractor.LooksLikeSystemCommand(lower),
            LooksLikeBrowseRequest  = IntentFeatureExtractor.LooksLikeBrowseRequest(lower),
            LooksLikeMemoryWrite    = IntentFeatureExtractor.LooksLikeMemoryWriteRequest(lower),
            LooksLikeWebSearch      = webEvidence.ShouldLookup,
            WebLookupScore          = webEvidence.Score,
            WebLookupReasonCode     = webEvidence.ReasonCode,
            HasRecentRationale      = hasRecentRationale,
            HasRecentSearchResults  = hasRecentSearchResults,
            WordCount               = CountWords(userMessage),
            HasQuestionMark         = lower.Contains('?'),
            IsSlashCommand          = lower.StartsWith('/')
        };
    }

    /// <summary>
    /// Builds a compact text summary of the active features for
    /// inclusion in the Footman system prompt. Only truthy features
    /// are listed to minimise token count.
    /// </summary>
    public string ToPromptSummary()
    {
        var signals = new List<string>(16);

        if (IsGreeting)             signals.Add("greeting");
        if (IsLogicPuzzle)          signals.Add("logic_puzzle");
        if (IsReasoningFollowUp)    signals.Add("reasoning_followup");
        if (IsSearchFollowUp)       signals.Add("search_followup");
        if (LooksLikeFactLookup)    signals.Add("fact_lookup");
        if (LooksLikeNewsLookup)    signals.Add("news_lookup");
        if (LooksLikeDeepDive)      signals.Add("deep_dive");
        if (LooksLikeLocalBusiness) signals.Add("local_business");
        if (LooksLikeScreenRequest) signals.Add("screen_request");
        if (LooksLikeFileRequest)   signals.Add("file_request");
        if (LooksLikeSystemCommand) signals.Add("system_command");
        if (LooksLikeBrowseRequest) signals.Add("browse_request");
        if (LooksLikeMemoryWrite)   signals.Add("memory_write");
        if (LooksLikeWebSearch)     signals.Add("web_search");
        if (WebLookupScore > 0.0)
            signals.Add($"web_score={WebLookupScore:0.0}");
        if (!string.IsNullOrWhiteSpace(WebLookupReasonCode) &&
            !WebLookupReasonCode.Equals("none", StringComparison.Ordinal))
        {
            signals.Add($"web_reason={WebLookupReasonCode}");
        }
        if (HasRecentRationale)     signals.Add("has_recent_rationale");
        if (HasRecentSearchResults) signals.Add("has_recent_search");
        if (HasQuestionMark)        signals.Add("has_question_mark");
        if (IsSlashCommand)         signals.Add("slash_command");

        return signals.Count == 0
            ? "signals: none"
            : $"signals: [{string.Join(", ", signals)}] | words: {WordCount}";
    }

    private static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        var inWord = false;
        foreach (var c in text.AsSpan())
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }
}
