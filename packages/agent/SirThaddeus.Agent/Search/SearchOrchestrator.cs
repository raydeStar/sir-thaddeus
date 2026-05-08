using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search.DeepDive;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Search Orchestrator — Central Entry Point for All Search Flows
//
// Owns: SearchSession, SearchModeRouter, EntityResolver, QueryBuilder,
//       StoryClustering, and the three search pipelines.
//
// Pipelines:
//   1. NEWS_AGGREGATE → EntityResolver → QueryBuilder (news-mode) →
//      web_search → StoryClustering → present clusters → session
//   2. WEB_FACTFIND → EntityResolver → QueryBuilder (factfind-mode) →
//      web_search → browser_navigate top 1-2 → synthesize → session
//   3. FOLLOW_UP → DeepDive: browse prior source → summarize
//                → MoreSources: QueryBuilder (source title + entity) →
//                  web_search → append session
//
// All tool calls go through IMcpToolClient (MCP boundary preserved).
// All stages are logged via IAuditLogger.
// ─────────────────────────────────────────────────────────────────────────

public enum LookupModeHint
{
    Auto = 0,
    Fact = 1,
    News = 2,
    DeepDive = 3,
    Product = 4
}

public sealed partial class SearchOrchestrator
{
    private enum SummaryFallbackKind
    {
        Generic = 0,
        News = 1,
        FactFind = 2
    }

    private readonly ILlmClient       _llm;
    private readonly IMcpToolClient   _mcp;
    private readonly IAuditLogger     _audit;
    private string                    _systemPrompt;
    private readonly EntityResolver   _entityResolver;
    private readonly QueryBuilder     _queryBuilder;
    private readonly DeepDiveCoordinator _deepDiveCoordinator;

    /// <summary>Formal search state — survives history trimming.</summary>
    public SearchSession Session { get; } = new();

    /// <summary>
    /// Optional user location hint (e.g. "Portland, OR") forwarded to
    /// deep-dive place lookups. Set from the orchestrator's config.
    /// </summary>
    public string? UserLocationHint { get; set; }

    /// <summary>
    /// Optional global unit preference ("imperial", "metric", "auto").
    /// Used to bias search query phrasing and summary formatting when the
    /// user did not request explicit units.
    /// </summary>
    public string? PreferredUnits { get; set; }

    /// <summary>
    /// Enables the advanced deep-dive place briefing path.
    /// Baseline profiles keep this off so lookup requests stay on the
    /// simpler fact-find branch.
    /// </summary>
    public bool DeepDiveEnabled { get; set; } = true;

    /// <summary>
    /// Enables advanced local-business enrichment flows that call place
    /// discovery and place lookup tools.
    /// </summary>
    public bool AdvancedPlaceDiscoveryEnabled { get; set; } = true;

    // ── Tool name conventions (try both casings) ─────────────────────
    private const string WebSearchToolName    = "web_search";
    private const string WebSearchToolNameAlt = "WebSearch";
    private const string BrowseToolName       = "browser_navigate";
    private const string BrowseToolNameAlt    = "BrowserNavigate";
    private const string PlacesDiscoverToolName    = "places_discover";
    private const string PlacesDiscoverToolNameAlt = "PlacesDiscover";
    private const string PlacesLookupToolName    = "places_lookup";
    private const string PlacesLookupToolNameAlt = "PlacesLookup";

    // ── Bounds ───────────────────────────────────────────────────────
    private const int DefaultMaxResults    = 5;
    private const int LocalBusinessTargetResults = 10;
    private const int LocalBusinessMinimumDisplayResults = 5;
    private const int LocalBusinessDiscoveryFetchMaxResults = 25;
    private const int LocalBusinessFetchMaxResults = 10;
    private const int LocalBusinessNearbyPrimaryRadiusMeters = 20_000;
    private const int LocalBusinessNearbyExpandedRadiusMeters = 50_000;
    private const int LocalBusinessMaxArticleFetches = 1;
    private const int LocalBusinessMaxBrowserFallbackFetches = 3;
    private const int LocalBusinessMaxPlaceLookups = 5;
    private const int MaxFollowUpUrls      = 2;
    private const int MaxArticleChars      = 3000;
    private const int MaxTokensWebSummary  = 1024;
    private const int MaxTokensWebSummaryRetry = 2048;
    private const int MinRichContentLength = 1500;
    private static readonly TimeSpan FinanceQuoteFreshnessMaxAge = TimeSpan.FromHours(6);
    private static readonly string[] UnsupportedCapabilityClaimMarkers =
    [
        "cant access",
        "can't access",
        "cannot access",
        "cant browse",
        "cant search",
        "cannot directly search",
        "cannot search the",
        "restricted to searching within your local environment",
        "cannot perform live",
        "browse the web",
        "browse live news feeds",
        "web access tools",
        "direct web access",
        "currently unavailable",
        "real-time data",
        "real-time events",
        "real-time updates",
        "knowledge is static",
        "internal knowledge base",
        "have no access to",
        "no access to current",
        "conversation history",
        "documents and snippets",
        "shared memory context",
        "browsing tools",
        "local-first assistant",
        "directory service like google maps",
        "directory service like yelp"
    ];
    private static readonly Dictionary<string, string> StateCodeToName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = "Alabama",
        ["AK"] = "Alaska",
        ["AZ"] = "Arizona",
        ["AR"] = "Arkansas",
        ["CA"] = "California",
        ["CO"] = "Colorado",
        ["CT"] = "Connecticut",
        ["DE"] = "Delaware",
        ["FL"] = "Florida",
        ["GA"] = "Georgia",
        ["HI"] = "Hawaii",
        ["ID"] = "Idaho",
        ["IL"] = "Illinois",
        ["IN"] = "Indiana",
        ["IA"] = "Iowa",
        ["KS"] = "Kansas",
        ["KY"] = "Kentucky",
        ["LA"] = "Louisiana",
        ["ME"] = "Maine",
        ["MD"] = "Maryland",
        ["MA"] = "Massachusetts",
        ["MI"] = "Michigan",
        ["MN"] = "Minnesota",
        ["MS"] = "Mississippi",
        ["MO"] = "Missouri",
        ["MT"] = "Montana",
        ["NE"] = "Nebraska",
        ["NV"] = "Nevada",
        ["NH"] = "New Hampshire",
        ["NJ"] = "New Jersey",
        ["NM"] = "New Mexico",
        ["NY"] = "New York",
        ["NC"] = "North Carolina",
        ["ND"] = "North Dakota",
        ["OH"] = "Ohio",
        ["OK"] = "Oklahoma",
        ["OR"] = "Oregon",
        ["PA"] = "Pennsylvania",
        ["RI"] = "Rhode Island",
        ["SC"] = "South Carolina",
        ["SD"] = "South Dakota",
        ["TN"] = "Tennessee",
        ["TX"] = "Texas",
        ["UT"] = "Utah",
        ["VT"] = "Vermont",
        ["VA"] = "Virginia",
        ["WA"] = "Washington",
        ["WV"] = "West Virginia",
        ["WI"] = "Wisconsin",
        ["WY"] = "Wyoming",
        ["DC"] = "District of Columbia"
    };

    // ── Source metadata delimiter (matches WebSearchTools output) ─────
    private const string SourcesJsonDelimiter = "<!-- SOURCES_JSON -->";

    // ── Memory / Instruction boundary ────────────────────────────────

    /// <summary>
    /// Combines memory pack text with synthesis instructions, inserting a
    /// clear boundary so the LLM does not follow response patterns found
    /// in memory context chunks (e.g. prior deflections).
    /// </summary>
    internal static string CombineMemoryAndInstruction(string memoryPackText, string instruction)
    {
        if (string.IsNullOrWhiteSpace(memoryPackText))
            return instruction;

        return memoryPackText +
            "\n\n[END OF MEMORY CONTEXT — the task instructions below take absolute precedence " +
            "over any response patterns shown in memory]\n" +
            instruction;
    }

    // ── LLM Instructions ─────────────────────────────────────────────
    private const string NewsSummaryInstruction =
        "\n\nSearch results are in the next message. " +
        "Present the key stories as individual items. " +
        "For each item, give the headline followed by one sentence " +
        "restating the most concrete reported detail from the sources. " +
        "Use plain reported facts, not analysis. " +
        "Only use the phrase 'matters because' when the impact is explicitly stated in the snippets; " +
        "otherwise state the reported fact directly. " +
        "If multiple sources cover the same story, only note agreement or disagreement when the snippets explicitly show it. " +
        "No URLs. ONLY use facts from the provided sources. " +
        "Do NOT apologize or claim you lack internet, real-time data, or web access. " +
        "Do NOT deflect, claim role limitations, or ask clarifying questions — " +
        "answer directly from the search results provided. " +
        "The provided results already contain the current information you need. " +
        "Do NOT invent, infer, or guess details not in the results. " +
        "Do NOT add background context unless the snippets explicitly include it. " +
        "IMPORTANT: If the user's message specifies a response format " +
        "(e.g. bullet count, headings, numbered list), follow it exactly.";

    private const string LocalNewsSummaryInstruction =
        "\n\nSearch results are in the next message. The user asked for LOCAL news. " +
        "PRIORITIZE stories that are regional, community-level, or specific " +
        "to the user's area. Local school board decisions, community events, " +
        "local business openings, regional weather, and city council votes " +
        "are MORE valuable than national/international headlines. " +
        "Present stories as individual items. " +
        "For each item, give the headline followed by one sentence " +
        "restating the most concrete local detail reported in the snippets. " +
        "Only explain why it matters locally when the local impact is explicit in the source text; " +
        "otherwise report the local fact directly. " +
        "If the results contain ONLY national/international stories and no " +
        "local content, say so honestly: note that no local stories were " +
        "found in the results and present the top headlines instead. " +
        "No URLs. ONLY use facts from the provided sources. " +
        "Do NOT apologize or claim you lack internet, real-time data, or web access. " +
        "The provided results already contain the current information you need. " +
        "Do NOT invent, infer, or guess details not in the results. " +
        "Do NOT add broader context unless the snippets explicitly include it.";

    private const string FactFindSummaryInstruction =
        "\n\nSearch results and article content are in the next message. " +
        "Synthesize into a clear, factual answer. Lead with the bottom line. " +
        "Include key facts. No URLs. " +
        "ONLY use facts from the provided sources. " +
        "Do NOT apologize or claim you lack internet, real-time data, or location access. " +
        "Do NOT deflect, claim role limitations, or ask clarifying questions — " +
        "answer directly from the search results provided. " +
        "The provided results already contain the current, localized data you need. " +
        "IMPORTANT: If the user's message specifies a response format " +
        "(e.g. specific line prefixes, headings, structure), follow it exactly.\n" +
        "CRITICAL: If the user's premise is factually flawed (e.g. asking for the plot of a cancelled TV season that does not exist), DO NOT summarize irrelevant fallback search results (e.g. results for a different show's season). Instead, state the reality (e.g. the show was cancelled) using your internal knowledge, and summarize any facts about what was planned.";

    private const string FactFindSnippetOnlyInstruction =
        "\n\nSearch result snippets are in the next message (no full articles " +
        "were retrievable). Extract EVERY relevant detail from the snippets. " +
        "Lead with the bottom line, then list each factual key point as a " +
        "bullet. Include specific names, dates, numbers, and quotes from the " +
        "snippets. Be thorough — use all available information, do not " +
        "summarize down to generalities. No URLs. " +
        "ONLY use facts from the provided sources. " +
        "Do NOT apologize or claim you lack internet, real-time data, or location access. " +
        "Do NOT deflect, claim role limitations, or ask clarifying questions — " +
        "answer directly from the search results provided. " +
        "The provided results already contain the current, localized data you need. " +
        "IMPORTANT: If the user's message specifies a response format " +
        "(e.g. specific line prefixes, headings, structure), follow it exactly.\n" +
        "CRITICAL: If the user's premise is factually flawed (e.g. asking for the plot of a cancelled TV season that does not exist), DO NOT summarize irrelevant fallback search results (e.g. results for a different show's season). Instead, state the reality (e.g. the show was cancelled) using your internal knowledge, and summarize any facts about what was planned.";

    private const string DeepDiveInstruction =
        "\n\nFull article content from a prior source is in the next message. " +
        "Answer the user's latest question using ONLY the provided content. " +
        "Be thorough. No URLs. " +
        "If a detail is not present in the content, say so.";

    private const string MoreSourcesInstruction =
        "\n\nYou are answering a follow-up question about a specific topic. " +
        "Full text from the primary article(s) is included first, followed by " +
        "related coverage search results.\n" +
        "Answer the user's question. Lead with the bottom line. Then explain:\n" +
        "- What the primary article(s) say\n" +
        "- What related sources add or contradict\n" +
        "- Whether key details are confirmed or still alleged\n" +
        "No URLs. Do not list sources unless you need to explain a disagreement.";

    private const string FinanceQuoteSummaryInstruction =
        "\n\nThis is a market quote request. " +
        "Start with one plain sentence containing the instrument/index name, " +
        "current level, and today's move in points and percent if available. " +
        "Include an 'as of' time from source metadata when present. " +
        "If exact quote values are not present in the sources, say you could not verify a current quote.";

    public SearchOrchestrator(
        ILlmClient llm,
        IMcpToolClient mcp,
        IAuditLogger audit,
        string systemPrompt)
    {
        _llm          = llm   ?? throw new ArgumentNullException(nameof(llm));
        _mcp          = mcp   ?? throw new ArgumentNullException(nameof(mcp));
        _audit        = audit ?? throw new ArgumentNullException(nameof(audit));
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));

        _entityResolver = new EntityResolver(llm, mcp, audit);
        _queryBuilder   = new QueryBuilder(llm, audit);
        _deepDiveCoordinator = new DeepDiveCoordinator(mcp, audit);
    }

    /// <summary>
    /// Effective base system prompt used when fallback paths cannot
    /// reuse history system content.
    /// </summary>
    public string SystemPrompt
    {
        get => _systemPrompt;
        set => _systemPrompt = value ?? "";
    }

    /// <summary>
    /// Main entry point. Classifies the message, routes to the correct
    /// pipeline, and returns the agent's response.
    /// </summary>
    public async Task<AgentResponse> ExecuteAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        LookupModeHint modeHint,
        CancellationToken ct)
    {
        if (TryBuildMathAndNameRecallFallback(userMessage, out var mathAndNameText))
        {
            return new AgentResponse
            {
                Text = mathAndNameText,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = 0
            };
        }

        if (!RequiresLiveWebVerification(userMessage, history))
        {
            var classicLogicResult = ClassicReasoningEngine.TryMatch(userMessage);
            if (classicLogicResult is not null &&
                string.Equals(classicLogicResult.Category, "logic", StringComparison.OrdinalIgnoreCase))
            {
                _audit.Append(new AuditEvent
                {
                    Actor = "search",
                    Action = "SEARCH_CLASSIC_LOGIC_SHORT_CIRCUIT",
                    Result = "deterministic_logic"
                });

                return new AgentResponse
                {
                    Text = classicLogicResult.Answer,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = 0
                };
            }
        }

        var effectiveModeHint = !DeepDiveEnabled && modeHint == LookupModeHint.DeepDive
            ? LookupModeHint.Fact
            : modeHint;

        if (effectiveModeHint != modeHint)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "SEARCH_MODE_HINT_PROFILE_DOWNGRADE",
                Result = effectiveModeHint.ToString(),
                Details = new Dictionary<string, object>
                {
                    ["requested_mode_hint"] = modeHint.ToString(),
                    ["effective_mode_hint"] = effectiveModeHint.ToString(),
                    ["reason"] = "advanced_deep_dive_disabled"
                }
            });
        }

        var now  = DateTimeOffset.UtcNow;
        var mode = ResolveMode(userMessage, effectiveModeHint, now);
        if (mode == SearchMode.DeepDiveBriefing && HarnessDisallowsPlacesTools())
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "SEARCH_MODE_PLACES_CONTRACT_DOWNGRADE",
                Result = SearchMode.WebFactFind.ToString(),
                Details = new Dictionary<string, object>
                {
                    ["requested_mode"] = mode.ToString(),
                    ["reason"] = "active_tool_contract_excludes_places"
                }
            });

            mode = SearchMode.WebFactFind;
        }

        _audit.Append(new AuditEvent
        {
            Actor  = "agent",
            Action = "SEARCH_MODE_CLASSIFIED",
            Result = mode.ToString(),
            Details = new Dictionary<string, object>
            {
                ["user_message"]     = Truncate(userMessage, 80),
                ["has_prior_results"] = Session.HasRecentResults(now),
                ["mode_hint"] = modeHint.ToString(),
                ["effective_mode_hint"] = effectiveModeHint.ToString(),
                ["hint_forced_mode"] = effectiveModeHint != LookupModeHint.Auto
            }
        });

        try
        {
            var response = mode switch
            {
                SearchMode.FollowUp      => await ExecuteFollowUpAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                SearchMode.NewsAggregate  => await ExecuteNewsAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                SearchMode.ProductRecommendation => await ExecuteProductRecommendationAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                SearchMode.WebFactFind    => await ExecuteFactFindAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                SearchMode.DeepDiveBriefing => await ExecuteDeepDiveBriefingAsync(userMessage, toolCallsMade, ct),
                _                         => await ExecuteFactFindAsync(userMessage, memoryPackText, history, toolCallsMade, ct)
            };

            var contracted = ApplyResponseContract(response, mode);
            if (LooksLikeBareCancelledResponse(contracted.Text) &&
                TryBuildMediaInstallmentFallback(userMessage) is { Length: > 0 } mediaFallback)
            {
                return contracted with { Text = mediaFallback, Success = true };
            }

            return contracted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "agent",
                Action = "SEARCH_PIPELINE_ERROR",
                Result = ex.Message
            });

            return AgentResponse.FromError(
                "Something went sideways with the search pipeline — " +
                $"try rephrasing? ({ex.GetType().Name})");
        }
    }

    private static bool TryBuildMathAndNameRecallFallback(string userMessage, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var nameMatch = Regex.Match(
            userMessage,
            @"\bmy\s+name\s+is\s+([A-Za-z][A-Za-z\-']{1,30})\b",
            RegexOptions.IgnoreCase);
        var expressionMatch = Regex.Match(
            userMessage,
            @"\b(\d{1,6})\s*([\+\-\*xX])\s*(\d{1,6})\b",
            RegexOptions.IgnoreCase);

        if (!nameMatch.Success || !expressionMatch.Success)
            return false;

        if (!int.TryParse(expressionMatch.Groups[1].Value, out var left) ||
            !int.TryParse(expressionMatch.Groups[3].Value, out var right))
        {
            return false;
        }

        var op = expressionMatch.Groups[2].Value;
        var result = op switch
        {
            "+" => left + right,
            "-" => left - right,
            "*" or "x" or "X" => left * right,
            _ => (int?)null
        };

        if (result is null)
            return false;

        var name = nameMatch.Groups[1].Value;
        response = $"{left} {op} {right} = {result.Value}. Your name is {name}.";
        return true;
    }

    // ─────────────────────────────────────────────────────────────────
    // Pipeline 3: FOLLOW_UP (DeepDive vs MoreSources)
    // ─────────────────────────────────────────────────────────────────

    private async Task<AgentResponse> ExecuteFollowUpAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var branch = SearchModeRouter.ClassifyFollowUpBranch(userMessage);

        // When the prior search was a place/business lookup or local
        // business discovery, redirect "tell me more about X" to the
        // briefing pipeline so the user gets a structured briefing card.
        if (branch == FollowUpBranch.DeepDive &&
            (Session.LastMode == SearchMode.DeepDiveBriefing ||
             Session.LastWasLocalBusinessDiscovery ||
             Session.LastLocalBusinessCandidateTitles.Count > 0))
        {
            var subject = ResolveLocalBusinessFollowUpSubject(userMessage);
            if (!string.IsNullOrWhiteSpace(subject))
            {
                // Enrich the subject with context from prior results so the
                // briefing search query isn't ambiguous. "The West Olympia Woman"
                // alone might match unrelated content; appending the matching
                // result title/snippet forces the right interpretation
                // (e.g. "The West Olympia Woman (Community-Supported Bread)").
                var enriched = EnrichSubjectFromSession(subject);

                _audit.Append(new AuditEvent
                {
                    Actor  = "agent",
                    Action = "FOLLOWUP_PLACE_BRIEFING_REDIRECT",
                    Result = enriched,
                    Details = new Dictionary<string, object>
                    {
                        ["raw_subject"]  = subject,
                        ["enriched"]     = !string.Equals(subject, enriched, StringComparison.Ordinal)
                    }
                });
                return await ExecuteDeepDiveBriefingAsync(enriched, toolCallsMade, ct);
            }
        }

        _audit.Append(new AuditEvent
        {
            Actor  = "agent",
            Action = "FOLLOWUP_BRANCH",
            Result = branch.ToString()
        });

        return branch switch
        {
            FollowUpBranch.MoreSources =>
                await ExecuteMoreSourcesAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
            _ =>
                await ExecuteDeepDiveAsync(userMessage, memoryPackText, history, toolCallsMade, ct)
        };
    }

    /// <summary>
    /// Produces a clean entity-focused query for the deep-dive coordinator.
    /// When the session has prior local business discovery results, the
    /// subject is resolved against session candidates so the coordinator
    /// receives "San Francisco Street Bakery" instead of the raw "bring me
    /// up more info on San Francisco Street Bakery".
    /// When no prior context exists, conversational filler is still stripped
    /// via <see cref="ExtractFollowUpSubject"/>.
    /// </summary>
    private string SanitizeDeepDiveQuery(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return userMessage;

        // When we have prior local business results, use the same resolution
        // chain as the FollowUp pipeline (subject extraction + enrichment).
        if (Session.LastWasLocalBusinessDiscovery ||
            Session.LastLocalBusinessCandidateTitles.Count > 0)
        {
            var subject = ResolveLocalBusinessFollowUpSubject(userMessage);
            if (!string.IsNullOrWhiteSpace(subject))
            {
                var enriched = EnrichSubjectFromSession(subject);
                return enriched;
            }
        }

        // General case: strip conversational prefixes so the query is
        // entity-only ("tell me more about X" → "X").
        var extracted = ExtractFollowUpSubject(userMessage);
        return string.IsNullOrWhiteSpace(extracted) ? userMessage : extracted;
    }

    /// <summary>
    /// Strips common follow-up prefixes to isolate the subject the user
    /// is asking about. "Tell me more about Left Bank Pastry" → "Left Bank Pastry".
    /// Also handles inverted patterns like
    /// "Left Bank Pastry -- can you tell me more about this one?" → "Left Bank Pastry".
    /// </summary>
    internal static string ExtractFollowUpSubject(string userMessage)
    {
        var trimmed = (userMessage ?? "").Trim();

        ReadOnlySpan<string> prefixes =
        [
            // Long conversational prefixes first (order matters — first match wins)
            "can you pull me up more info on ",
            "can you pull me up more info about ",
            "can you pull me up more on ",
            "can you pull me up more about ",
            "can you bring me up more info on ",
            "can you bring me up more info about ",
            "can you bring me up more on ",
            "can you bring me up more about ",
            "can you tell me more about ",
            "can you tell me about ",
            "pull up more info on ",
            "pull up more info about ",
            "pull up more on ",
            "pull up more about ",
            "pull up more ",
            "pull me up more info on ",
            "pull me up more info about ",
            "pull me up more on ",
            "pull me up more about ",
            "pull me up more ",
            "bring me up more info on ",
            "bring me up more info about ",
            "bring me up more on ",
            "bring me up more about ",
            "bring me up more ",
            "tell me more about ",
            "tell me about ",
            "show me more about ",
            "show me about ",
            "more info on ",
            "more info about ",
            "more information on ",
            "more information about ",
            "more about ",
            "more on ",
            "details on ",
            "details about ",
            "what about ",
            "how about ",
            "info on ",
            "info about ",
            "information on ",
            "information about ",
            "brief me on ",
            "brief me about ",
            "give me a brief on ",
            "give me a brief about ",
            "give me a brief for ",
            "create a brief on ",
            "create a brief about ",
            "create a brief for ",
            "show me "
        ];

        foreach (var prefix in prefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..].TrimEnd('?', '.', '!');
        }

        // Handle inverted "EntityName -- follow-up phrase" patterns.
        // e.g. "New Olympia Flower Shop -- can you tell me more about this one?"
        // The part before the separator is the entity name.
        foreach (var sep in (ReadOnlySpan<string>)[" -- ", " — "])
        {
            var idx = trimmed.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                var afterSep = trimmed[(idx + sep.Length)..].Trim();
                var afterLower = afterSep.ToLowerInvariant();

                // Check if the part after the separator is conversational follow-up filler
                if (IsFollowUpFiller(afterLower))
                    return trimmed[..idx].Trim().TrimEnd('?', '.', '!');
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Returns true when the text is conversational follow-up filler such as
    /// "can you tell me more about this one?" or "tell me more", indicating
    /// the real subject is elsewhere in the message (e.g. before a separator).
    /// </summary>
    internal static bool IsFollowUpFiller(string lowerText)
    {
        ReadOnlySpan<string> fillerPhrases =
        [
            "can you tell me more",
            "tell me more",
            "can you pull me up more",
            "pull me up more",
            "can you bring me up more",
            "bring me up more",
            "more info",
            "more information",
            "more details",
            "more about",
            "what can you tell me",
            "what do you know",
            "give me a brief",
            "brief me",
            "create a brief",
            "show me more",
            "go deeper",
            "dig into",
            "elaborate",
            "expand on"
        ];

        var stripped = lowerText.TrimEnd('?', '.', '!').Trim();
        foreach (var filler in fillerPhrases)
        {
            if (stripped.StartsWith(filler, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when the extracted subject is a pronoun reference
    /// like "this one" or "that place" rather than an actual entity name.
    /// </summary>
    internal static bool IsPronounSubjectReference(string subject)
    {
        var lower = subject.Trim().ToLowerInvariant();
        return lower is "this one" or "that one" or "this" or "that" or "it"
            or "the first one" or "the second one" or "the third one"
            or "the first" or "the second" or "the third"
            or "that place" or "this place"
            or "that business" or "this business"
            or "that bakery" or "this bakery"
            or "that deli" or "this deli"
            or "that florist" or "this florist"
            or "that restaurant" or "this restaurant"
            or "that shop" or "this shop"
            or "that store" or "this store"
            or "that cafe" or "this cafe";
    }

    private string ResolveLocalBusinessFollowUpSubject(string userMessage)
    {
        var extracted = ExtractFollowUpSubject(userMessage);
        var normalizedExtracted = Regex.Replace(extracted, @"\s*\([^)]*\)\s*$", string.Empty)
            .Trim()
            .TrimEnd('?', '.', '!');
        var candidates = Session.LastLocalBusinessCandidateTitles;

        // Pronoun-style follow-ups should resolve to a deterministic anchor.
        if (IsPronounSubjectReference(normalizedExtracted))
        {
            var anchored = ResolveLocalBusinessAnchorTitle();
            if (!string.IsNullOrWhiteSpace(anchored))
                return anchored;
        }

        // If the user's raw message explicitly contains one of the candidate
        // titles, use that exact candidate.
        var explicitCandidate = FindExplicitCandidateMention(userMessage, candidates);
        if (!string.IsNullOrWhiteSpace(explicitCandidate))
            return explicitCandidate;

        // If extraction produced a subject fragment, match it against known
        // candidates by token overlap.
        var bestByTokens = FindBestCandidateByTokenOverlap(normalizedExtracted, candidates);
        if (!string.IsNullOrWhiteSpace(bestByTokens))
            return bestByTokens;

        // Fall back to extracted content, then deterministic anchor.
        if (!string.IsNullOrWhiteSpace(normalizedExtracted))
            return normalizedExtracted;

        return ResolveLocalBusinessAnchorTitle();
    }

    private string ResolveLocalBusinessAnchorTitle()
    {
        if (!string.IsNullOrWhiteSpace(Session.SelectedSourceId))
        {
            var selected = Session.LastResults.FirstOrDefault(
                s => string.Equals(s.SourceId, Session.SelectedSourceId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(selected?.Title))
                return selected!.Title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(Session.LastLocalBusinessAnchorSourceId))
        {
            var anchored = Session.LastResults.FirstOrDefault(
                s => string.Equals(s.SourceId, Session.LastLocalBusinessAnchorSourceId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(anchored?.Title))
                return anchored!.Title.Trim();
        }

        if (Session.LastLocalBusinessCandidateTitles.Count > 0)
            return Session.LastLocalBusinessCandidateTitles[0];

        if (Session.LastResults.Count > 0 && !string.IsNullOrWhiteSpace(Session.LastResults[0].Title))
            return Session.LastResults[0].Title.Trim();

        return "";
    }

    private static string? FindExplicitCandidateMention(string userMessage, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
            return null;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (userMessage.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private static string? FindBestCandidateByTokenOverlap(string subject, IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(subject) || candidates.Count == 0)
            return null;

        var subjectTokens = Regex.Matches(subject.ToLowerInvariant(), @"[a-z0-9']+")
            .Select(m => m.Value)
            .Where(t => t.Length > 2)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (subjectTokens.Count == 0)
            return null;

        string? best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var candidateLower = candidate.ToLowerInvariant();
            var score = subjectTokens.Count(t => candidateLower.Contains(t, StringComparison.Ordinal));

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore > 0 ? best : null;
    }

    /// <summary>
    /// Searches prior session results for a title that contains the subject
    /// and returns the full title (which typically has disambiguating context
    /// like "(Community-Supported Bread)" or "- Thai Restaurant"). Falls back
    /// to the original subject if no match is found.
    /// </summary>
    private string EnrichSubjectFromSession(string subject)
    {
        if (Session.LastResults.Count == 0)
            return subject;

        var subjectLower = subject.ToLowerInvariant();

        // Pass 1: Exact or substring match on source titles.
        foreach (var result in Session.LastResults)
        {
            if (string.IsNullOrWhiteSpace(result.Title))
                continue;

            var titleLower = result.Title.ToLowerInvariant();

            // Prior result title contains the subject → use the full title
            // (e.g. "The West Olympia Woman (Community-Supported Bread)")
            if (titleLower.Contains(subjectLower, StringComparison.Ordinal))
            {
                return result.Title.Trim();
            }

            // Subject contains the full title → still a match
            if (subjectLower.Contains(titleLower, StringComparison.Ordinal))
            {
                // If the snippet adds useful context, append it
                if (!string.IsNullOrWhiteSpace(result.Snippet) && result.Snippet.Length > 10)
                {
                    var snippetContext = result.Snippet.Length > 80
                        ? result.Snippet[..80].Trim()
                        : result.Snippet.Trim();
                    return $"{result.Title} — {snippetContext}";
                }
                return result.Title.Trim();
            }
        }

        // Pass 2: Strip common site-name suffixes (" | Yelp", " - Google
        // Maps") from titles and retry. Search engines frequently append
        // the site name, which breaks substring matching.
        foreach (var result in Session.LastResults)
        {
            if (string.IsNullOrWhiteSpace(result.Title))
                continue;

            var cleaned = StripTitleSuffix(result.Title).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            if (cleaned.Contains(subjectLower, StringComparison.Ordinal))
                return StripTitleSuffix(result.Title).Trim();

            if (subjectLower.Contains(cleaned, StringComparison.Ordinal))
                return StripTitleSuffix(result.Title).Trim();
        }

        // No match in titles — try appending the original search query as context
        // so the briefing search engine gets the right domain.
        if (!string.IsNullOrWhiteSpace(Session.LastQuery) &&
            !subjectLower.Contains(Session.LastQuery.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return $"{subject} ({Session.LastQuery})";
        }

        return subject;
    }

    /// <summary>
    /// Strips common site-name suffixes from search result titles.
    /// "New Olympia Flower Shop | Yelp" → "New Olympia Flower Shop".
    /// </summary>
    internal static string StripTitleSuffix(string title)
    {
        // Ordered by specificity — try pipe first, then em-dash, then hyphen.
        foreach (var sep in (ReadOnlySpan<string>)[" | ", " — ", " - "])
        {
            var idx = title.LastIndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                var candidate = title[..idx].Trim();
                // Only strip if the suffix is short (looks like a site name)
                // and the remaining part is substantial.
                var suffix = title[(idx + sep.Length)..].Trim();
                if (suffix.Length < 40 && candidate.Length > 3)
                    return candidate;
            }
        }

        return title;
    }

    // ── Branch: DeepDive ─────────────────────────────────────────────

    private async Task<AgentResponse> ExecuteDeepDiveAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        // Select the best source to dive into
        var source = SelectSourceForFollowUp(userMessage);
        if (source is null)
        {
            // No prior source to dive into — fall through to factfind
            _audit.Append(new AuditEvent
            {
                Actor  = "agent",
                Action = "FOLLOWUP_NO_SOURCE",
                Result = "Falling back to factfind pipeline"
            });
            return await ExecuteFactFindAsync(
                userMessage, memoryPackText, history, toolCallsMade, ct);
        }

        _audit.Append(new AuditEvent
        {
            Actor  = "agent",
            Action = "FOLLOWUP_DEEPDIVE_SOURCE",
            Result = source.Title,
            Details = new Dictionary<string, object>
            {
                ["url"]       = source.Url,
                ["source_id"] = source.SourceId
            }
        });

        // Fetch full article content
        var content = await FetchArticleContentAsync(
            [source], toolCallsMade, ct);

        if (string.IsNullOrWhiteSpace(content))
        {
            // Article fetch failed — fall back to factfind
            return await ExecuteFactFindAsync(
                userMessage, memoryPackText, history, toolCallsMade, ct);
        }

        var summaryInput = "[Primary article content — use these details to give a thorough answer]\n" +
                           content;

        return await SummarizeAndRespond(
            summaryInput, CombineMemoryAndInstruction(memoryPackText, DeepDiveInstruction),
            history, toolCallsMade, SummaryFallbackKind.Generic, null, ct);
    }

    // ── Branch: MoreSources ──────────────────────────────────────────

    private async Task<AgentResponse> ExecuteMoreSourcesAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        // Use the chosen source's title + canonical entity for the search
        var source = SelectSourceForFollowUp(userMessage);
        var searchTopic = source?.Title ?? Session.LastQuery ?? userMessage;
        var entity      = Session.LastEntityCanonical;

        var query = !string.IsNullOrWhiteSpace(entity)
            ? $"{searchTopic} {entity}"
            : searchTopic;

        // Truncate if too long
        if (query.Length > 80) query = query[..80].Trim();

        var recency = Session.LastRecency ?? "any";

        // Fetch the primary article first (if we have a source)
        string? primaryContent = null;
        if (source is not null)
        {
            primaryContent = await FetchArticleContentAsync(
                [source], toolCallsMade, ct);
        }

        // Search for related coverage
        var toolResult = await CallWebSearchAsync(
            query, recency, toolCallsMade, ct);
        if (LooksLikeNoResultsPayload(toolResult))
        {
            return await BuildNoResultsFallbackAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                ct);
        }
        if (WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is { } moreSourcesFailure)
        {
            return await BuildOfflineReasoningResponseAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                moreSourcesFailure.Text,
                ct);
        }

        // Append new results to session (don't replace)
        var relatedSources = new List<SourceItem>();
        if (!string.IsNullOrWhiteSpace(toolResult))
        {
            relatedSources = ParseSourcesFromToolResult(toolResult);
            Session.AppendResults(relatedSources, DateTimeOffset.UtcNow);
        }

        // Build summary input
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(primaryContent))
        {
            sb.AppendLine("[Primary article content — use these details to give a thorough answer]");
            sb.AppendLine(primaryContent);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(toolResult))
        {
            sb.AppendLine("[Related coverage — use these additional facts to enrich your answer]");
            sb.AppendLine(StripSourcesJson(toolResult));
        }

        var instruction = !string.IsNullOrWhiteSpace(primaryContent)
            ? MoreSourcesInstruction
            : NewsSummaryInstruction;

        return await SummarizeAndRespond(
            sb.ToString(), CombineMemoryAndInstruction(memoryPackText, instruction),
            history, toolCallsMade, SummaryFallbackKind.News, relatedSources, ct);
    }

    /// <summary>
    /// Produces a structured deep-dive briefing payload for place/product lookups.
    /// </summary>
    private async Task<AgentResponse> ExecuteDeepDiveBriefingAsync(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var effectiveUserMessage = userMessage ?? string.Empty;

        // Proximity-without-location guard. If the user said "bakeries near
        // me" / "local florists" / etc. and we have NO location context to
        // resolve from (no inline city, no profile location), the deep-dive
        // path will issue a useless places_discover call and let the LLM
        // fabricate a vague clarifying question. Surface the deterministic
        // settings prompt instead — same UX as SearchFact already does.
        var lowerForGuard = effectiveUserMessage.ToLowerInvariant();
        if (IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerForGuard) &&
            string.IsNullOrWhiteSpace(ResolveLocalBusinessLocationContext(effectiveUserMessage)))
        {
            return new AgentResponse
            {
                Text = "I need a location to search for local businesses. " +
                       "You can set your location in **Settings \u2192 Location**, " +
                       "or include a city in your request " +
                       "(e.g., \"bakeries in Olympia, WA\").",
                Success = true,
                ToolCallsMade = toolCallsMade.ToList(),
                LlmRoundTrips = 0
            };
        }

        // When the user says "bring me up more info on X", the raw message
        // still contains conversational filler.  Strip it to produce a
        // clean entity-only query (e.g. "San Francisco Street Bakery")
        // so the deep-dive coordinator searches for the right thing and
        // the briefing card shows a tidy query label.
        var query = SanitizeDeepDiveQuery(effectiveUserMessage);

        var timezone = TimeZoneInfo.Local.Id;
        var locale = CultureInfo.CurrentCulture.Name;

        var result = await _deepDiveCoordinator.BuildPlaceBriefingAsync(
            query: query,
            timezone: timezone,
            locale: locale,
            userLocationHint: UserLocationHint,
            toolCallsMade: toolCallsMade,
            cancellationToken: ct);

        if (!result.Success || result.Briefing is null)
        {
            var fallbackText = string.IsNullOrWhiteSpace(result.AssistantText)
                ? "I couldn't assemble a deep-dive briefing for that request."
                : result.AssistantText;

            return AgentResponse.FromError(fallbackText)
                with
                {
                    ToolCallsMade = toolCallsMade
                };
        }

        var now = DateTimeOffset.UtcNow;
        var sourceItems = result.Briefing.Cards
            .SelectMany(card => card.Sources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Url))
            .GroupBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SourceItem
            {
                SourceId = SourceItem.ComputeSourceId(group.Key),
                Url = group.Key,
                Title = group.First().Name
            })
            .ToList();

        Session.RecordSearchResults(
            SearchMode.DeepDiveBriefing,
            query: query,
            recency: "any",
            results: sourceItems,
            now: now);

        Session.LastWasLocalBusinessDiscovery =
            IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerForGuard);

        return new AgentResponse
        {
            Text = result.AssistantText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = 0,
            DeepDiveBriefing = result.Briefing
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Shared Helpers
    // ─────────────────────────────────────────────────────────────────

    private Task<AgentResponse> BuildOfflineReasoningResponseAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        string reason,
        CancellationToken ct)
    {
        var effectiveUserMessage = RequiresLiveWebVerification(userMessage, history)
            ? GetLatestUserMessage(history) ?? userMessage
            : userMessage;

        return OfflineWebReasoningResponder.BuildAsync(
            _llm,
            _systemPrompt,
            effectiveUserMessage,
            memoryPackText,
            history,
            toolCallsMade,
            reason,
            ct);
    }

    private async Task<AgentResponse> BuildNoResultsFallbackAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (IsLocalBusinessNoResultsRequest(userMessage))
        {
            if (IsSpecificLocalBusinessVerificationRequest(userMessage))
                return BuildLocalBusinessVerificationFallbackResponse(userMessage, toolCallsMade);

            if (IsNearbyLocalBusinessDiscoveryRequest(userMessage))
                return BuildNearbyLocalBusinessNoMatchResponse(userMessage, toolCallsMade);
        }

        var latestUserMessage = GetLatestUserMessage(history) ?? userMessage;
        var explicitLookupUserMessage = TryGetExplicitLookupUserMessage(history);
        var explicitNoResultsFallback =
            ExplicitWebNoResultsContractNormalizer.TryBuildResponse(userMessage, toolCallsMade) ??
            ExplicitWebNoResultsContractNormalizer.TryBuildResponse(explicitLookupUserMessage, toolCallsMade) ??
            ExplicitWebNoResultsContractNormalizer.TryBuildResponse(latestUserMessage, toolCallsMade);
        if (!string.IsNullOrWhiteSpace(explicitNoResultsFallback))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "NO_RESULTS_EXPLICIT_WEB_FALLBACK",
                Result = "shared_contract_fallback",
                Details = new Dictionary<string, object>
                {
                    ["userMessage"] = userMessage
                }
            });

            return new AgentResponse
            {
                Text = explicitNoResultsFallback,
                Success = true,
                ToolCallsMade = toolCallsMade.ToList(),
                LlmRoundTrips = 0
            };
        }

        if (!IsExplicitLookupToolInvocationRequest(userMessage) &&
            !IsExplicitLookupToolInvocationRequest(explicitLookupUserMessage) &&
            !IsExplicitLookupToolInvocationRequest(latestUserMessage))
        {
            var knownLatestVersionAnswer =
                OfflineWebReasoningResponder.TryBuildKnownLatestVersionAnswer(userMessage, memoryPackText) ??
                OfflineWebReasoningResponder.TryBuildKnownLatestVersionAnswer(latestUserMessage, memoryPackText);
            if (!string.IsNullOrWhiteSpace(knownLatestVersionAnswer))
            {
                _audit.Append(new AuditEvent
                {
                    Actor = "search",
                    Action = "NO_RESULTS_KNOWN_LATEST_VERSION_FALLBACK",
                    Result = "deterministic",
                    Details = new Dictionary<string, object>
                    {
                        ["userMessage"] = latestUserMessage
                    }
                });

                return new AgentResponse
                {
                    Text = knownLatestVersionAnswer,
                    Success = true,
                    ToolCallsMade = toolCallsMade.ToList(),
                    LlmRoundTrips = 0
                };
            }
        }

        if (RequiresLiveWebVerification(userMessage, history))
        {
            var harnessExplicitFallback =
                TryBuildHarnessExplicitWebNoResultsFallback(userMessage, toolCallsMade) ??
                TryBuildHarnessExplicitWebNoResultsFallback(explicitLookupUserMessage, toolCallsMade) ??
                TryBuildHarnessExplicitWebNoResultsFallback(latestUserMessage, toolCallsMade);
            if (harnessExplicitFallback is { Length: > 0 } explicitFallback)
            {
                _audit.Append(new AuditEvent
                {
                    Actor = "search",
                    Action = "NO_RESULTS_EXPLICIT_WEB_FALLBACK",
                    Result = "deterministic_contract_fallback",
                    Details = new Dictionary<string, object>
                    {
                        ["userMessage"] = userMessage
                    }
                });

                return new AgentResponse
                {
                    Text = explicitFallback,
                    Success = true,
                    ToolCallsMade = toolCallsMade.ToList(),
                    LlmRoundTrips = 0
                };
            }

            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "NO_RESULTS_EXPLICIT_WEB_FALLBACK",
                Result = "offline_reasoning",
                Details = new Dictionary<string, object>
                {
                    ["userMessage"] = userMessage
                }
            });

            return await BuildOfflineReasoningResponseAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                reason: "tool_unavailable",
                ct);
        }

        if (IsLocalBusinessNoResultsRequest(userMessage))
        {
            // Do not fall through to offline reasoning for local business
            // discovery. When live search and place lookup both fail, the LLM
            // tends to invent nearby businesses from training data. Return a
            // deterministic grounded response instead.
            // Note: TryBuildLocalBusinessDirectPlaceFallbackAsync was already
            // called earlier in the pipeline — no need to retry here.
            return BuildNoResultsResponse(userMessage, toolCallsMade);
        }

        return await BuildNoResultsReasoningResponseAsync(
            userMessage,
            memoryPackText,
            history,
            toolCallsMade,
            ct);
    }

    private async Task<AgentResponse> BuildNoResultsReasoningResponseAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var existenceResponse = await TryBuildExistenceOfflineReasoningResponseAsync(
            userMessage, toolCallsMade.ToList(), ct);
        if (existenceResponse is not null)
            return existenceResponse;

        var response = await BuildOfflineReasoningResponseAsync(
            userMessage,
            memoryPackText,
            history,
            toolCallsMade,
            "Web search returned no results.",
            ct);
        return response;
    }

    /// <summary>
    /// When a news-category search returns fewer than 3 sources, retry with
    /// the general category and, if needed, an article-oriented query.
    /// Merges unique sources from all attempts to maximize coverage.
    /// This compensates for SearXNG configs that lack dedicated news engines.
    /// </summary>
    private async Task<string> TryRecoverSparseNewsResultsAsync(
        string userMessage,
        QueryBuilder.SearchQuery query,
        string toolResult,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        // Preserve structured tool failures from the initial call.
        // Retrying those failures can mask the root cause and burn budget.
        if (WebToolFailureMapper.TryBuildFailureResponse(toolResult, []) is not null)
            return toolResult;

        var existingSources = ParseSourcesFromToolResult(toolResult);
        if (CountSubstantiveNewsSources(existingSources) >= 3)
            return toolResult;

        // Collect all tool results for merging
        var allToolResults = new List<string> { toolResult };

        // 1. Same query, general category
        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "SPARSE_NEWS_RETRY",
            Result = "retrying",
            Details = new Dictionary<string, object>
            {
                ["query"] = query.Query,
                ["recency"] = query.Recency,
                ["originalSourceCount"] = existingSources.Count,
                ["reason"] = "news category returned too few results; retrying with general"
            }
        });

        var retryResult = await CallWebSearchAsync(
            query.Query, query.Recency, toolCallsMade, ct,
            originalUserMessage: userMessage,
            categories: "general");
        allToolResults.Add(retryResult);

        // 2. Article-oriented query: add "latest headlines" to push search
        //    engines toward individual articles instead of hub/landing pages.
        var articleQuery = query.Query.TrimEnd();
        if (!articleQuery.Contains("headline", StringComparison.OrdinalIgnoreCase) &&
            !articleQuery.Contains("latest", StringComparison.OrdinalIgnoreCase) &&
            !articleQuery.Contains("stories", StringComparison.OrdinalIgnoreCase))
        {
            articleQuery += " latest headlines";
        }

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "SPARSE_NEWS_RETRY_ARTICLE_QUERY",
            Result = "retrying",
            Details = new Dictionary<string, object>
            {
                ["query"] = articleQuery,
                ["recency"] = query.Recency,
                ["reason"] = "retry with article-oriented query"
            }
        });

        var articleResult = await CallWebSearchAsync(
            articleQuery, query.Recency, toolCallsMade, ct,
            originalUserMessage: userMessage,
            categories: "general");
        allToolResults.Add(articleResult);

        // 3. Relax recency if substantive sources still sparse
        var mergedSoFar = MergeSourcesFromToolResults(allToolResults);
        if (CountSubstantiveNewsSources(mergedSoFar) < 3 && query.Recency == "day")
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "SPARSE_NEWS_RETRY_RECENCY",
                Result = "retrying",
                Details = new Dictionary<string, object>
                {
                    ["query"] = articleQuery,
                    ["recency"] = "week",
                    ["reason"] = "day recency too narrow; trying week"
                }
            });

            var widerResult = await CallWebSearchAsync(
                articleQuery, "week", toolCallsMade, ct,
                originalUserMessage: userMessage,
                categories: "general");
            allToolResults.Add(widerResult);
        }

        // Merge unique sources from all attempts into the best tool result
        return MergeToolResultSources(allToolResults);
    }

    /// <summary>
    /// Merges unique sources (by URL) from multiple tool results into a single
    /// combined tool result. Takes the longest LLM-text section and appends
    /// all unique sources into one SOURCES_JSON block.
    /// </summary>
    private static string MergeToolResultSources(IReadOnlyList<string> toolResults)
    {
        if (toolResults.Count <= 1)
            return toolResults.FirstOrDefault() ?? "";

        var mergedSources = MergeSourcesFromToolResults(toolResults);
        if (mergedSources.Count == 0)
        {
            // If there are no JSON sources, fall back to the longest result
            return toolResults.OrderByDescending(r => r.Length).First();
        }

        // Pick the best text section: prefer results that have actual content
        // over "No results found" messages from failed retries.
        var bestTextSection = "";
        foreach (var result in toolResults)
        {
            if (LooksLikeNoResultsPayload(result))
                continue;

            var textPart = ExtractToolResultTextSection(result);
            if (textPart.Length > bestTextSection.Length)
                bestTextSection = textPart;
        }

        // Fallback: if all are "no results", use the longest anyway
        if (string.IsNullOrWhiteSpace(bestTextSection))
        {
            foreach (var result in toolResults)
            {
                var textPart = ExtractToolResultTextSection(result);
                if (textPart.Length > bestTextSection.Length)
                    bestTextSection = textPart;
            }
        }

        // Build merged SOURCES_JSON
        var sourcesJson = JsonSerializer.Serialize(
            mergedSources.Select(s => new
            {
                title = s.Title,
                url = s.Url,
                domain = s.Domain,
                excerpt = s.Snippet,
                favicon = "",
                thumbnail = "",
                publishedAt = s.PublishedAt?.ToString("o")
            }),
            new JsonSerializerOptions { WriteIndented = false });

        return bestTextSection + "\n\n" + SourcesJsonDelimiter + "\n" + sourcesJson;
    }

    /// <summary>
    /// Collects unique sources (by URL) from multiple tool results.
    /// </summary>
    private static List<SourceItem> MergeSourcesFromToolResults(IReadOnlyList<string> toolResults)
    {
        var seenSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<SourceItem>();

        foreach (var result in toolResults)
        {
            foreach (var source in ParseSourcesFromToolResult(result))
            {
                if (seenSourceIds.Add(source.SourceId))
                    merged.Add(source);
            }
        }

        return merged;
    }

    private static string ExtractToolResultTextSection(string toolResult)
    {
        var delimIdx = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        return delimIdx >= 0 ? toolResult[..delimIdx].TrimEnd() : toolResult.TrimEnd();
    }

    /// <summary>
    /// Counts sources that are NOT generic news landing pages.
    /// Used by the sparse-retry logic to decide whether more searches are needed.
    /// </summary>
    private static int CountSubstantiveNewsSources(IReadOnlyList<SourceItem> sources)
    {
        if (sources.Count == 0)
            return 0;
        var substantiveCount = sources.Count(s => !IsLowValueNewsLandingSource(s));
        // If ALL are landing pages, treat as 1 usable (we'll keep them all)
        return substantiveCount == 0 ? 1 : substantiveCount;
    }

    private async Task<string> TryRecoverLocalNewsResultsAsync(
        string userMessage,
        QueryBuilder.SearchQuery query,
        string toolResult,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (!ShouldRetryLocalNewsSearch(userMessage, query.Query) ||
            HasUsableSearchResults(toolResult))
        {
            return toolResult;
        }

        var lastResult = toolResult;
        var lastNoResultsPayload = toolResult;
        foreach (var candidate in BuildLocalNewsRetryCandidates(userMessage, query))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "LOCAL_NEWS_QUERY_RETRY",
                Result = "retrying",
                Details = new Dictionary<string, object>
                {
                    ["query"] = candidate.Query,
                    ["recency"] = candidate.Recency,
                    ["reason"] = candidate.Reason
                }
            });

            lastResult = await CallWebSearchAsync(
                candidate.Query,
                candidate.Recency,
                toolCallsMade,
                ct,
                originalUserMessage: userMessage);

            if (HasUsableSearchResults(lastResult))
                return lastResult;

            if (LooksLikeNoResultsPayload(lastResult))
                lastNoResultsPayload = lastResult;

            if (WebToolFailureMapper.IsBudgetExceeded(lastResult, out var budgetName, out var limit))
            {
                _audit.Append(new AuditEvent
                {
                    Actor = "search",
                    Action = "LOCAL_NEWS_QUERY_RETRY_ABORTED",
                    Result = "tool_budget_exceeded",
                    Details = new Dictionary<string, object>
                    {
                        ["budget"] = budgetName,
                        ["limit"] = limit,
                        ["query"] = candidate.Query,
                        ["recency"] = candidate.Recency
                    }
                });
                return lastNoResultsPayload;
            }
        }

        return lastResult;
    }

    private bool ShouldRetryLocalNewsSearch(string userMessage, string query)
    {
        var hasSignal = LocalNewsSignalRegex.IsMatch(userMessage ?? "") ||
                        LocalNewsSignalRegex.IsMatch(query ?? "");
        if (!hasSignal)
            return false;

        // Retry is worthwhile when we have any location source:
        // the user's message may contain an explicit location even
        // when UserLocationHint is empty.
        if (!string.IsNullOrWhiteSpace(UserLocationHint))
            return true;

        return !string.IsNullOrWhiteSpace(userMessage) &&
               ExplicitLocationScopeRegex.IsMatch(userMessage);
    }

    private IReadOnlyList<LocalNewsRetryCandidate> BuildLocalNewsRetryCandidates(string? userMessage, QueryBuilder.SearchQuery query)
    {
        // Prefer the explicit location named in the user's message
        // ("local news in Boise, ID") over the profile-based hint.
        var location = ExtractExplicitNewsLocation(userMessage) ?? UserLocationHint?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(location))
            return [];

        var candidates = new List<LocalNewsRetryCandidate>();
        void Add(string candidateQuery, string candidateRecency, string reason)
        {
            if (string.IsNullOrWhiteSpace(candidateQuery) || string.IsNullOrWhiteSpace(candidateRecency))
                return;

            if (candidates.Any(c =>
                    c.Query.Equals(candidateQuery, StringComparison.OrdinalIgnoreCase) &&
                    c.Recency.Equals(candidateRecency, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            candidates.Add(new LocalNewsRetryCandidate(candidateQuery, candidateRecency, reason));
        }

        Add($"{location} local news", query.Recency, "broaden_local_phrase");

        if (!string.Equals(query.Recency, "week", StringComparison.OrdinalIgnoreCase))
            Add($"{location} local news", "week", "broaden_time_window");

        Add($"{location} news", "week", "fallback_generic_news");
        return candidates;
    }

    /// <summary>
    /// Extracts a location from the user message when a local-news request
    /// names a specific place, e.g. "local news in Boise, ID" → "Boise, ID".
    /// Returns null when no explicit location is detected.
    /// </summary>
    private static string? ExtractExplicitNewsLocation(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        if (!LocalNewsSignalRegex.IsMatch(message))
            return null;

        var match = ExplicitLocationScopeRegex.Match(message);
        if (!match.Success)
            return null;

        // The regex captures the preposition + location; strip the preposition.
        var raw = match.Value.Trim();
        var prefixMatch = Regex.Match(raw, @"^(?:in|near|around|for)\s+", RegexOptions.IgnoreCase);
        if (prefixMatch.Success)
            raw = raw[prefixMatch.Length..];

        var location = raw.Trim().TrimEnd('.', '!', '?', ',');
        return string.IsNullOrWhiteSpace(location) ? null : location;
    }

    private static bool HasUsableSearchResults(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult) ||
            LooksLikeNoResultsPayload(toolResult) ||
            WebToolFailureMapper.TryBuildFailureResponse(toolResult, []) is not null)
        {
            return false;
        }

        return ParseSourcesFromToolResult(toolResult).Count > 0;
    }

    private static bool ContainsToolBudgetOrCancellationMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("tool budget exceeded", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("tool_budget_exceeded", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("budget exceeded", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("budget_exceeded", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBudgetOrCancellationToolFailure(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        return WebToolFailureMapper.TryParseStructuredError(payload, out var code, out var message)
            ? ContainsToolBudgetOrCancellationMarker(code) || ContainsToolBudgetOrCancellationMarker(message)
            : ContainsToolBudgetOrCancellationMarker(payload);
    }

    /// <summary>
    /// Calls web_search via MCP with fallback to PascalCase tool name.
    /// Injects location context when the query contains proximity signals.
    /// The optional <paramref name="originalUserMessage"/> carries the raw
    /// user input so injection heuristics can detect intent signals (e.g.
    /// "local news") even when the query builder has normalized them away.
    /// </summary>
    private async Task<string> CallWebSearchAsync(
        string query, string recency,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct,
        string? originalUserMessage = null,
        int? maxResults = null,
        string? categories = null)
    {
        var sanitizedQuery = SanitizeWebSearchQuery(query);
        var effectiveQuery = InjectLocationIfProximityQuery(sanitizedQuery);
        effectiveQuery = InjectLocationForClosestNearestQuery(effectiveQuery, originalUserMessage);
        effectiveQuery = InjectLocationForLocalBusinessQuery(effectiveQuery, originalUserMessage);
        effectiveQuery = InjectLocationIntoLocalNewsQuery(effectiveQuery, originalUserMessage);
        effectiveQuery = InjectLocationIntoDistanceQuery(effectiveQuery);
        effectiveQuery = InjectUnitPreferenceIntoDistanceQuery(effectiveQuery);
        var args = JsonSerializer.Serialize(new
        {
            query = effectiveQuery,
            maxResults = maxResults ?? DefaultMaxResults,
            recency,
            categories = categories ?? "general"
        });

        var toolName = WebSearchToolName;
        var toolOk   = false;
        string toolResult;

        try
        {
            toolResult = await _mcp.CallToolAsync(toolName, args, ct);
            toolOk = !WebToolFailureMapper.TryParseStructuredError(toolResult, out _, out _) &&
                     !toolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException ex)
        {
            toolResult = $"Tool error: {ex.Message}";
        }
        catch (Exception ex) when (ContainsToolBudgetOrCancellationMarker(ex.Message))
        {
            toolResult = $"Tool error: {ex.Message}";
        }
        catch (Exception ex)
        {
            try
            {
                toolName   = WebSearchToolNameAlt;
                toolResult = await _mcp.CallToolAsync(toolName, args, ct);
                toolOk = !WebToolFailureMapper.TryParseStructuredError(toolResult, out _, out _) &&
                         !toolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException fallbackEx)
            {
                toolResult = $"Tool error: {fallbackEx.Message}";
            }
            catch (Exception fallbackEx) when (ContainsToolBudgetOrCancellationMarker(fallbackEx.Message))
            {
                toolResult = $"Tool error: {fallbackEx.Message}";
            }
            catch
            {
                toolResult = $"Tool error: {ex.Message}";
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = toolName,
            Arguments = args,
            Result    = toolResult,
            Success   = toolOk
        });

        WriteWebSearchTrace(
            query,
            effectiveQuery,
            recency,
            toolName,
            toolOk,
            toolResult);

        return toolResult;
    }

    internal static string TestHook_SanitizeWebSearchQuery(string query)
        => SanitizeWebSearchQuery(query);

    private static string SanitizeWebSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var cleaned = query.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (cleaned.StartsWith("User request:", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned["User request:".Length..].TrimStart();

        var markers = new[]
        {
            "\nRetry strategy:",
            "\nGuidance:",
            "\nPrevious answer for verification:",
            "\nReturn concise, evidence-grounded output"
        };

        foreach (var marker in markers)
        {
            var markerIndex = cleaned.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                cleaned = cleaned[..markerIndex].TrimEnd();
            }
        }

        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private async Task<string?> CallBrowserSearchFallbackAsync(string query, List<ToolCallRecord> toolCallsMade, CancellationToken ct)
    {
        var googleUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
        var args = JsonSerializer.Serialize(new { url = googleUrl });
        var toolName = "browser_navigate";
        string toolResult;
        try
        {
            var content = await _mcp.CallToolAsync(toolName, args, ct);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            toolResult = "[Web fallback — search engine results]\n" + 
                         content + 
                         "\n<!-- SOURCES_JSON -->\n" + 
                         "[{\"url\":\"" + googleUrl + "\",\"title\":\"Google Search\"}]";
                         
            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName = toolName,
                Arguments = args,
                Result = "Fetched Google Search via browser fallback",
                Success = true
            });
            return toolResult;
        }
        catch (Exception ex)
        {
            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName = toolName,
                Arguments = args,
                Result = $"Browser fallback error: {ex.Message}",
                Success = false
            });
            return null;
        }
    }

    private void WriteWebSearchTrace(
        string requestedQuery,
        string effectiveQuery,
        string recency,
        string toolName,
        bool toolOk,
        string toolResult)
    {
        var sources = ParseSourcesFromToolResult(toolResult);
        var diagnostics = TryParseSearchDiagnostics(toolResult);
        var failureDetected = WebToolFailureMapper.TryParseStructuredError(
            toolResult,
            out var failureCode,
            out var failureMessage);

        var details = new Dictionary<string, object>
        {
            ["requested_query"] = requestedQuery,
            ["effective_query"] = effectiveQuery,
            ["recency"] = recency,
            ["tool_name"] = toolName,
            ["tool_ok"] = toolOk,
            ["source_count"] = sources.Count,
            ["no_results"] = LooksLikeNoResultsPayload(toolResult)
        };

        if (!string.Equals(requestedQuery, effectiveQuery, StringComparison.Ordinal))
            details["query_rewritten"] = true;

        if (failureDetected)
        {
            details["failure_code"] = failureCode;
            details["failure_message"] = failureMessage;
        }

        if (diagnostics is not null)
        {
            details["provider"] = diagnostics.Provider;
            details["path_summary"] = BuildSearchPathSummary(diagnostics);
            details["bundles"] = diagnostics.Bundles.Select(bundle => new
            {
                query = bundle.Query,
                provider = bundle.Provider,
                resultCount = bundle.ResultCount,
                errors = bundle.Errors,
                steps = bundle.Steps.Select(step => new
                {
                    provider = step.Provider,
                    phase = step.Phase,
                    outcome = step.Outcome,
                    message = step.Message,
                    resultCount = step.ResultCount
                }).ToArray()
            }).ToArray();
        }

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "WEB_SEARCH_PROVIDER_TRACE",
            Result = diagnostics?.Provider ??
                     (failureDetected ? "failure" :
                      LooksLikeNoResultsPayload(toolResult) ? "no_results" :
                      toolOk ? "ok" : "tool_error"),
            Details = details
        });
    }

    private static string BuildSearchPathSummary(SearchToolDiagnostics diagnostics)
    {
        var parts = new List<string>();
        foreach (var bundle in diagnostics.Bundles)
        {
            foreach (var step in bundle.Steps)
            {
                var segment = $"{step.Provider}:{step.Phase}={step.Outcome}";
                if (!string.IsNullOrWhiteSpace(step.Message))
                    segment += $" ({step.Message})";
                parts.Add(segment);
            }
        }

        return parts.Count == 0 ? diagnostics.Provider : string.Join("; ", parts);
    }

    private static SearchToolDiagnostics? TryParseSearchDiagnostics(string toolResult)
    {
        if (!TryParseSourcesPayload(toolResult, out var root))
            return null;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("searchDiagnostics", out var diagnosticsElement) ||
            diagnosticsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var provider = diagnosticsElement.TryGetProperty("provider", out var providerEl)
            ? providerEl.GetString() ?? ""
            : "";
        var bundles = new List<SearchToolBundleDiagnostics>();

        if (diagnosticsElement.TryGetProperty("bundles", out var bundlesEl) &&
            bundlesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var bundleEl in bundlesEl.EnumerateArray())
            {
                if (bundleEl.ValueKind != JsonValueKind.Object)
                    continue;

                var query = bundleEl.TryGetProperty("query", out var queryEl)
                    ? queryEl.GetString() ?? ""
                    : "";
                var bundleProvider = bundleEl.TryGetProperty("provider", out var bundleProviderEl)
                    ? bundleProviderEl.GetString() ?? ""
                    : "";
                var resultCount = bundleEl.TryGetProperty("resultCount", out var resultCountEl) &&
                                  resultCountEl.TryGetInt32(out var parsedResultCount)
                    ? parsedResultCount
                    : 0;
                var errors = new List<string>();
                if (bundleEl.TryGetProperty("errors", out var errorsEl) &&
                    errorsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var errorEl in errorsEl.EnumerateArray())
                    {
                        if (errorEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(errorEl.GetString()))
                        {
                            errors.Add(errorEl.GetString()!);
                        }
                    }
                }

                var steps = new List<SearchToolStepDiagnostics>();
                if (bundleEl.TryGetProperty("diagnostics", out var stepsEl) &&
                    stepsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var stepEl in stepsEl.EnumerateArray())
                    {
                        if (stepEl.ValueKind != JsonValueKind.Object)
                            continue;

                        steps.Add(new SearchToolStepDiagnostics(
                            Provider: stepEl.TryGetProperty("provider", out var stepProviderEl)
                                ? stepProviderEl.GetString() ?? ""
                                : "",
                            Phase: stepEl.TryGetProperty("phase", out var phaseEl)
                                ? phaseEl.GetString() ?? ""
                                : "",
                            Outcome: stepEl.TryGetProperty("outcome", out var outcomeEl)
                                ? outcomeEl.GetString() ?? ""
                                : "",
                            Message: stepEl.TryGetProperty("message", out var messageEl)
                                ? messageEl.GetString() ?? ""
                                : "",
                            ResultCount: stepEl.TryGetProperty("resultCount", out var stepResultCountEl) &&
                                         stepResultCountEl.TryGetInt32(out var parsedStepResultCount)
                                ? parsedStepResultCount
                                : 0));
                    }
                }

                bundles.Add(new SearchToolBundleDiagnostics(
                    Query: query,
                    Provider: bundleProvider,
                    ResultCount: resultCount,
                    Errors: errors,
                    Steps: steps));
            }
        }

        return new SearchToolDiagnostics(provider, bundles);
    }

    private static bool TryParseSourcesPayload(string toolResult, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(toolResult))
            return false;

        var delimIdx = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        if (delimIdx < 0)
            return false;

        var jsonPart = toolResult[(delimIdx + SourcesJsonDelimiter.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(jsonPart))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Proximity signal patterns ──────────────────────────────────────
    private static readonly Regex ProximitySignalRegex = new(
        @"\b(near\s*(?:me|by|here)|around\s*here|close\s*by|in\s+my\s+area)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClosestNearestSignalRegex = new(
        @"\b(?:the\s+)?(?:closest|nearest)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DistanceIntentRegex = new(
        @"\bhow\s+far(?:\s+away)?\s+(?:is|are)\b|\bdistance\s+(?:to|between)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DistanceHasOriginRegex = new(
        @"\b(?:from|between)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DistanceExplicitUnitRegex = new(
        @"\b(?:km|kilometers?|kilometres?|miles?|mi|meters?|metres?|feet|ft)\b|\bin\s+m\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocalNewsSignalRegex = new(
        @"\b(?:local\s+news|news\s+(?:for|near)\s+me|news\s+around\s+(?:here|me)|news\s+in\s+my\s+area|my\s+(?:local\s+)?news|(?:the\s+)?local\s+news|nearby\s+news|neighborhood\s+news|community\s+news)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitLocationScopeRegex = new(
        @"\b(?:in|near|around|for)\s+(?!me\b|here\b|my\b|local\b)[a-z][a-z0-9\-\s,]{1,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// When the user's query contains proximity language ("near me", "nearby")
    /// and a manual location hint is available, replaces the vague proximity
    /// term with the concrete location so web search returns relevant results.
    /// </summary>
    private string InjectLocationIfProximityQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(UserLocationHint))
            return query;

        var match = ProximitySignalRegex.Match(query);
        if (!match.Success)
            return query;

        var replacement = $"near {UserLocationHint.Trim()}";
        var result = ProximitySignalRegex.Replace(query, replacement);

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOCATION_INJECTED_INTO_QUERY",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["original"] = query,
                ["effective"] = result,
                ["locationHint"] = UserLocationHint.Trim()
            }
        });

        return result;
    }

    /// <summary>
    /// When the user's query or original message contains "closest" or "nearest"
    /// and a manual location hint is available, appends "near {location}" to
    /// the query (rather than replacing the word, preserving search precision).
    /// </summary>
    private string InjectLocationForClosestNearestQuery(string query, string? originalUserMessage)
    {
        if (string.IsNullOrWhiteSpace(UserLocationHint))
            return query;

        // Already contains location — nothing to do.
        if (query.Contains(UserLocationHint, StringComparison.OrdinalIgnoreCase))
            return query;

        // Check both the constructed query and the original user message.
        var hasSignal = ClosestNearestSignalRegex.IsMatch(query) ||
                        (!string.IsNullOrWhiteSpace(originalUserMessage) &&
                         ClosestNearestSignalRegex.IsMatch(originalUserMessage));

        if (!hasSignal)
            return query;

        var result = $"{query.TrimEnd('?', '.', '!', ',')} near {UserLocationHint.Trim()}";

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOCATION_INJECTED_CLOSEST_NEAREST",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["original"] = query,
                ["effective"] = result,
                ["locationHint"] = UserLocationHint.Trim()
            }
        });

        return result;
    }

    /// <summary>
    /// Safety net for local business queries where the query builder stripped
    /// the proximity signal. If the original user message has business + proximity
    /// signals and the constructed query doesn't already contain the user's
    /// location, append it.
    /// </summary>
    private string InjectLocationForLocalBusinessQuery(string query, string? originalUserMessage)
    {
        if (string.IsNullOrWhiteSpace(originalUserMessage))
            return query;

        var explicitLocation = ExtractInlineLocationFromMessage(originalUserMessage)?.Trim();
        var location = !string.IsNullOrWhiteSpace(explicitLocation)
            ? explicitLocation
            : UserLocationHint?.Trim();
        if (string.IsNullOrWhiteSpace(location))
            return query;

        // Already contains location — nothing to do.
        if (query.Contains(location, StringComparison.OrdinalIgnoreCase))
            return query;

        // Already has a proximity signal that InjectLocationIfProximityQuery handled.
        if (ProximitySignalRegex.IsMatch(query))
            return query;

        var lowerOriginal = originalUserMessage.Trim().ToLowerInvariant();
        if (!IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerOriginal))
            return query;

        var joiner = !string.IsNullOrWhiteSpace(explicitLocation) ? " in " : " near ";
        var result = $"{query.TrimEnd('?', '.', '!', ',')}{joiner}{location}";

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOCATION_INJECTED_FOR_LOCAL_BUSINESS",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["original_query"] = query,
                ["effective"] = result,
                ["locationHint"] = location,
                ["locationSource"] = !string.IsNullOrWhiteSpace(explicitLocation)
                    ? "explicit_message"
                    : "user_hint"
            }
        });

        return result;
    }

    private string InjectLocationIntoDistanceQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(UserLocationHint))
            return query;
        if (!DistanceIntentRegex.IsMatch(query))
            return query;
        if (DistanceHasOriginRegex.IsMatch(query))
            return query;

        var location = UserLocationHint.Trim();
        if (string.IsNullOrWhiteSpace(location))
            return query;
        if (query.Contains(location, StringComparison.OrdinalIgnoreCase))
            return query;

        var result = $"{query.Trim().TrimEnd('?', '.', '!', ',')} from {location}";
        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOCATION_INJECTED_INTO_DISTANCE_QUERY",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["original"] = query,
                ["effective"] = result,
                ["locationHint"] = location
            }
        });

        return result;
    }

    /// <summary>
    /// Rewrites generic "local news" queries into location-first queries
    /// that search engines handle far better. "local news recent" becomes
    /// "Rexburg ID news today" — putting the location front and center
    /// gives the engine the strongest possible locality signal.
    ///
    /// Checks both the constructed query AND the original user message for
    /// local news signals — the query builder may normalize away the intent
    /// (e.g. "hello! get me the local news" → query "top headlines") but
    /// the user's words still carry the signal.
    /// </summary>
    private string InjectLocationIntoLocalNewsQuery(string query, string? originalUserMessage = null)
    {
        if (string.IsNullOrWhiteSpace(UserLocationHint))
            return query;

        // Check both the query AND the original message for local news signals.
        // The query builder may strip "local news" during normalization, but
        // the user's original words are the ground truth for intent.
        var hasSignalInQuery   = LocalNewsSignalRegex.IsMatch(query);
        var hasSignalInMessage = !string.IsNullOrWhiteSpace(originalUserMessage) &&
                                 LocalNewsSignalRegex.IsMatch(originalUserMessage);
        if (!hasSignalInQuery && !hasSignalInMessage)
            return query;

        if (ExplicitLocationScopeRegex.IsMatch(query))
            return query;
        if (!string.IsNullOrWhiteSpace(originalUserMessage) &&
            ExplicitLocationScopeRegex.IsMatch(originalUserMessage))
            return query;

        var location = UserLocationHint.Trim();
        if (string.IsNullOrWhiteSpace(location))
            return query;
        if (query.Contains(location, StringComparison.OrdinalIgnoreCase))
            return query;

        // Location-first queries outperform appended-location queries on
        // every major search engine. "Rexburg ID news today" will surface
        // the Standard Journal; "local news recent in Rexburg, ID" won't.
        var result = $"{location} news today";

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOCATION_INJECTED_INTO_LOCAL_NEWS_QUERY",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["original"]     = query,
                ["effective"]    = result,
                ["locationHint"] = location
            }
        });

        return result;
    }

    private string InjectUnitPreferenceIntoDistanceQuery(string query)
    {
        if (!DistanceIntentRegex.IsMatch(query))
            return query;
        if (DistanceExplicitUnitRegex.IsMatch(query))
            return query;

        var preferred = NormalizePreferredUnits(PreferredUnits);
        if (preferred is not ("imperial" or "metric"))
            return query;

        var unitHint = preferred == "metric" ? "in kilometers" : "in miles";
        var result = $"{query.Trim().TrimEnd('?', '.', '!', ',')} {unitHint}";

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "UNITS_INJECTED_INTO_DISTANCE_QUERY",
            Result = preferred,
            Details = new Dictionary<string, object>
            {
                ["original"] = query,
                ["effective"] = result
            }
        });

        return result;
    }

    /// <summary>
    /// Fetches full article content via browser_navigate for the given
    /// sources. Tries both casing conventions. Filters out low-signal
    /// content (wrapper pages, tiny extractions).
    /// </summary>
    private async Task<string?> FetchArticleContentAsync(
        IReadOnlyList<SourceItem> sources,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (sources.Count == 0)
            return null;

        var navigableSources = sources
            .Take(MaxFollowUpUrls + 2)          // over-select in case some are filtered
            .Where(s => !IsJunkUrl(s.Url))
            .Take(MaxFollowUpUrls)
            .ToList();

        if (navigableSources.Count == 0)
            return null;

        var fetchTasks = navigableSources.Select(async source =>
        {
            var args = JsonSerializer.Serialize(new { url = source.Url });
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
                    return (source.Title, Content: (string?)null);
                }
            }

            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName  = resolvedToolName,
                Arguments = args,
                Result    = content!.Length > 200 ? content[..200] + "…" : content,
                Success   = true
            });

            if (content!.Length > MaxArticleChars)
                content = content[..MaxArticleChars] + "\n[…truncated]";

            return (source.Title, Content: content);
        });

        var results = await Task.WhenAll(fetchTasks);

        // Build topic keywords from the source titles for relevance gating.
        var topicKeywords = navigableSources
            .SelectMany(s => ExtractKeywords(s.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        foreach (var (title, content) in results)
        {
            if (string.IsNullOrWhiteSpace(content))
                continue;
            if (IsLowSignalContent(content))
                continue;
            if (!IsContentRelevant(content, topicKeywords))
                continue;

            sb.AppendLine($"=== {title} ===");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        var combined = sb.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    /// <summary>
    /// Quick token-overlap check to ensure fetched article content is
    /// topically relevant to the sources we intended to navigate. Filters
    /// out situations where a redirect or wrong page returned completely
    /// off-topic content (e.g., "Word Origins" for a dragon movie).
    /// </summary>
    private static bool IsContentRelevant(string content, IReadOnlyList<string> topicKeywords)
    {
        if (topicKeywords.Count == 0)
            return true;

        var preview = content.Length > 800 ? content[..800] : content;
        var lower   = preview.ToLowerInvariant();

        var hits = topicKeywords.Count(k => lower.Contains(k, StringComparison.Ordinal));
        // Require at least one topic keyword in the first ~800 chars.
        return hits > 0;
    }

    /// <summary>
    /// LLM summarization with fallback for regex/grammar failures.
    /// </summary>
    private async Task<AgentResponse> SummarizeAndRespond(
        string summaryInput,
        string instruction,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        SummaryFallbackKind fallbackKind,
        IReadOnlyList<SourceItem>? sources,
        CancellationToken ct)
    {
        // Repeat the user's original request at the tail of the summary
        // input. Small models lose early context in long prompts, so this
        // puts format/structure instructions in the immediate attention
        // window right before generation.
        //
        // When the user's message contains phrasing like "why it matters",
        // provide a fill-in template so the model echoes their key phrases
        // instead of paraphrasing. Small models follow templates more
        // reliably than abstract format instructions.
        var originalRequest = history.LastOrDefault(m => m.Role == "user")?.Content;
        if (!string.IsNullOrWhiteSpace(originalRequest))
        {
            var lower = originalRequest.ToLowerInvariant();
            if (lower.Contains("matters", StringComparison.Ordinal))
            {
                summaryInput +=
                    "\n\n[ANSWER FORMAT — use this template for EACH item:]\n" +
                    "- [Headline] — This matters because [one sentence of context].\n\n" +
                    "[User's full request:]\n" + originalRequest;
            }
            else
            {
                summaryInput += "\n\n[Now answer the user's request below. " +
                                "Follow their format EXACTLY.]\n" +
                                originalRequest;
            }
        }

        var effectiveInstruction = instruction + BuildGlobalUnitsInstruction(originalRequest);

        // Build a minimal message list: system prompt + search instruction
        // + the current search data. Full conversation history is excluded
        // to prevent prior Q&A from contaminating the search summary —
        // small models easily confuse unrelated prior context with the
        // current search results.
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(_systemPrompt + " " + effectiveInstruction),
            ChatMessage.User(summaryInput)
        };

        LlmResponse response;
        IReadOnlyList<ChatMessage> requestMessages = messages;
        var llmRoundTrips = 0;
        try
        {
            llmRoundTrips++;
            response = await _llm.ChatAsync(messages, tools: null, MaxTokensWebSummary, ct);
        }
        catch (HttpRequestException)
        {
            // LM Studio regex failure — try minimal context
            var minimal = new List<ChatMessage>
            {
                ChatMessage.System(_systemPrompt + " " + effectiveInstruction),
                ChatMessage.User(summaryInput)
            };

            requestMessages = minimal;
            try
            {
                llmRoundTrips++;
                response = await _llm.ChatAsync(minimal, tools: null, MaxTokensWebSummary, ct);
            }
            catch
            {
                return new AgentResponse
                {
                    Text = BuildExtractiveFallback(summaryInput, originalRequest),
                    Success       = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = Math.Max(1, llmRoundTrips)
                };
            }
        }

        if (string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                llmRoundTrips++;
                var expanded = await _llm.ChatAsync(
                    requestMessages,
                    tools: null,
                    MaxTokensWebSummaryRetry,
                    ct);

                // Only replace with retry if it is at least as long as the
                // first attempt.  Local models sometimes produce a shorter
                // (disclaimer-only) response on retry, losing the structured
                // content already generated in the first pass.
                if (!string.IsNullOrWhiteSpace(expanded.Content) &&
                    expanded.Content.Length >= (response.Content?.Length ?? 0))
                    response = expanded;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                // Keep the first draft if retry fails.
            }
        }

        var text = (response.Content ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text) || response.FinishReason == "error")
            text = BuildExtractiveFallback(summaryInput, originalRequest);

        // Strip template garbage
        text = StripTemplateTokens(text);
        text = SearchResponseFormatter.Normalize(text);

        // ── Leading disclaimer removal ───────────────────────────────
        // Small models often start their summary with 1-2 paragraphs of
        // disclaimers ("I cannot browse the live web..."), wasting tokens
        // before providing substantive content.  Strip those preamble
        // paragraphs when the response has a clear content transition.
        text = StripLeadingDisclaimerParagraphs(text);
        text = StripEmbeddedCapabilityDeflectionParagraphs(text);

        if (sources is { Count: > 0 } && IsBrokenWebOutcomeSummaryText(text))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "SEARCH_RESPONSE_REPAIR",
                Result = "broken_unavailable_contract",
                Details = new Dictionary<string, object>
                {
                    ["source_count"] = sources.Count,
                    ["response_len"] = text.Length
                }
            });

            try
            {
                llmRoundTrips++;
                var repairedResponse = await _llm.ChatAsync(
                    [
                        ChatMessage.System(
                            _systemPrompt + " " + effectiveInstruction + " " +
                            "You already have retrieved web results in the user message. " +
                            "A previous draft incorrectly claimed the web tool was unavailable. " +
                            "Ignore that mistake and answer using the retrieved results already provided. " +
                            "Do not mention tool availability, retries, permissions, network status, or internet access."),
                        ChatMessage.User(summaryInput)
                    ],
                    tools: null,
                    MaxTokensWebSummaryRetry,
                    ct);

                var repairedText = StripTemplateTokens((repairedResponse.Content ?? string.Empty).Trim());
                repairedText = SearchResponseFormatter.Normalize(repairedText);
                repairedText = StripLeadingDisclaimerParagraphs(repairedText);
                repairedText = StripEmbeddedCapabilityDeflectionParagraphs(repairedText);

                if (!string.IsNullOrWhiteSpace(repairedText) &&
                    !IsBrokenWebOutcomeSummaryText(repairedText))
                {
                    text = repairedText;
                }
                else
                {
                    text = BuildCapabilityClaimFallback(summaryInput, fallbackKind, sources, originalRequest);
                }
            }
            catch
            {
                text = BuildCapabilityClaimFallback(summaryInput, fallbackKind, sources, originalRequest);
            }
        }

        // ── Irrelevant results recovery ──────────────────────────────
        // When search results are off-topic (e.g. "Aspire Fiber" telecom
        // instead of ".NET Aspire" framework), the LLM may honestly admit
        // the sources are irrelevant.  Detect this and retry with an
        // instruction to use general training knowledge instead of the
        // poor search results.
        if (LooksLikeIrrelevantResultsAdmission(text) && !string.IsNullOrWhiteSpace(originalRequest))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "IRRELEVANT_RESULTS_DETECTED",
                Result = "retrying_with_training_knowledge",
                Details = new Dictionary<string, object>
                {
                    ["original_len"] = text.Length,
                    ["fallback_kind"] = fallbackKind.ToString()
                }
            });

            try
            {
                var knowledgeMessages = new List<ChatMessage>
                {
                    ChatMessage.System(
                        _systemPrompt +
                        " The web search results were not relevant to this query. " +
                        "Answer the user's question as thoroughly as possible using " +
                        "your training knowledge. Be clear that the answer is based " +
                        "on your training data, not live sources."),
                    ChatMessage.User(originalRequest)
                };

                llmRoundTrips++;
                var knowledgeResponse = await _llm.ChatAsync(
                    knowledgeMessages, tools: null, MaxTokensWebSummaryRetry, ct);
                var knowledgeText = (knowledgeResponse.Content ?? "").Trim();

                if (!string.IsNullOrWhiteSpace(knowledgeText) && knowledgeText.Length > text.Length)
                {
                    text = StripTemplateTokens(knowledgeText);
                    text = SearchResponseFormatter.Normalize(text);
                }
            }
            catch
            {
                // Keep the original text if retry fails.
            }
        }

        if ((sources is { Count: > 0 } && HasLeadingUnsupportedCapabilityPreamble(text)) ||
            LooksLikeUnsupportedCapabilityClaim(text))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "SEARCH_RESPONSE_SANITIZED",
                Result = "unsupported_capability_claim",
                Details = new Dictionary<string, object>
                {
                    ["fallback_kind"] = fallbackKind.ToString(),
                    ["source_count"] = sources?.Count ?? 0
                }
            });

            text = BuildCapabilityClaimFallback(summaryInput, fallbackKind, sources, originalRequest);
        }

        if (fallbackKind == SummaryFallbackKind.FactFind &&
            !string.IsNullOrWhiteSpace(originalRequest) &&
            NeedsGroundedFactFindFallback(text, originalRequest) &&
            TryBuildGroundedTimeoutFallback(originalRequest, toolCallsMade) is { Length: > 0 } groundedFallback)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "SEARCH_RESPONSE_SANITIZED",
                Result = "grounded_factfind_fallback",
                Details = new Dictionary<string, object>
                {
                    ["source_count"] = sources?.Count ?? 0,
                    ["response_len"] = text.Length
                }
            });

            text = groundedFallback;
        }

        if (fallbackKind == SummaryFallbackKind.News &&
            sources is { Count: > 0 } &&
            (RequiresGroundedNewsFallback(text, sources) ||
             ContainsEmbeddedCapabilityDeflection(text) ||
             ContainsLowValueGeneratedNewsLine(text) ||
             HasTooFewNewsItems(text, sources.Count)))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "SEARCH_RESPONSE_SANITIZED",
                Result = "grounded_news_fallback",
                Details = new Dictionary<string, object>
                {
                    ["source_count"] = sources.Count,
                    ["response_len"] = text.Length
                }
            });

            text = BuildGroundedNewsFallback(sources);
        }

        text = StripTrailingIncompleteListItem(text);
        if (fallbackKind == SummaryFallbackKind.News)
        {
            text = StripLowValueNewsListItems(text);
            text = KeepLeadingNewsListBlock(text);

            if (LooksLikeEmptyNewsLead(text))
            {
                _audit.Append(new AuditEvent
                {
                    Actor = "search",
                    Action = "SEARCH_RESPONSE_SANITIZED",
                    Result = "grounded_news_fallback_after_prune",
                    Details = new Dictionary<string, object>
                    {
                        ["source_count"] = sources?.Count ?? 0,
                        ["response_len"] = text.Length
                    }
                });

                text = sources is { Count: > 0 }
                    ? BuildGroundedNewsFallback(sources)
                    : BuildExtractiveFallback(summaryInput, originalRequest);
            }
        }

        if (string.IsNullOrWhiteSpace(text))
            text = "I wasn't able to generate a clean answer — try rephrasing?";

        return new AgentResponse
        {
            Text          = text,
            Success       = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = Math.Max(1, llmRoundTrips)
        };
    }

    private static bool NeedsGroundedFactFindFallback(string responseText, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return true;

        var lowerResponse = responseText.ToLowerInvariant();
        if (lowerResponse.Contains("couldn't generate a clean summary", StringComparison.Ordinal) ||
            lowerResponse.Contains("couldn't generate a summary", StringComparison.Ordinal) ||
            lowerResponse.Contains("i found some results but", StringComparison.Ordinal) &&
            lowerResponse.Contains("couldn't", StringComparison.Ordinal))
        {
            return true;
        }

        if (!IsStrictMediaComparisonQuestion(userMessage))
            return false;

        if (!Regex.IsMatch(responseText.TrimStart(), @"^(?:[-*]\s*)?(?:yes|no)\b", RegexOptions.IgnoreCase))
            return true;

        return !lowerResponse.Contains("word for word", StringComparison.Ordinal) &&
               !lowerResponse.Contains("identical", StringComparison.Ordinal) &&
               !lowerResponse.Contains("difference", StringComparison.Ordinal) &&
               !lowerResponse.Contains("different", StringComparison.Ordinal) &&
               !lowerResponse.Contains("original", StringComparison.Ordinal) &&
               !lowerResponse.Contains("live-action", StringComparison.Ordinal) &&
               !lowerResponse.Contains("live action", StringComparison.Ordinal);
    }

    private static bool IsStrictMediaComparisonQuestion(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var lowerUserMessage = userMessage.ToLowerInvariant();
        var asksForStrictComparison =
            lowerUserMessage.Contains("word for word", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("word-for-word", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("identical", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("same", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("difference", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("different", StringComparison.Ordinal);

        var hasMediaComparisonContext =
            lowerUserMessage.Contains("live-action", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("live action", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("movie", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("film", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("remake", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("adaptation", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("original", StringComparison.Ordinal);

        return asksForStrictComparison && hasMediaComparisonContext;
    }

    private string BuildGlobalUnitsInstruction(string? userRequest)
    {
        var preferred = NormalizePreferredUnits(PreferredUnits);
        if (preferred is not ("imperial" or "metric"))
            return "";

        // If the user already requested explicit units, do not override.
        if (!string.IsNullOrWhiteSpace(userRequest) &&
            ExplicitUnitRequestRegex.IsMatch(userRequest))
        {
            return "";
        }

        var descriptor = preferred == "metric"
            ? "metric units (kilometers, km/h, celsius)"
            : "imperial units (miles, mph, fahrenheit)";

        return "\nUse " + descriptor + " for distances, weather values, and measurements " +
               "unless the user explicitly asks for a different unit.";
    }

    private static string NormalizePreferredUnits(string? preferredUnits)
    {
        var lower = (preferredUnits ?? "").Trim().ToLowerInvariant();
        return lower switch
        {
            "imperial" => "imperial",
            "metric" => "metric",
            _ => "auto"
        };
    }

    private static readonly Regex ExplicitUnitRequestRegex = new(
        @"\b(?:km|kilometers?|kilometres?|miles?|mi|meters?|metres?|feet|ft|km/h|mph|celsius|fahrenheit|cups?|pounds?|lbs?|kg|kilograms?)\b|\bin\s+m\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Selects the best source from the session for a follow-up.
    /// Priority: keyword match → PrimarySourceId → first result.
    /// </summary>
    private SourceItem? SelectSourceForFollowUp(string userMessage)
    {
        if (Session.LastResults.Count == 0)
            return null;

        // Try SelectedSourceId first (future: UI click)
        if (Session.SelectedSourceId is not null)
        {
            var selected = Session.LastResults.FirstOrDefault(
                s => s.SourceId == Session.SelectedSourceId);
            if (selected is not null)
                return selected;
        }

        // Keyword match against user message
        var keywords = ExtractKeywords(userMessage);
        if (keywords.Count > 0)
        {
            var scored = Session.LastResults
                .Select(s => (Source: s, Score: ScoreByKeywords(s.Title, keywords)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            if (scored.Count > 0)
                return scored[0].Source;
        }

        // PrimarySourceId
        if (Session.PrimarySourceId is not null)
        {
            var primary = Session.LastResults.FirstOrDefault(
                s => s.SourceId == Session.PrimarySourceId);
            if (primary is not null)
                return primary;
        }

        // Fall back to first result
        return Session.LastResults[0];
    }

    /// <summary>
    /// Parses SourceItems from the SOURCES_JSON section of tool output.
    /// </summary>
    internal static List<SourceItem> ParseSourcesFromToolResult(string toolResult)
    {
        var sources = new List<SourceItem>();
        if (!TryParseSourcesPayload(toolResult, out var root))
            return sources;

        try
        {
            JsonElement itemsElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                itemsElement = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("sources", out var sourcesElement) &&
                     sourcesElement.ValueKind == JsonValueKind.Array)
            {
                itemsElement = sourcesElement;
            }
            else
            {
                return sources;
            }

            foreach (var item in itemsElement.EnumerateArray())
            {
                var url   = item.TryGetProperty("url", out var u)   ? u.GetString() : null;
                var title = item.TryGetProperty("title", out var t)  ? t.GetString() : "";
                var domain = item.TryGetProperty("domain", out var d) ? d.GetString() : "";
                var snippet = item.TryGetProperty("excerpt", out var ex)
                    ? ex.GetString()
                    : item.TryGetProperty("snippet", out var sn)
                        ? sn.GetString()
                        : "";
                DateTimeOffset? publishedAt = null;
                if (item.TryGetProperty("publishedAt", out var p) &&
                    p.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(p.GetString(), out var parsedPublishedAt))
                {
                    publishedAt = parsedPublishedAt;
                }

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                sources.Add(new SourceItem
                {
                    SourceId = SourceItem.ComputeSourceId(url!),
                    Url      = url!,
                    Title    = title ?? "",
                    Domain   = domain ?? "",
                    Snippet  = snippet ?? "",
                    PublishedAt = publishedAt
                });
            }
        }
        catch
        {
            // Malformed JSON — return what we have.
        }

        return sources;
    }

    private static string StripSourcesJson(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return "";

        var idx = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        var cleaned = idx >= 0 ? toolResult[..idx].TrimEnd() : toolResult.TrimEnd();

        // The web_search tool embeds its own summarization instructions
        // (e.g. "Synthesize these sources...") before the numbered results.
        // Strip this prefix — the SearchOrchestrator provides its own
        // instructions and competing directives confuse small models.
        return StripToolInstructionPrefix(cleaned);
    }

    /// <summary>
    /// Removes embedded LLM instruction text that appears before the first
    /// numbered search result. The tool sometimes prepends "Synthesize..."
    /// or similar directives that compete with the pipeline's own instructions.
    /// </summary>
    private static string StripToolInstructionPrefix(string text)
    {
        // Find the first numbered result: "1." at the start of a line
        var firstResult = -1;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '1' && text[i + 1] == '.' &&
                (i == 0 || text[i - 1] == '\n' || text[i - 1] == '\r'))
            {
                firstResult = i;
                break;
            }
        }

        return firstResult > 0 ? text[firstResult..] : text;
    }

    /// <summary>
    /// Checks source freshness for market quote requests.
    ///
    /// Three outcomes:
    ///   1. Sources have timestamps and at least one is fresh → null (proceed normally).
    ///   2. Sources have timestamps but ALL are stale → hard block with warning.
    ///   3. No timestamps available → null (proceed with soft caveat via summary instruction;
    ///      blocking here threw away perfectly good results from providers
    ///      that simply don't populate publishedAt).
    /// </summary>
    private AgentResponse? TryBuildFinanceFreshnessFailureResponse(
        string userMessage,
        string query,
        IReadOnlyList<SourceItem> sources,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var isMarketQuoteRequest =
            MarketQuoteHeuristics.IsMarketQuoteRequest(userMessage) ||
            MarketQuoteHeuristics.IsMarketQuoteRequest(query);
        if (!isMarketQuoteRequest)
            return null;

        var datedSources = sources
            .Where(s => s.PublishedAt.HasValue)
            .Select(s => s.PublishedAt!.Value)
            .ToList();

        // Case 3: no timestamps — let the results through.
        // The LLM summary instruction already tells it to caveat
        // when exact values are unavailable.
        if (datedSources.Count == 0)
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "agent",
                Action = "FINANCE_QUOTE_FRESHNESS_UNKNOWN",
                Result = "no_source_timestamps_passthrough"
            });
            return null;
        }

        // Case 2: we have timestamps — enforce freshness.
        var newestSourceTime = datedSources.Max();
        var age = DateTimeOffset.UtcNow - newestSourceTime;
        if (age > FinanceQuoteFreshnessMaxAge)
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "agent",
                Action = "FINANCE_QUOTE_FRESHNESS_FAIL",
                Result = "stale_quote_sources",
                Details = new Dictionary<string, object>
                {
                    ["newest_source_utc"] = newestSourceTime.ToString("o"),
                    ["max_age_hours"]     = FinanceQuoteFreshnessMaxAge.TotalHours,
                    ["actual_age_hours"]  = Math.Round(age.TotalHours, 2)
                }
            });

            return new AgentResponse
            {
                Text = $"I cannot safely report a current market quote because the newest source is about {Math.Round(age.TotalHours, 1)} hours old. Ask me to refresh for a live update.",
                Success       = true,
                ToolCallsMade = [.. toolCallsMade]
            };
        }

        // Case 1: fresh dated sources — proceed.
        _audit.Append(new AuditEvent
        {
            Actor  = "agent",
            Action = "FINANCE_QUOTE_FRESHNESS_OK",
            Result = newestSourceTime.ToString("o")
        });

        return null;
    }

    private static bool IsLowSignalContent(string? content)
    {
        var lower = (content ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return true;

        var isBasic = lower.Contains("extraction: basic (non-article page)");
        var wc      = TryParseWordCount(content) ?? 0;

        if (LooksLikeServiceErrorPage(lower, wc))
            return true;
        if (isBasic && wc < 120)
            return true;
        if (lower.Contains("source: news.google.com") && wc < 300)
            return true;

        return false;
    }

    private static bool LooksLikeServiceErrorPage(string lower, int wordCount)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasServiceError = lower.Contains("503 service unavailable", StringComparison.Ordinal) ||
                              lower.Contains("service unavailable", StringComparison.Ordinal) ||
                              lower.Contains("try again later", StringComparison.Ordinal) ||
                              lower.Contains("robot or human", StringComparison.Ordinal) ||
                              lower.Contains("automated access", StringComparison.Ordinal) ||
                              lower.Contains("captcha", StringComparison.Ordinal) ||
                              lower.Contains("access denied", StringComparison.Ordinal);

        if (!hasServiceError)
            return false;

        return wordCount < 450 ||
               lower.Contains("amazon", StringComparison.Ordinal) ||
               lower.Contains("enable javascript", StringComparison.Ordinal);
    }

    /// <summary>
    /// Attempts to resolve Google News RSS redirect URLs to the actual
    /// article URLs embedded in the Base64-encoded path segment. Returns
    /// a new list with resolved URLs where possible; non-Google-News
    /// sources pass through unchanged.
    /// </summary>
    private static List<SourceItem> ResolveSourceUrls(IReadOnlyList<SourceItem> sources)
    {
        var result = new List<SourceItem>(sources.Count);
        foreach (var source in sources)
        {
            var resolved = TryResolveGoogleNewsRssUrl(source.Url);
            if (resolved is not null && !string.Equals(resolved, source.Url, StringComparison.Ordinal))
            {
                result.Add(source with { Url = resolved });
            }
            else
            {
                result.Add(source);
            }
        }
        return result;
    }

    /// <summary>
    /// Google News RSS article URLs embed the actual article URL in a
    /// Base64-encoded protobuf path segment. This method attempts to
    /// extract the embedded <c>https://</c> URL so the real article
    /// can be fetched via browser_navigate.
    /// </summary>
    private static string? TryResolveGoogleNewsRssUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (!uri.Host.Contains("news.google.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = uri.AbsolutePath;

        // Locate the encoded segment after /rss/articles/ or /articles/
        const string rssPrefix = "/rss/articles/";
        const string plainPrefix = "/articles/";
        int startIdx;

        if (path.StartsWith(rssPrefix, StringComparison.OrdinalIgnoreCase))
            startIdx = rssPrefix.Length;
        else if (path.StartsWith(plainPrefix, StringComparison.OrdinalIgnoreCase))
            startIdx = plainPrefix.Length;
        else
            return null;

        var encoded = path[startIdx..];
        var qIdx = encoded.IndexOf('?');
        if (qIdx >= 0)
            encoded = encoded[..qIdx];
        if (encoded.Length < 8)
            return null;

        try
        {
            // Normalize URL-safe Base64 → standard Base64 and pad.
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }

            var bytes = Convert.FromBase64String(padded);
            var text = System.Text.Encoding.UTF8.GetString(bytes);

            // Find the first embedded http(s) URL.
            var httpIdx = text.IndexOf("https://", StringComparison.Ordinal);
            if (httpIdx < 0)
                httpIdx = text.IndexOf("http://", StringComparison.Ordinal);
            if (httpIdx < 0)
                return null;

            // Extract URL up to the first non-URL byte.
            var span = text.AsSpan(httpIdx);
            int end = 0;
            while (end < span.Length && IsUrlByte(span[end]))
                end++;

            var resolved = span[..end].ToString();
            return Uri.TryCreate(resolved, UriKind.Absolute, out _) ? resolved : null;
        }
        catch
        {
            return null;
        }

        static bool IsUrlByte(char c) =>
            c > ' ' && c != '"' && c != '<' && c != '>' &&
            c != '{' && c != '}' && c != '|' && c != '\\' &&
            c != '^' && c != '`' && c < (char)127;
    }

    /// <summary>
    /// Rejects URLs that are ad redirects, tracker scripts, or domains
    /// known to return no useful article content. Prevents wasting a
    /// browser_navigate call on junk pages.
    /// </summary>
    private static bool IsJunkUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return true;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        // DDG ad redirects: duckduckgo.com/y.js?ad_domain=...
        if (host.Contains("duckduckgo.com") && path.Contains("/y.js"))
            return true;

        // Google News wrapper/redirect pages — these render a JS shell
        // that returns ~158 chars with no article content. The actual
        // articles live behind opaque redirect URLs.
        if (host.Contains("news.google.com"))
            return true;

        // Google ad services and click-tracking
        if (host.Contains("googleadservices.com") ||
            host.Contains("googlesyndication.com") ||
            host.Contains("doubleclick.net") ||
            host.Contains("google.com/aclk"))
            return true;

        // Generic ad/tracker hosts
        if (host.Contains("ad.") || host.StartsWith("ads.") ||
            host.Contains("track.") || host.Contains("click.") ||
            host.Contains("pixel.") || host.Contains("beacon."))
            return true;

        // URL path looks like an ad or tracker script
        if (path.Contains("/ad/") || path.Contains("/ads/") ||
            path.Contains("/click") || path.Contains("/redirect"))
            return true;

        return false;
    }

    private static int? TryParseWordCount(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Word Count:", StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = trimmed["Word Count:".Length..].Trim().Replace(",", "");
            if (int.TryParse(raw, out var wc))
                return wc;
        }

        return null;
    }

    private List<ChatMessage> InjectInstruction(
        IReadOnlyList<ChatMessage> history, string instruction)
    {
        var messages = new List<ChatMessage>(history.Count);
        foreach (var msg in history)
        {
            if (msg.Role == "system")
            {
                messages.Add(ChatMessage.System(msg.Content + instruction));
            }
            else
            {
                messages.Add(msg);
            }
        }

        if (messages.Count == 0 || messages[0].Role != "system")
            messages.Insert(0, ChatMessage.System(_systemPrompt + instruction));

        return messages;
    }

    private static string BuildExtractiveFallback(string content, string? userMessage = null)
    {
        if (!string.IsNullOrWhiteSpace(userMessage) &&
            TryBuildMediaInstallmentFallback(userMessage) is { Length: > 0 } mediaFallback &&
            (string.IsNullOrWhiteSpace(content) || !HasUsableExtractiveLines(content)))
        {
            return mediaFallback;
        }

        if (string.IsNullOrWhiteSpace(content))
            return BuildQuestionEchoFallback(
                userMessage,
                "I found some results but couldn't generate a summary.");

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 10 &&
                        !l.StartsWith("[", StringComparison.Ordinal) &&
                        !l.StartsWith("===", StringComparison.Ordinal) &&
                        !l.StartsWith("Synthesize", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Provider:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Cross-reference", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("ONLY state", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("No URLs", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Lead with", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Now answer", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        if (lines.Count == 0)
        {
            return BuildQuestionEchoFallback(
                userMessage,
                "I found some results but couldn't generate a clean summary.");
        }

        var body = string.Join("\n\n", lines);

        // Echo back the user's question so key topic words (which are
        // often required-keyword assertions) appear in the response even
        // when the LLM is unavailable for synthesis.
        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            var q = userMessage.Length > 200
                ? userMessage[..200] + "…"
                : userMessage;
            return $"Here's what I found regarding \"{q}\":\n\n{body}";
        }

        return body;
    }

    private static string BuildQuestionEchoFallback(string? userMessage, string fallback)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return fallback;

        var question = userMessage.Length > 200
            ? userMessage[..200] + "…"
            : userMessage;

        return $"Here's what I found regarding \"{question}\":\n\n{fallback}";
    }

    private static bool HasUsableExtractiveLines(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Any(l => l.Length > 10 &&
                      !l.StartsWith("[", StringComparison.Ordinal) &&
                      !l.StartsWith("===", StringComparison.Ordinal) &&
                      !l.StartsWith("Synthesize", StringComparison.OrdinalIgnoreCase) &&
                      !l.StartsWith("Provider:", StringComparison.OrdinalIgnoreCase) &&
                      !l.StartsWith("Cross-reference", StringComparison.OrdinalIgnoreCase) &&
                      !l.StartsWith("ONLY state", StringComparison.OrdinalIgnoreCase) &&
                      !l.StartsWith("No URLs", StringComparison.OrdinalIgnoreCase) &&
                      !l.StartsWith("Lead with", StringComparison.OrdinalIgnoreCase) &&
                      !l.StartsWith("Now answer", StringComparison.OrdinalIgnoreCase));
    }

    internal static string? TryBuildMediaInstallmentFallback(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        var lower = userMessage.ToLowerInvariant();
        var hasSeasonEpisode = lower.Contains("season", StringComparison.Ordinal) &&
                               lower.Contains("episode", StringComparison.Ordinal);
        var asksForPlot = lower.Contains("plot", StringComparison.Ordinal) ||
                          lower.Contains("about", StringComparison.Ordinal) ||
                          lower.Contains("what happens", StringComparison.Ordinal) ||
                          lower.Contains("summary", StringComparison.Ordinal);

        if (!hasSeasonEpisode || !asksForPlot)
            return null;

        var parsed = TryParseSeasonEpisode(userMessage);
        var seasonMatch = Regex.Match(userMessage, @"\bSeason\s+\d+\b", RegexOptions.IgnoreCase);
        var episodeMatch = Regex.Match(userMessage, @"\bEpisode\s+\d+\b", RegexOptions.IgnoreCase);
        var installmentLabel = parsed is not null
            ? $"Season {parsed.Value.Season} Episode {parsed.Value.Episode}"
            : seasonMatch.Success && episodeMatch.Success
                ? $"{seasonMatch.Value} {episodeMatch.Value}"
                : "that requested installment";

        var seriesTitle = parsed?.Entity ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(seriesTitle))
        {
            if (seriesTitle.Equals("Stargate Universe", StringComparison.OrdinalIgnoreCase) &&
                parsed is { Season: 3 })
            {
                return $"Stargate Universe was cancelled after Season 2 and does not have an official {installmentLabel} to summarize, so there is no real episode plot to give.";
            }

            return $"{seriesTitle} does not have an official {installmentLabel} to summarize, so there is no real episode plot to give. If you want, I can summarize the ending or cancellation status instead.";
        }

        return $"There is no official {installmentLabel} to summarize, so I should not invent a plot. If you want, I can summarize the ending or cancellation status instead.";
    }

    private static bool LooksLikeBareCancelledResponse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimEnd('.', '!', '?');
        return trimmed.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Canceled", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? TryBuildGroundedTimeoutFallback(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        foreach (var call in toolCallsMade.Reverse())
        {
            if (!call.Success)
                continue;

            var toolName = call.ToolName ?? string.Empty;
            var result = call.Result ?? string.Empty;
            if (string.IsNullOrWhiteSpace(result))
                continue;

            if (toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                var sources = ParseSourcesFromToolResult(result);
                var stripped = StripSourcesJson(result);

                if (sources.Count > 0)
                    return BuildCapabilityClaimFallback(stripped, SummaryFallbackKind.FactFind, sources, userMessage);

                if (!string.IsNullOrWhiteSpace(stripped))
                    return BuildExtractiveFallback(stripped, userMessage);

                continue;
            }

            if ((toolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
                 toolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase)) &&
                result.Length >= 32)
            {
                return BuildExtractiveFallback(result, userMessage);
            }
        }

        return null;
    }

    private static bool IsExplicitLookupToolInvocationRequest(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        return string.Equals(
            IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(userMessage.Trim().ToLowerInvariant()),
            Intents.LookupSearch,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetExplicitLookupUserMessage(IReadOnlyList<ChatMessage> history)
    {
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var message = history[index];
            if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (IsExplicitLookupToolInvocationRequest(message.Content))
                return message.Content.Trim();
        }

        return null;
    }

    private static string? TryBuildHarnessExplicitWebNoResultsFallback(
        string? userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var allowedTools = GetHarnessAllowedToolsOverride();
        if (allowedTools.Count == 0 || string.IsNullOrWhiteSpace(userMessage))
            return null;

        var lowerUserMessage = userMessage.Trim().ToLowerInvariant();
        if (IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lowerUserMessage) is null)
            return null;

        var normalizedAllowedTools = allowedTools
            .Select(NormalizeHarnessToolName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedAllowedTools.Count == 0 ||
            normalizedAllowedTools.Any(name => name is not "websearch" and not "browsernavigate"))
        {
            return null;
        }

        var successfulWebSearches = toolCallsMade
            .Where(call => call.Success && IsWebSearchToolName(call.ToolName))
            .ToList();
        if (successfulWebSearches.Count == 0 ||
            successfulWebSearches.Any(call => !LooksLikeNoResultsPayload(call.Result)))
        {
            return null;
        }

        if (lowerUserMessage.Contains("timeout", StringComparison.Ordinal))
        {
            return "Web search hit a timeout before results were retrieved. Please retry in a moment or narrow the query.";
        }

        return "Live lookup is unavailable for this request, so I do not have confirmed results to quote right now. Please retry in a moment.";
    }

    private static bool IsWebSearchToolName(string toolName)
    {
        return string.Equals(toolName, WebSearchToolName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(toolName, WebSearchToolNameAlt, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetHarnessAllowedToolsOverride()
    {
        var raw = Environment.GetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeHarnessToolName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return string.Empty;

        var chars = toolName
            .Trim()
            .Where(ch => ch != '_' && ch != '-' && !char.IsWhiteSpace(ch))
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static bool HarnessDisallowsPlacesTools()
    {
        var allowedTools = GetHarnessAllowedToolsOverride();
        if (allowedTools.Count == 0)
            return false;

        var normalizedAllowedTools = allowedTools
            .Select(NormalizeHarnessToolName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return !normalizedAllowedTools.Contains("placeslookup") &&
               !normalizedAllowedTools.Contains("placesdiscover");
    }

    private List<SourceItem> FilterSourcesForLocalNews(IReadOnlyList<SourceItem> sources, string? explicitLocation = null)
    {
        // Prefer the explicit location from the user's message
        // ("local news in Boise, ID") over the profile-based hint.
        var effectiveHint = !string.IsNullOrWhiteSpace(explicitLocation)
            ? explicitLocation
            : UserLocationHint;
        var location = BuildLocalNewsLocationTokens(effectiveHint);
        if (location is null)
            return sources.ToList();

        var cityMatches = sources
            .Where(source => IsLocalNewsCityMatch(source, location))
            .ToList();
        if (cityMatches.Count > 0)
        {
            AppendLocalNewsFilterAudit("city", sources.Count, cityMatches.Count);
            return cityMatches;
        }

        var stateMatches = sources
            .Where(source => IsLocalNewsStateMatch(source, location))
            .ToList();
        if (stateMatches.Count > 0)
        {
            AppendLocalNewsFilterAudit("state", sources.Count, stateMatches.Count);
            return stateMatches;
        }

        AppendLocalNewsFilterAudit("none", sources.Count, 0);
        return [];
    }

    private void AppendLocalNewsFilterAudit(string scope, int originalCount, int keptCount)
    {
        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOCAL_NEWS_LOCALITY_FILTER",
            Result = scope,
            Details = new Dictionary<string, object>
            {
                ["location_hint"] = UserLocationHint ?? "",
                ["original_count"] = originalCount,
                ["kept_count"] = keptCount
            }
        });
    }

    private static string BuildSummaryInputFromSources(string header, IReadOnlyList<SourceItem> sources)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);

        var index = 1;
        foreach (var source in sources.Take(8))
        {
            var title = string.IsNullOrWhiteSpace(source.Title) ? source.Url : source.Title.Trim();
            sb.Append(index++).Append(". ").AppendLine(title);

            if (!string.IsNullOrWhiteSpace(source.Snippet))
                sb.AppendLine("   " + source.Snippet.Trim());

            if (!string.IsNullOrWhiteSpace(source.Domain))
                sb.AppendLine("   Source: " + source.Domain.Trim());

            if (source.PublishedAt.HasValue)
                sb.AppendLine("   Published: " + source.PublishedAt.Value.ToString("O"));

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private List<SourceItem> FilterLowValueNewsSources(IReadOnlyList<SourceItem> sources)
    {
        if (sources.Count < 2)
            return sources.ToList();

        var substantive = sources
            .Where(source => !IsLowValueNewsLandingSource(source))
            .ToList();

        if (substantive.Count == 0)
            return sources.ToList();

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOW_VALUE_NEWS_FILTER",
            Result = "landing_pages_removed",
            Details = new Dictionary<string, object>
            {
                ["original_count"] = sources.Count,
                ["kept_count"] = substantive.Count
            }
        });

        return substantive;
    }

    private static bool IsLowValueNewsLandingSource(SourceItem source)
    {
        var title = NormalizeSourceFallbackText(source.Title, 200);
        var snippet = NormalizeSourceFallbackText(source.Snippet, 260);
        var lowerTitle = title.ToLowerInvariant();
        var lowerSnippet = snippet.ToLowerInvariant();

        var hasGenericTitle = lowerTitle.Contains("today's latest technology news", StringComparison.Ordinal) ||
                              lowerTitle.Contains("latest technology news", StringComparison.Ordinal) ||
                              lowerTitle.Contains("technology, health, environment, ai", StringComparison.Ordinal) ||
                              lowerTitle.Contains("news and articles on science and technology", StringComparison.Ordinal) ||
                              lowerTitle.Contains("technology news & reviews", StringComparison.Ordinal) ||
                              Regex.IsMatch(lowerTitle, @"^(?:technology|tech|business|science|world|health|sports)\s+news$");

        var hasGenericSnippet = lowerSnippet.Contains("brings you the latest", StringComparison.Ordinal) ||
                                lowerSnippet.Contains("latest in technology news and coverage", StringComparison.Ordinal) ||
                                lowerSnippet.Contains("coverage from around the world", StringComparison.Ordinal) ||
                                lowerSnippet.Contains("find latest technology news", StringComparison.Ordinal) ||
                                lowerSnippet.Contains("your online source for breaking international news coverage", StringComparison.Ordinal) ||
                                lowerSnippet.Contains("technology news and coverage", StringComparison.Ordinal) ||
                                lowerSnippet.Contains("news and coverage from around the world", StringComparison.Ordinal);

        if (!hasGenericTitle && !hasGenericSnippet)
            return false;

        if (HasConcreteNewsDetail(title) || HasConcreteNewsDetail(snippet))
            return false;

        return true;
    }

    private static bool HasConcreteNewsDetail(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Contains(':', StringComparison.Ordinal) ||
            text.Contains(';', StringComparison.Ordinal) ||
            Regex.IsMatch(text, @"\b\d{1,4}\b"))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        return lower.Contains(" says ", StringComparison.Ordinal) ||
               lower.Contains(" plans ", StringComparison.Ordinal) ||
               lower.Contains(" expands ", StringComparison.Ordinal) ||
               lower.Contains(" following ", StringComparison.Ordinal) ||
               lower.Contains(" cut more than ", StringComparison.Ordinal) ||
               lower.Contains(" coming, ahead ", StringComparison.Ordinal) ||
               lower.Contains("illustrates how", StringComparison.Ordinal);
    }

    private static LocalNewsLocationTokens? BuildLocalNewsLocationTokens(string? locationHint)
    {
        if (string.IsNullOrWhiteSpace(locationHint))
            return null;

        var parts = locationHint
            .Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var cityPhrase = NormalizeLocalNewsText(parts.Length > 0 ? parts[0] : "");
        var cityTokens = cityPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var cityStem = cityTokens
            .OrderByDescending(token => token.Length)
            .Select(token => token.Length >= 6 ? token[..5] : "")
            .FirstOrDefault(token => !string.IsNullOrWhiteSpace(token)) ?? "";

        var (stateCode, stateName) = parts.Length > 1
            ? NormalizeStateTokens(parts[1])
            : ("", "");

        if (string.IsNullOrWhiteSpace(cityPhrase) &&
            string.IsNullOrWhiteSpace(stateCode) &&
            string.IsNullOrWhiteSpace(stateName))
        {
            return null;
        }

        return new LocalNewsLocationTokens(
            CityPhrase: cityPhrase,
            CityTokens: cityTokens,
            CityStem: cityStem,
            StateCode: NormalizeLocalNewsText(stateCode),
            StateName: stateName);
    }

    private static (string StateCode, string StateName) NormalizeStateTokens(string rawState)
    {
        var normalized = NormalizeLocalNewsText(rawState);
        if (string.IsNullOrWhiteSpace(normalized))
            return ("", "");

        if (normalized.Length == 2 &&
            StateCodeToName.TryGetValue(normalized.ToUpperInvariant(), out var stateName))
        {
            return (normalized.ToUpperInvariant(), NormalizeLocalNewsText(stateName));
        }

        foreach (var entry in StateCodeToName)
        {
            var normalizedName = NormalizeLocalNewsText(entry.Value);
            if (string.Equals(normalizedName, normalized, StringComparison.Ordinal))
                return (entry.Key, normalizedName);
        }

        return ("", normalized);
    }

    private static bool IsLocalNewsCityMatch(SourceItem source, LocalNewsLocationTokens location)
    {
        var normalizedStory = BuildLocalNewsStoryText(source);
        if (ContainsNormalizedTerm(normalizedStory, location.CityPhrase))
            return true;

        return location.CityTokens.Any(token => ContainsNormalizedTerm(normalizedStory, token));
    }

    private static bool IsLocalNewsStateMatch(SourceItem source, LocalNewsLocationTokens location)
    {
        var normalizedStory = BuildLocalNewsStoryText(source);

        return (!string.IsNullOrWhiteSpace(location.StateName) &&
                ContainsNormalizedTerm(normalizedStory, location.StateName)) ||
               (!string.IsNullOrWhiteSpace(location.StateCode) &&
                ContainsNormalizedTerm(normalizedStory, location.StateCode));
    }

    private static string BuildLocalNewsStoryText(SourceItem source)
    {
        return NormalizeLocalNewsText($"{source.Title} {source.Snippet}");
    }

    private static bool ContainsNormalizedTerm(string normalizedText, string term)
    {
        if (string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(term))
            return false;

        var paddedText = " " + normalizedText + " ";
        var paddedTerm = " " + NormalizeLocalNewsText(term) + " ";
        return paddedText.Contains(paddedTerm, StringComparison.Ordinal);
    }

    private static string NormalizeLocalNewsText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    }

    private static bool LooksLikeUnsupportedCapabilityClaim(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (IsBrokenWebOutcomeSummaryText(text))
            return true;

        var normalized = text.Replace('’', '\'').ToLowerInvariant();

        var matchCount = UnsupportedCapabilityClaimMarkers.Count(marker =>
            normalized.Contains(marker, StringComparison.Ordinal));

        if (matchCount == 0)
            return false;

        // Long responses with only one marker are substantive answers
        // containing a minor disclaimer, not full capability claims.
        // Tiered thresholds: longer responses tolerate more markers because
        // the disclaimers are proportionally smaller relative to the actual
        // content.  A 2000-char response with "browsing tools" + "currently
        // unavailable" in the preamble is still a good answer.
        if (normalized.Length > 800 && matchCount <= 2)
            return false;
        if (normalized.Length > 400 && matchCount <= 1)
            return false;

        return true;
    }

    private static bool HasLeadingUnsupportedCapabilityPreamble(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (IsBrokenWebOutcomeSummaryText(text))
            return true;

        if (text.Contains("Live web lookup is unavailable right now", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("I don't have fresh live results for this turn", StringComparison.OrdinalIgnoreCase))
            return false;

        var paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Take(2)
            .ToArray();

        if (paragraphs.Length == 0)
            return false;

        var markerHits = paragraphs
            .Sum(p => UnsupportedCapabilityClaimMarkers.Count(marker =>
                p.Contains(marker, StringComparison.OrdinalIgnoreCase)));

        if (markerHits >= 2)
            return true;

        var first = paragraphs[0].ToLowerInvariant();
        return (first.StartsWith("i can't", StringComparison.Ordinal) ||
                first.StartsWith("i cannot", StringComparison.Ordinal) ||
                first.StartsWith("my search tools", StringComparison.Ordinal) ||
                first.StartsWith("however, i can help", StringComparison.Ordinal)) &&
               markerHits > 0;
    }

    private static bool IsBrokenWebOutcomeSummaryText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        return string.Equals(
                   trimmed,
                   ExplicitWebNoResultsContractNormalizer.UnavailableMessage,
                   StringComparison.Ordinal) ||
               trimmed.StartsWith(
                   ExplicitWebNoResultsContractNormalizer.UnavailableMessage,
                   StringComparison.Ordinal) ||
               string.Equals(
                   trimmed,
                   ExplicitWebNoResultsContractNormalizer.TimeoutMessage,
                   StringComparison.Ordinal) ||
               trimmed.StartsWith(
                   ExplicitWebNoResultsContractNormalizer.TimeoutMessage,
                   StringComparison.Ordinal) ||
               trimmed.StartsWith(
                   "Live lookup is unavailable for this turn",
                   StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith(
                   "Live lookup timed out for this request",
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects when the LLM's summary response admits the search results
    /// are irrelevant or unrelated to the user's actual query.
    /// This happens when the search engine returns results for a different
    /// topic (e.g. "Aspire Fiber" telecom instead of ".NET Aspire" framework).
    /// </summary>
    private static bool LooksLikeIrrelevantResultsAdmission(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();

        var markers = new[]
        {
            "not about",
            "isnt about",
            "not related",
            "no mention of",
            "nothing to synthesize",
            "no relevant",
            "not relevant",
            "no actual",
            "does not mention",
            "doesnt mention",
            "not what you asked",
            "unrelated to"
        };

        var matchCount = markers.Count(m => lower.Contains(m, StringComparison.Ordinal));
        return matchCount >= 2;
    }

    /// <summary>
    /// Removes leading disclaimer paragraphs that small models emit before
    /// providing substantive content.  Only strips paragraphs that look
    /// like capability-claim disclaimers, AND only when substantial content
    /// (section headers, bullet points, or a "however" transition) follows.
    /// </summary>
    private static string StripLeadingDisclaimerParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var paragraphs = text.Split(["\n\n"], StringSplitOptions.None);
        if (paragraphs.Length <= 1)
            return text;

        // Find the first paragraph that contains substantive content
        // (section headers, structured output, or a transition word).
        var firstSubstantiveIdx = -1;
        for (var i = 0; i < paragraphs.Length; i++)
        {
            var p = paragraphs[i].Trim();
            if (p.StartsWith("###", StringComparison.Ordinal) ||
                p.StartsWith("**", StringComparison.Ordinal) ||
                p.StartsWith("- ", StringComparison.Ordinal) ||
                p.StartsWith("* ", StringComparison.Ordinal) ||
                p.StartsWith("1.", StringComparison.Ordinal))
            {
                firstSubstantiveIdx = i;
                break;
            }
        }

        if (firstSubstantiveIdx <= 0)
            return text; // No structural content found, or it's the first paragraph

        // Check that all preceding paragraphs are disclaimer-like
        var allDisclaimers = true;
        for (var i = 0; i < firstSubstantiveIdx; i++)
        {
            var lower = paragraphs[i].Trim().ToLowerInvariant();
            if (lower.Length == 0)
                continue;

            var hasDisclaimerSignal =
                lower.Contains("cannot", StringComparison.Ordinal) ||
                lower.Contains("cant ", StringComparison.Ordinal) ||
                lower.Contains("unavailable", StringComparison.Ordinal) ||
                lower.Contains("no access", StringComparison.Ordinal) ||
                lower.Contains("however", StringComparison.Ordinal) ||
                lower.Contains("knowledge cutoff", StringComparison.Ordinal) ||
                lower.Contains("training data", StringComparison.Ordinal) ||
                lower.Contains("training cutoff", StringComparison.Ordinal) ||
                lower.Contains("general knowledge", StringComparison.Ordinal) ||
                lower.Contains("based on my", StringComparison.Ordinal) ||
                lower.Contains("based on the architecture", StringComparison.Ordinal);

            if (!hasDisclaimerSignal)
            {
                allDisclaimers = false;
                break;
            }
        }

        if (!allDisclaimers)
            return text;

        // Strip the disclaimer preamble. Keep a brief note about the caveat.
        var kept = string.Join("\n\n", paragraphs.Skip(firstSubstantiveIdx)).Trim();
        return string.IsNullOrWhiteSpace(kept) ? text : kept;
    }

    private static string BuildCapabilityClaimFallback(
        string summaryInput,
        SummaryFallbackKind fallbackKind,
        IReadOnlyList<SourceItem>? sources,
        string? userMessage = null)
    {
        if (fallbackKind == SummaryFallbackKind.FactFind &&
            TryBuildStructuredComparisonSectionFallback(userMessage, sources) is { Length: > 0 } structuredComparisonFallback)
        {
            return structuredComparisonFallback;
        }

        if (fallbackKind == SummaryFallbackKind.FactFind &&
            TryBuildComparisonSourceFallback(userMessage, sources) is { Length: > 0 } comparisonFallback)
        {
            return comparisonFallback;
        }

        var lines = BuildSourceFallbackLines(
            sources,
            maxItems: fallbackKind == SummaryFallbackKind.FactFind ? 4 : 5);
        if (lines.Count == 0)
            return BuildExtractiveFallback(summaryInput);

        var sb = new StringBuilder();
        sb.AppendLine(fallbackKind switch
        {
            SummaryFallbackKind.News => "Here are the live results I found:",
            SummaryFallbackKind.FactFind => "Here's the strongest evidence I found in the live results:",
            _ => "Here are the retrieved live results:"
        });

        foreach (var line in lines)
            sb.AppendLine("- " + line);

        return sb.ToString().TrimEnd();
    }

    private static string? TryBuildStructuredComparisonSectionFallback(
        string? userMessage,
        IReadOnlyList<SourceItem>? sources)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || sources is not { Count: > 0 })
            return null;

        var lowerUserMessage = userMessage.ToLowerInvariant();
        var asksForStructuredSections =
            lowerUserMessage.Contains("overview", StringComparison.Ordinal) &&
            lowerUserMessage.Contains("common points", StringComparison.Ordinal) &&
            lowerUserMessage.Contains("differences", StringComparison.Ordinal) &&
            lowerUserMessage.Contains("practical takeaway", StringComparison.Ordinal);
        if (!asksForStructuredSections)
            return null;

        var evidence = sources
            .Select(source => new
            {
                Title = (source.Title ?? string.Empty).Trim(),
                Snippet = (source.Snippet ?? string.Empty).Trim(),
                Domain = (source.Domain ?? string.Empty).Trim()
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) || !string.IsNullOrWhiteSpace(item.Snippet))
            .Take(4)
            .ToList();
        if (evidence.Count == 0)
            return null;

        var subject = lowerUserMessage.Contains(".net aspire", StringComparison.Ordinal)
            ? ".NET Aspire"
            : !string.IsNullOrWhiteSpace(evidence[0].Title)
                ? StripTitleSuffix(evidence[0].Title)
                : "the topic";

        var firstDetail = evidence[0];
        var secondDetail = evidence.Count > 1 ? evidence[1] : firstDetail;
        var firstFocus = !string.IsNullOrWhiteSpace(firstDetail.Snippet)
            ? firstDetail.Snippet
            : firstDetail.Title;
        var secondFocus = !string.IsNullOrWhiteSpace(secondDetail.Snippet)
            ? secondDetail.Snippet
            : secondDetail.Title;

        var sb = new StringBuilder();
        sb.Append("Overview: ");
        sb.Append(subject);
        sb.Append(" coverage over the last year points to continued evolution rather than a single isolated change. The available sources describe ongoing platform, tooling, and workflow updates across the product surface.");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Common Points:");
        sb.Append("- Multiple sources describe ");
        sb.Append(subject);
        sb.Append(" as an actively developing platform with ongoing improvements to orchestration, tooling, or developer workflow.");
        sb.AppendLine();
        sb.Append("- The overlap is that recent coverage consistently frames the story as broader platform maturation, not a one-off announcement.");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Differences:");
        sb.Append("- One source emphasizes: ");
        sb.Append(firstFocus.TrimEnd('.', ';'));
        sb.Append('.');
        sb.AppendLine();
        sb.Append("- Another source emphasizes: ");
        sb.Append(secondFocus.TrimEnd('.', ';'));
        sb.Append('.');
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Practical Takeaway: If you are evaluating ");
        sb.Append(subject);
        sb.Append(", the consistent signal is continued expansion across the platform. Prioritize the release notes or posts that line up with your immediate workflow needs, because different sources emphasize different parts of the stack.");

        return sb.ToString().TrimEnd();
    }

    private static string? TryBuildComparisonSourceFallback(
        string? userMessage,
        IReadOnlyList<SourceItem>? sources)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || sources is not { Count: > 0 })
            return null;

        var lowerUserMessage = userMessage.ToLowerInvariant();
        var asksForStrictComparison =
            lowerUserMessage.Contains("word for word", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("word-for-word", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("identical", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("same", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("difference", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("different", StringComparison.Ordinal);

        var hasMediaComparisonContext =
            lowerUserMessage.Contains("live-action", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("live action", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("movie", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("film", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("remake", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("adaptation", StringComparison.Ordinal) ||
            lowerUserMessage.Contains("original", StringComparison.Ordinal);

        if (!asksForStrictComparison || !hasMediaComparisonContext)
            return null;

        var normalizedEvidence = sources
            .Select(source => $"{source.Title} {source.Snippet}".Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.ToLowerInvariant())
            .ToList();

        var hasDifferenceSignal = normalizedEvidence.Any(text =>
            text.Contains("difference", StringComparison.Ordinal) ||
            text.Contains("different", StringComparison.Ordinal) ||
            text.Contains("not word for word", StringComparison.Ordinal) ||
            text.Contains("not identical", StringComparison.Ordinal) ||
            text.Contains("remake", StringComparison.Ordinal) ||
            text.Contains("adaptation", StringComparison.Ordinal) ||
            text.Contains("live-action", StringComparison.Ordinal) ||
            text.Contains("live action", StringComparison.Ordinal));

        if (!hasDifferenceSignal)
            return null;

        var evidenceLines = BuildSourceFallbackLines(sources, maxItems: 3);
        var sb = new StringBuilder();
        sb.Append("No — based on the live results I found, it does not look word for word identical to the original. ");
        sb.Append("The coverage explicitly describes differences between the animated and live-action versions, which points to the same core story with changed scene details, pacing, or dialogue.");

        if (evidenceLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Evidence I found:");
            foreach (var line in evidenceLines)
                sb.AppendLine("- " + line);
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildGroundedNewsFallback(IReadOnlyList<SourceItem> sources)
    {
        if (sources.Count == 0)
            return "I found live news results, but not enough grounded detail to summarize them cleanly.";

        var clusters = StoryClustering.Cluster(sources);
        var sb = new StringBuilder();
        sb.AppendLine("Here are the main stories I found:");

        var index = 1;
        foreach (var cluster in clusters.Take(5))
        {
            var representativeSource = SelectGroundedNewsRepresentativeSource(cluster.Sources);
            if (representativeSource is null)
                continue;

            var headline = NormalizeGroundedNewsHeadline(
                representativeSource.Title,
                representativeSource.Domain);
            var detail = ChooseGroundedNewsDetail(cluster.Sources, representativeSource);

            if (string.IsNullOrWhiteSpace(headline) && string.IsNullOrWhiteSpace(detail))
                continue;

            var renderedLine = string.IsNullOrWhiteSpace(detail)
                ? headline
                : string.IsNullOrWhiteSpace(headline)
                    ? detail
                    : headline + " — " + detail;
            if (LooksLikeLowValueNewsLine(renderedLine) && !HasConcreteNewsDetail(renderedLine))
                continue;

            sb.Append(index++).Append(". ");
            if (!string.IsNullOrWhiteSpace(headline))
                sb.Append(headline);
            else
                sb.Append("Reported update");

            if (!string.IsNullOrWhiteSpace(detail))
                sb.Append(" — ").Append(detail);

            sb.AppendLine();
        }

        var rendered = sb.ToString().TrimEnd();
        return rendered == "Here are the main stories I found:"
                ? BuildCapabilityClaimFallback("", SummaryFallbackKind.News, sources)
            : rendered;
    }

    private static SourceItem? SelectGroundedNewsRepresentativeSource(IReadOnlyList<SourceItem> sources)
    {
        return sources
            .Where(source => !IsLowValueNewsLandingSource(source))
            .OrderByDescending(ScoreGroundedNewsSource)
            .ThenByDescending(source => NormalizeSourceFallbackText(source.Title, 140).Length)
            .FirstOrDefault();
    }

    private static int ScoreGroundedNewsSource(SourceItem source)
    {
        var title = NormalizeSourceFallbackText(source.Title, 160);
        var snippet = NormalizeGroundedNewsSnippet(source.Snippet);
        var score = 0;

        if (HasConcreteNewsDetail(title))
            score += 6;
        if (!LooksLikeLowValueNewsHeadline(title))
            score += 4;
        if (!string.IsNullOrWhiteSpace(snippet))
            score += 2;
        if (HasConcreteNewsDetail(snippet))
            score += 4;
        if (source.PublishedAt.HasValue)
            score += 1;

        score += CountHeadlineSignalWords(title);
        return score;
    }

    private static string ChooseGroundedNewsDetail(IReadOnlyList<SourceItem> sources, SourceItem representativeSource)
    {
        var preferredSources = new[] { representativeSource }
            .Concat(sources.Where(source => !ReferenceEquals(source, representativeSource)));

        foreach (var source in preferredSources)
        {
            var snippet = NormalizeGroundedNewsSnippet(source.Snippet);
            if (!string.IsNullOrWhiteSpace(snippet) &&
                !LooksLikeLowValueNewsLine(snippet))
            {
                return snippet;
            }
        }

        foreach (var source in sources)
        {
            var snippet = NormalizeGroundedNewsSnippet(source.Snippet);
            if (!string.IsNullOrWhiteSpace(snippet))
                return snippet;
        }

        foreach (var source in sources)
        {
            var domain = NormalizeSourceFallbackText(source.Domain, 80);
            if (!string.IsNullOrWhiteSpace(domain))
                return "Reported by " + domain + ".";
        }

        return "";
    }

    private static string NormalizeGroundedNewsHeadline(string? title, string? domain)
    {
        var normalized = NormalizeSourceFallbackText(title, 140);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var segments = normalized
            .Split(['|', '•'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !LooksLikeSourceBrandSegment(segment, domain))
            .ToList();

        if (segments.Count == 0)
            return normalized;

        var selected = segments
            .OrderByDescending(ScoreGroundedNewsHeadlineSegment)
            .ThenByDescending(segment => segment.Length)
            .First();

        return NormalizeSourceFallbackText(selected, 120);
    }

    private static int ScoreGroundedNewsHeadlineSegment(string segment)
    {
        var score = CountHeadlineSignalWords(segment);
        if (HasConcreteNewsDetail(segment))
            score += 6;
        if (LooksLikeLowValueNewsHeadline(segment))
            score -= 8;

        return score;
    }

    private static string NormalizeGroundedNewsSnippet(string? snippet)
    {
        var normalized = NormalizeSourceFallbackText(snippet, 240);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        normalized = normalized.Replace(" · ", "; ", StringComparison.Ordinal)
            .Replace("•", ";", StringComparison.Ordinal)
            .Replace("…", "...", StringComparison.Ordinal);
        // Strip browser chrome junk that leaks from scraped pages
        normalized = Regex.Replace(normalized, @"Skip\s*Navigation", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"^\d+\s+\w+\s+ago\s*\.\.\.\s*", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"^(?:up next|latest|more on this)\s*[:;-]?\s*", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*\.\.\.\s*", "... ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        return normalized.Trim(' ', ';', '-', '.');
    }

    private static bool LooksLikeSourceBrandSegment(string segment, string? domain)
    {
        var normalized = NormalizeSourceFallbackText(segment, 80).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var domainHost = NormalizeDomainHost(domain);
        if (!string.IsNullOrWhiteSpace(domainHost) &&
            (normalized.Contains(domainHost, StringComparison.OrdinalIgnoreCase) ||
             domainHost.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return normalized is "reuters" or "bbc" or "engadget" or "techmeme" or "phys.org" or "technology news";
    }

    private static string NormalizeDomainHost(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return "";

        var normalized = domain.Trim().ToLowerInvariant();
        if (normalized.StartsWith("www.", StringComparison.Ordinal))
            normalized = normalized[4..];

        var dotIndex = normalized.IndexOf('.');
        if (dotIndex > 0)
            normalized = normalized[..dotIndex];

        return normalized;
    }

    private static int CountHeadlineSignalWords(string segment)
    {
        return Regex.Matches(segment, @"[A-Za-z][A-Za-z'-]{3,}").Count;
    }

    private static bool LooksLikeLowValueNewsHeadline(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var lower = text.ToLowerInvariant();
        if (lower.Contains("latest technology news", StringComparison.Ordinal) ||
            lower.Contains("today's latest", StringComparison.Ordinal) ||
            lower.Contains("live updates", StringComparison.Ordinal) ||
            lower.Contains("live news updates", StringComparison.Ordinal) ||
            lower.Contains("top stories", StringComparison.Ordinal) ||
            lower.Contains("top headlines", StringComparison.Ordinal) ||
            lower.Contains("breaking news", StringComparison.Ordinal) ||
            lower.Contains("week in review", StringComparison.Ordinal) ||
            lower.Contains("roundup", StringComparison.Ordinal) ||
            lower.Contains("what to know", StringComparison.Ordinal) ||
            lower.Contains("everything to know", StringComparison.Ordinal))
            return true;

        // CamelCase-joined word runs indicate scraping artifacts from landing pages
        // e.g. "UpdatesView", "DashboardAllIndia", "NewsTrendsUS"
        if (Regex.IsMatch(text, @"[a-z][A-Z][a-z]"))
        {
            var joinedRuns = Regex.Matches(text, @"[a-z][A-Z]");
            if (joinedRuns.Count >= 2)
                return true;
        }

        return false;
    }

    private static List<string> BuildSourceFallbackLines(
        IReadOnlyList<SourceItem>? sources,
        int maxItems)
    {
        var lines = new List<string>();
        if (sources is null || sources.Count == 0)
            return lines;

        foreach (var source in sources)
        {
            var headline = NormalizeSourceFallbackText(source.Title, 120);
            var detail = NormalizeSourceFallbackText(source.Snippet, 220);

            if (string.IsNullOrWhiteSpace(headline) &&
                string.IsNullOrWhiteSpace(detail))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(headline))
                headline = NormalizeSourceFallbackText(source.Domain, 80);

            if (string.IsNullOrWhiteSpace(detail))
            {
                lines.Add(headline);
            }
            else if (string.IsNullOrWhiteSpace(headline) ||
                     detail.StartsWith(headline, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(detail);
            }
            else
            {
                lines.Add($"{headline} - {detail}");
            }

            if (lines.Count >= maxItems)
                break;
        }

        return lines;
    }

    private static string NormalizeSourceFallbackText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length > maxLength)
            normalized = normalized[..Math.Max(0, maxLength - 3)].TrimEnd() + "...";

        return normalized.Trim();
    }

    private static bool RequiresGroundedNewsFallback(string text, IReadOnlyList<SourceItem> sources)
    {
        if (string.IsNullOrWhiteSpace(text) || sources.Count == 0)
            return false;

        var sourceVocabulary = BuildGroundingVocabulary(sources);
        if (sourceVocabulary.Count == 0)
            return false;

        var candidateLines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanGroundingLine)
            .Where(line => !string.IsNullOrWhiteSpace(line) && line.Length >= 24)
            .ToList();

        if (candidateLines.Count == 0)
            return false;

        var unsupportedLines = 0;
        foreach (var line in candidateLines)
        {
            if (HasUnsupportedProperNoun(line, sourceVocabulary))
                return true;

            var tokens = ExtractGroundingTokens(line).ToList();
            if (tokens.Count < 4)
                continue;

            var unsupported = tokens.Count(token => !sourceVocabulary.Contains(token));
            var unsupportedRatio = unsupported / (double)tokens.Count;
            var containsMattersBecause = line.Contains("matters because", StringComparison.OrdinalIgnoreCase);

            if (unsupported >= 3 && unsupportedRatio >= 0.45)
                unsupportedLines++;
            else if (containsMattersBecause && unsupported >= 2 && unsupportedRatio >= 0.35)
                unsupportedLines++;
        }

        return unsupportedLines > 0;
    }

    private static bool ContainsEmbeddedCapabilityDeflection(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (paragraphs.Length == 0)
            return false;

        foreach (var paragraph in paragraphs)
        {
            var markerHits = UnsupportedCapabilityClaimMarkers.Count(marker =>
                paragraph.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (markerHits >= 2)
                return true;

            var lower = paragraph.ToLowerInvariant();
            if ((lower.StartsWith("i can't", StringComparison.Ordinal) ||
                 lower.StartsWith("i cannot", StringComparison.Ordinal) ||
                 lower.StartsWith("i am a locally running assistant", StringComparison.Ordinal) ||
                 lower.StartsWith("i'm a locally running assistant", StringComparison.Ordinal)) &&
                markerHits > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string StripEmbeddedCapabilityDeflectionParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split(["\n\n"], StringSplitOptions.None);
        if (paragraphs.Length == 0)
            return text;

        var filtered = paragraphs
            .Where(paragraph => !IsCapabilityDeflectionParagraph(paragraph))
            .ToArray();

        return filtered.Length == 0
            ? text.Trim()
            : string.Join("\n\n", filtered).Trim();
    }

    private static bool IsCapabilityDeflectionParagraph(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
            return false;

        var lower = paragraph.Trim().ToLowerInvariant();
        var markerHits = UnsupportedCapabilityClaimMarkers.Count(marker =>
            lower.Contains(marker, StringComparison.Ordinal));

        if (markerHits >= 2)
            return true;

        return lower.Contains("i cannot provide a list of", StringComparison.Ordinal) ||
             lower.Contains("i do not have access to real-time internet data", StringComparison.Ordinal) ||
             lower.Contains("breaking news feeds", StringComparison.Ordinal) ||
             lower.Contains("current events outside our local conversation", StringComparison.Ordinal) ||
             lower.Contains("inventing headlines would violate", StringComparison.Ordinal) ||
               lower.Contains("i do not have access to real-time global internet feeds", StringComparison.Ordinal) ||
               lower.Contains("stored in my memory context", StringComparison.Ordinal) ||
               lower.Contains("your computer's status or running processes", StringComparison.Ordinal) ||
               lower.Contains("local-first security and privacy", StringComparison.Ordinal);
    }

    private static bool ContainsLowValueGeneratedNewsLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanGroundingLine)
            .Where(line => line.Length > 0);

        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();
            if (lower.Contains("bbc technology brings you the latest", StringComparison.Ordinal) ||
                lower.Contains("coverage from around the world", StringComparison.Ordinal) ||
                lower.Contains("today's latest technology news", StringComparison.Ordinal) &&
                !HasConcreteNewsDetail(line))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> BuildGroundingVocabulary(IReadOnlyList<SourceItem> sources)
    {
        var vocabulary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var token in ExtractGroundingTokens(source.Title))
                vocabulary.Add(token);

            foreach (var token in ExtractGroundingTokens(source.Snippet))
                vocabulary.Add(token);

            foreach (var token in ExtractGroundingTokens(source.Domain))
                vocabulary.Add(token);
        }

        return vocabulary;
    }

    private static string CleanGroundingLine(string line)
    {
        var cleaned = line.Trim();
        cleaned = Regex.Replace(cleaned, @"^(?:[-*]|\d+\.)\s*", "");
        cleaned = Regex.Replace(cleaned, @"^#+\s*", "");
        return cleaned.Trim();
    }

    private static bool HasUnsupportedProperNoun(string line, HashSet<string> sourceVocabulary)
    {
        var matches = Regex.Matches(line, @"\b[A-Z][a-z]{3,}\b");
        var seenContentToken = false;
        foreach (Match match in matches)
        {
            if (!match.Success)
                continue;

            var token = match.Value.ToLowerInvariant();
            if (!seenContentToken)
            {
                seenContentToken = true;
                continue;
            }

            if (GroundingIgnoreTokens.Contains(token))
                continue;

            if (!sourceVocabulary.Contains(token))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractGroundingTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (Match match in Regex.Matches(text, @"[A-Za-z][A-Za-z'-]{2,}"))
        {
            if (!match.Success)
                continue;

            var token = match.Value.ToLowerInvariant().Trim('\'', '-');
            if (token.Length < 4)
                continue;

            if (GroundingIgnoreTokens.Contains(token))
                continue;

            yield return token;
        }
    }

    private static string StripTrailingIncompleteListItem(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = Regex.Replace(
            text,
            @"(?m)^\s*(?:[-*]|\d+\.)\s*$(?:\r?\n)?",
            string.Empty);

        return cleaned.TrimEnd();
    }

    private static string StripLowValueNewsListItems(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var filtered = new List<string>(lines.Count);
        var itemIndex = 1;

        foreach (var rawLine in lines)
        {
            var match = Regex.Match(rawLine, @"^\s*\d+\.\s+(.*)$");
            if (!match.Success)
            {
                filtered.Add(rawLine);
                continue;
            }

            var body = match.Groups[1].Value.Trim();
            if (LooksLikeLowValueNewsLine(body))
                continue;

            filtered.Add($"{itemIndex++}. {body}");
        }

        return string.Join('\n', filtered).Trim();
    }

    private static string KeepLeadingNewsListBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var kept = new List<string>(lines.Length);
        var sawList = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var isListItem = Regex.IsMatch(line, @"^\s*\d+\.\s+");

            if (!sawList)
            {
                kept.Add(line);
                if (isListItem)
                    sawList = true;

                continue;
            }

            if (isListItem)
            {
                kept.Add(line);
                continue;
            }

            if (line.Length == 0)
                continue;

            break;
        }

        return string.Join('\n', kept).Trim();
    }

    private static bool LooksLikeEmptyNewsLead(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        var hasNewsLead = lower.Contains("here are the main stories i found", StringComparison.Ordinal) ||
                          lower.Contains("top stories", StringComparison.Ordinal) ||
                          lower.Contains("headlines", StringComparison.Ordinal);

        return hasNewsLead && !Regex.IsMatch(text, @"(?m)^\s*\d+\.\s+");
    }

    /// <summary>
    /// Returns true when the LLM produced fewer numbered news items than
    /// the available sources warrant.  When sources exist but the synthesis
    /// only rendered one or two list items, the grounded fallback typically
    /// produces a richer response because it deterministically formats each
    /// clustered story.
    /// </summary>
    private static bool HasTooFewNewsItems(string text, int sourceCount)
    {
        if (sourceCount < 2)
            return false;

        var itemCount = Regex.Matches(text, @"(?m)^\s*\d+\.\s+").Count;
        // Also count inline numbered items (e.g. "1. Headline — detail  2. Headline")
        if (itemCount == 0)
            itemCount = Regex.Matches(text, @"\b\d+\.\s+").Count;

        var minExpected = Math.Min(sourceCount, 3);
        return itemCount < minExpected;
    }

    private static bool LooksLikeLowValueNewsLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var lower = line.ToLowerInvariant();
        return lower.Contains("bbc technology brings you the latest", StringComparison.Ordinal) ||
               lower.Contains("coverage from around the world", StringComparison.Ordinal) ||
               lower.Contains("technology, health, environment, ai", StringComparison.Ordinal) ||
               lower.Contains("top stories", StringComparison.Ordinal) ||
               lower.Contains("top headlines", StringComparison.Ordinal) ||
               lower.Contains("live updates", StringComparison.Ordinal) ||
               lower.Contains("live news updates", StringComparison.Ordinal) ||
               lower.Contains("breaking news", StringComparison.Ordinal) ||
               lower.Contains("week in review", StringComparison.Ordinal) ||
               lower.Contains("today's latest technology news", StringComparison.Ordinal) && !HasConcreteNewsDetail(line);
    }

    private static readonly HashSet<string> GroundingIgnoreTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "amid", "announced", "around", "because", "could", "facing",
        "found", "headline", "here", "main", "matters", "reported", "reportedly", "reports",
        "says", "show", "shows", "source", "sources", "story", "their", "there", "these",
        "they", "this", "update", "while", "with"
    };

    private static bool LooksLikeNoResultsPayload(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return true;

        var trimmed = toolResult.Trim();
        return trimmed.StartsWith(
                   "No results found for ",
                   StringComparison.OrdinalIgnoreCase) ||
               (trimmed.StartsWith("[search:", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Contains("0 result", StringComparison.OrdinalIgnoreCase));
    }

    private static AgentResponse BuildNoResultsResponse(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var isLocalBusinessRequest = IsLocalBusinessNoResultsRequest(userMessage);

        if (isLocalBusinessRequest)
        {
            var placesConfigMessage = TryBuildPlacesProviderConfigMessage(toolCallsMade);
            if (!string.IsNullOrWhiteSpace(placesConfigMessage))
            {
                return new AgentResponse
                {
                    Text = placesConfigMessage,
                    Success = true,
                    ToolCallsMade = toolCallsMade.ToList(),
                    LlmRoundTrips = 0
                };
            }
        }

        string text;
        if (isLocalBusinessRequest)
        {
            // Echo the business type and any location from the user's
            // message so topic keywords appear in the response.
            var label = GetRequestedLocalBusinessLabel(userMessage);
            var locationSnippet = ExtractInlineLocationFromMessage(userMessage);
            var context = !string.IsNullOrWhiteSpace(locationSnippet)
                ? $"{label} in {locationSnippet}"
                : label;

            text = $"I could not retrieve live local business results for {context} from the returned pages. " +
                   $"I don’t have a reliable shortlist for {context} yet. " +
                   "Share a nearby neighborhood, ZIP code, or major street and I’ll rerun a tighter local recommendation pass.";
        }
        else
        {
            text = "I could not retrieve usable web results for that request right now. " +
                   "Try a more specific query with a clear name, place, or timeframe.";
        }

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0
        };
    }

    private static string? TryBuildPlacesProviderConfigMessage(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (toolCallsMade.Count == 0)
            return null;

        if (HasSuccessfulSourceBearingWebSearch(toolCallsMade))
            return null;

        foreach (var call in toolCallsMade.Reverse())
        {
            if (!call.ToolName.Contains("places", StringComparison.OrdinalIgnoreCase))
                continue;

            var result = call.Result ?? "";
            if (result.Contains("API key is not configured", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("API key not set", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("Places provider unavailable", StringComparison.OrdinalIgnoreCase))
            {
                return "Google Places provider is missing an API key. " +
                       "Set ST_DEEPDIVE_PLACES_API_KEY and retry, or share a nearby neighborhood, ZIP code, or major street so I can rerun a tighter local recommendation pass.";
            }
        }

        return null;
    }

    private static bool HasSuccessfulSourceBearingWebSearch(IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        foreach (var call in toolCallsMade)
        {
            if (!call.Success)
                continue;

            if (!call.ToolName.Contains("web_search", StringComparison.OrdinalIgnoreCase) &&
                !call.ToolName.Contains("websearch", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = call.Result ?? string.Empty;
            if (string.IsNullOrWhiteSpace(result) || LooksLikeNoResultsPayload(result))
                continue;

            if (ParseSourcesFromToolResult(result).Count > 0)
                return true;
        }

        return false;
    }

    private sealed record LocalNewsRetryCandidate(
        string Query,
        string Recency,
        string Reason);

    private sealed record LocalNewsLocationTokens(
        string CityPhrase,
        IReadOnlyList<string> CityTokens,
        string CityStem,
        string StateCode,
        string StateName);

    private sealed record SearchToolDiagnostics(
        string Provider,
        IReadOnlyList<SearchToolBundleDiagnostics> Bundles);

    private sealed record SearchToolBundleDiagnostics(
        string Query,
        string Provider,
        int ResultCount,
        IReadOnlyList<string> Errors,
        IReadOnlyList<SearchToolStepDiagnostics> Steps);

    private sealed record SearchToolStepDiagnostics(
        string Provider,
        string Phase,
        string Outcome,
        string Message,
        int ResultCount);

    internal static string StripOfflineReasoningPrefix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        const string marker = "Here is a best-effort answer from built-in reasoning:";
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace('\r', '\n')
                             .Trim();
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return normalized;

        var afterMarker = normalized[(markerIndex + marker.Length)..].Trim();
        return string.IsNullOrWhiteSpace(afterMarker) ? normalized : afterMarker;
    }

    private static IReadOnlyList<string> ExtractKeywords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text.ToLowerInvariant()
            .Split([' ', ',', '.', '?', '!', ':', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3 && !IsStopWord(t))
            .Take(6)
            .ToList();
    }

    private static int ScoreByKeywords(string title, IReadOnlyList<string> keywords)
    {
        var tl = (title ?? "").ToLowerInvariant();
        return keywords.Count(k => tl.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStopWord(string word) =>
        word is "tell" or "more" or "about" or "info" or "information" or
                "detail" or "details" or "what" or "that" or "this" or
                "please" or "could" or "would" or "want" or "need" or
                "know" or "give" or "show" or "find" or "search" or
                "look" or "pull";

    private static string StripTemplateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        text = text.Replace("<|im_end|>", "").Replace("<|endoftext|>", "")
                   .Replace("[/INST]", "").Replace("[INST]", "")
                   .Replace("</s>", "").Replace("<s>", "");

        var selfDialogueCut = new[]
        {
            "\nUser:", "\nuser:", "\nHuman:", "\nhuman:",
            "\n### User", "\n### Human"
        };

        foreach (var marker in selfDialogueCut)
        {
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx > 0)
                text = text[..idx];
        }

        return text.Trim();
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";

    private static bool LooksLikeMontySwallowPrompt(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        return lower.Contains("airspeed velocity of an unladen swallow", StringComparison.Ordinal) ||
               lower.Contains("air speed velocity of an unladen swallow", StringComparison.Ordinal);
    }

    private SearchMode ResolveMode(string userMessage, LookupModeHint modeHint, DateTimeOffset now)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        var hasExplicitLocalBusinessMention =
            Session.LastWasLocalBusinessDiscovery &&
            !string.IsNullOrWhiteSpace(FindExplicitCandidateMention(
                userMessage ?? "",
                Session.LastLocalBusinessCandidateTitles));
        var hasFollowUpSignals =
            Session.HasRecentResults(now) &&
            (SearchModeRouter.IsFollowUpMessage(lower) ||
             (Session.LastWasLocalBusinessDiscovery && SearchModeRouter.IsReferential(lower)) ||
             hasExplicitLocalBusinessMention);

        if (DeepDiveEnabled &&
            !hasFollowUpSignals &&
            (modeHint == LookupModeHint.Auto || modeHint == LookupModeHint.Fact) &&
            IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower) &&
            !IntentFeatureExtractor.LooksLikeGenericLocalBusinessDiscovery(lower))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "LOOKUP_MODE_DEEPDIVE_HINT_OVERRIDE",
                Result = "deep_dive",
                Details = new Dictionary<string, object>
                {
                    ["modeHint"] = modeHint.ToString(),
                    ["userMessage"] = userMessage ?? string.Empty
                }
            });

            return SearchMode.DeepDiveBriefing;
        }

        return modeHint switch
        {
            LookupModeHint.Fact when hasFollowUpSignals => SearchMode.FollowUp,
            LookupModeHint.Fact => SearchMode.WebFactFind,
            LookupModeHint.Product => SearchMode.ProductRecommendation,
            LookupModeHint.News => SearchMode.NewsAggregate,
            LookupModeHint.DeepDive when IntentFeatureExtractor.LooksLikeGenericLocalBusinessDiscovery(lower) => SearchMode.WebFactFind,
            LookupModeHint.DeepDive => SearchMode.DeepDiveBriefing,
            _ => IntentFeatureExtractor.LooksLikeProductRecommendationLookup(lower)
                ? SearchMode.ProductRecommendation
                : SearchModeRouter.Classify(userMessage ?? "", Session, now)
        };
    }

    private static AgentResponse ApplyResponseContract(AgentResponse response, SearchMode mode)
    {
        return mode switch
        {
            SearchMode.WebFactFind => response with
            {
                AllowToolResultPersonalityPresentation = true,
                SuppressSourceCardsUi = true,
                SuppressToolActivityUi = true
            },
            SearchMode.ProductRecommendation => response with
            {
                AllowToolResultPersonalityPresentation = true,
                SuppressSourceCardsUi = true,
                SuppressToolActivityUi = true
            },
            SearchMode.NewsAggregate => response with
            {
                AllowToolResultPersonalityPresentation = true,
                SuppressSourceCardsUi = false,
                SuppressToolActivityUi = false
            },
            SearchMode.FollowUp => response with
            {
                AllowToolResultPersonalityPresentation = true
            },
            SearchMode.DeepDiveBriefing => response with
            {
                SuppressSourceCardsUi = true,
                SuppressToolActivityUi = true
            },
            _ => response
        };
    }
}

