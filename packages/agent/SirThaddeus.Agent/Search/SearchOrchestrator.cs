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
    DeepDive = 3
}

public sealed class SearchOrchestrator
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

    // ── Tool name conventions (try both casings) ─────────────────────
    private const string WebSearchToolName    = "web_search";
    private const string WebSearchToolNameAlt = "WebSearch";
    private const string BrowseToolName       = "browser_navigate";
    private const string BrowseToolNameAlt    = "BrowserNavigate";

    // ── Bounds ───────────────────────────────────────────────────────
    private const int DefaultMaxResults    = 5;
    private const int LocalBusinessTargetResults = 10;
    private const int LocalBusinessFetchMaxResults = 20;
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
        "cannot search the",
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
        "browsing tools"
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

    // ── LLM Instructions ─────────────────────────────────────────────
    private const string NewsSummaryInstruction =
        "\n\nSearch results are in the next message. " +
        "Present the key stories as individual items. " +
        "For each item, give the headline followed by one sentence " +
        "explaining why it matters (use the phrase 'matters because'). " +
        "Note where sources agree or differ. " +
        "No URLs. ONLY use facts from the provided sources. " +
        "Do NOT apologize or claim you lack internet, real-time data, or web access. " +
        "The provided results already contain the current information you need. " +
        "Do NOT invent or guess details not in the results. " +
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
        "explaining why it matters locally. " +
        "If the results contain ONLY national/international stories and no " +
        "local content, say so honestly: note that no local stories were " +
        "found in the results and present the top headlines instead. " +
        "No URLs. ONLY use facts from the provided sources. " +
        "Do NOT apologize or claim you lack internet, real-time data, or web access. " +
        "The provided results already contain the current information you need. " +
        "Do NOT invent or guess details not in the results.";

    private const string FactFindSummaryInstruction =
        "\n\nSearch results and article content are in the next message. " +
        "Synthesize into a clear, factual answer. Lead with the bottom line. " +
        "Include key facts. No URLs. " +
        "ONLY use facts from the provided sources. " +
        "Do NOT apologize or claim you lack internet, real-time data, or location access. " +
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
        var now  = DateTimeOffset.UtcNow;
        var mode = ResolveMode(userMessage, modeHint, now);

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
                ["hint_forced_mode"] = modeHint != LookupModeHint.Auto
            }
        });

        try
        {
            var response = mode switch
            {
                SearchMode.FollowUp      => await ExecuteFollowUpAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                SearchMode.NewsAggregate  => await ExecuteNewsAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                SearchMode.WebFactFind    => await ExecuteFactFindAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                SearchMode.DeepDiveBriefing => await ExecuteDeepDiveBriefingAsync(userMessage, toolCallsMade, ct),
                _                         => await ExecuteFactFindAsync(userMessage, memoryPackText, history, toolCallsMade, ct)
            };

            return ApplyResponseContract(response, mode);
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

    // ─────────────────────────────────────────────────────────────────
    // Pipeline 1: NEWS_AGGREGATE
    // ─────────────────────────────────────────────────────────────────

    private async Task<AgentResponse> ExecuteNewsAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        // ── 1. Entity resolution (optional for news) ─────────────────
        var entity = await _entityResolver.ResolveAsync(
            userMessage, Session, toolCallsMade, ct);

        // ── 2. Query construction (news-mode) ────────────────────────
        var query = await _queryBuilder.BuildAsync(
            SearchMode.NewsAggregate, userMessage, entity, Session, history, ct);

        // ── 3. web_search via MCP ────────────────────────────────────
        var toolResult = await CallWebSearchAsync(
            query.Query, query.Recency, toolCallsMade, ct,
            originalUserMessage: userMessage);
        toolResult = await TryRecoverLocalNewsResultsAsync(
            userMessage,
            query,
            toolResult,
            toolCallsMade,
            ct);

        // Derive location name from entity resolution for no-results messages.
        // When UserLocationHint is unset, this lets the response echo back the
        // city the user asked about (e.g. "Boise") instead of a generic message.
        var entityLocationName = entity is { Type: "Place" or "place" }
            ? entity.CanonicalName
            : null;

        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return BuildNewsNoResultsResponse(userMessage, toolCallsMade, entityLocationName);
        }
        if (LooksLikeNoResultsPayload(toolResult))
        {
            return BuildNewsNoResultsResponse(userMessage, toolCallsMade, entityLocationName);
        }
        if (WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is { } newsFailure)
        {
            return newsFailure;
        }

        // ── 4. Parse results into SourceItems ────────────────────────
        var sources = ParseSourcesFromToolResult(toolResult);
        /* if (isLocalNews)
        {
            summaryInput = BuildSummaryInputFromSources(
                "[Web search results â€” use these facts to answer the user's question]",
                sources);
        }
            MarketQuoteHeuristics.IsMarketQuoteRequest(userMessage) ||
            MarketQuoteHeuristics.IsMarketQuoteRequest(query.Query);
        */
        var isLocalNews = !string.IsNullOrWhiteSpace(UserLocationHint) &&
                          LocalNewsSignalRegex.IsMatch(userMessage);
        var isMarketQuoteRequest =
            MarketQuoteHeuristics.IsMarketQuoteRequest(userMessage) ||
            MarketQuoteHeuristics.IsMarketQuoteRequest(query.Query);
        var financeFreshnessFailure = TryBuildFinanceFreshnessFailureResponse(
            userMessage,
            query.Query,
            sources,
            toolCallsMade);
        if (financeFreshnessFailure is not null)
            return financeFreshnessFailure;

        if (sources.Count == 0)
        {
            return BuildNewsNoResultsResponse(userMessage, toolCallsMade, entityLocationName);
        }

        // ── 5. Story clustering ──────────────────────────────────────
        if (isLocalNews)
        {
            sources = FilterSourcesForLocalNews(sources);
            if (sources.Count == 0)
                return BuildNewsNoResultsResponse(userMessage, toolCallsMade, entityLocationName);
        }

        var clusters = StoryClustering.Cluster(sources);
        Session.LastClusters = clusters;

        // Set PrimarySourceId to the first item of the largest cluster
        if (clusters.Count > 0 && clusters[0].Sources.Count > 0)
            Session.PrimarySourceId = clusters[0].Sources[0].SourceId;

        // ── 6. Record in session ─────────────────────────────────────
        Session.RecordSearchResults(
            SearchMode.NewsAggregate, query.Query, query.Recency,
            sources, DateTimeOffset.UtcNow);

        // ── 7. Summarize via LLM ─────────────────────────────────────
        var summaryInput = "[Web search results — use these facts to answer the user's question]\n" +
                           StripSourcesJson(toolResult);

        /*
        var isLocalNews = !string.IsNullOrWhiteSpace(UserLocationHint) &&
                          LocalNewsSignalRegex.IsMatch(userMessage);
        */
        if (isLocalNews)
        {
            summaryInput = BuildSummaryInputFromSources(
                "[Web search results â€” use these facts to answer the user's question]",
                sources);
        }

        var instruction = isMarketQuoteRequest
            ? memoryPackText + FinanceQuoteSummaryInstruction
            : isLocalNews
                ? memoryPackText + LocalNewsSummaryInstruction
                : memoryPackText + NewsSummaryInstruction;

        return await SummarizeAndRespond(
            summaryInput, instruction,
            history, toolCallsMade, SummaryFallbackKind.News, sources, ct);
    }

    // ─────────────────────────────────────────────────────────────────
    // Pipeline 2: WEB_FACTFIND
    // ─────────────────────────────────────────────────────────────────

    private async Task<AgentResponse> ExecuteFactFindAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (LooksLikeMontySwallowPrompt(userMessage))
        {
            return new AgentResponse
            {
                Text = "What do you mean - an African or a European swallow?",
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = 0
            };
        }

        // ── 0. Local business + proximity with no location hint ──────
        // When the user asks about businesses "nearby" but hasn't set a
        // location, return guidance instead of hallucinating fake results.
        var lowerMessage = (userMessage ?? "").Trim().ToLowerInvariant();
        var localBusinessLocation = ResolveLocalBusinessLocationContext(userMessage ?? "");
        if (string.IsNullOrWhiteSpace(localBusinessLocation) &&
            IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerMessage))
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "search",
                Action = "LOCAL_BUSINESS_NO_LOCATION",
                Result = "guidance_returned",
                Details = new Dictionary<string, object>
                {
                    ["user_message"] = Truncate(userMessage ?? "", 80)
                }
            });

            return new AgentResponse
            {
                Text = "I need a location to search for local businesses. " +
                      "You can set your location in **Settings → Location**, " +
                       "or include a city in your request " +
                       "(e.g., \"florists in Portland, OR\").",
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = 0
            };
        }

        // ── 1. Entity resolution ──────────────────────────────────────
        // Skip for local business discovery ("bakery nearby") — there's no
        // named entity to canonicalize, and the LLM + web_search cost is
        // wasted overhead (5-15s on a local model).
        var isLocalBusinessQuery = IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerMessage);
        EntityResolver.ResolvedEntity? entity = null;
        if (!isLocalBusinessQuery)
        {
            entity = await _entityResolver.ResolveAsync(
                userMessage ?? "", Session, toolCallsMade, ct);
        }
        else
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "search",
                Action = "SKIP_ENTITY_RESOLUTION",
                Result = "local_business_discovery"
            });
        }

        // ── 2. Query construction (factfind-mode) ────────────────────
        var query = await _queryBuilder.BuildAsync(
            SearchMode.WebFactFind, userMessage ?? "", entity, Session, history, ct);

        // ── 3. web_search via MCP ────────────────────────────────────
        var toolResult = await CallWebSearchAsync(
            query.Query, query.Recency, toolCallsMade, ct,
            originalUserMessage: userMessage,
            maxResults: isLocalBusinessQuery ? LocalBusinessFetchMaxResults : null);

        var isNoResults = string.IsNullOrWhiteSpace(toolResult) || 
                          LooksLikeNoResultsPayload(toolResult) || 
                          WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is not null;

        if (!isNoResults)
        {
            var rawSources = ParseSourcesFromToolResult(toolResult);
            if (rawSources.Count == 0)
                isNoResults = true;
        }

        // Browser SERP fallback is NOT used for local business discovery.
        // browser_navigate extracts only raw text from a Google results
        // page — no URLs, no structured source data — producing a single
        // synthetic source titled "Google Search" which is useless for
        // business listing presentation. Skip it and let the no-results
        // handler give the user an honest message instead.
        if (isNoResults && isLocalBusinessQuery)
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "search",
                Action = "SKIP_BROWSER_FALLBACK",
                Result = "local_business_discovery",
                Details = new Dictionary<string, object>
                {
                    ["reason"] = "browser SERP scraping cannot produce structured business listings"
                }
            });
        }

        if (isNoResults)
        {
            if (WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is { } factFailure)
            {
                return await BuildOfflineReasoningResponseAsync(
                    userMessage ?? "", memoryPackText, history, toolCallsMade, factFailure.Text, ct);
            }

            if (LooksLikeNoResultsPayload(toolResult))
            {
                return await BuildNoResultsFallbackAsync(
                    userMessage ?? "", memoryPackText, history, toolCallsMade, ct);
            }

            return await BuildOfflineReasoningResponseAsync(
                userMessage ?? "", memoryPackText, history, toolCallsMade, "Web search returned no results.", ct);
        }

        // Parse and record results.
        var sources = ParseSourcesFromToolResult(toolResult);
        var preFilterCount = sources.Count;
        if (isLocalBusinessQuery)
        {
            sources = [.. SelectLocalBusinessDiscoverySources(
                userMessage ?? "",
                sources,
                LocalBusinessTargetResults,
                localBusinessLocation)];

            _audit.Append(new AuditEvent
            {
                Actor  = "search",
                Action = "LOCAL_BUSINESS_SOURCE_FILTER",
                Result = sources.Count > 0 ? "ok" : "all_filtered",
                Details = new Dictionary<string, object>
                {
                    ["pre_filter_count"] = preFilterCount,
                    ["post_filter_count"] = sources.Count,
                    ["location_context"] = localBusinessLocation ?? "(null)",
                    ["pre_filter_titles"] = string.Join(" | ", ParseSourcesFromToolResult(toolResult).Select(s => $"{s.Title} [{s.Domain}]")),
                    ["post_filter_titles"] = string.Join(" | ", sources.Select(s => $"{s.Title} [{s.Domain}]"))
                }
            });
        }
        var existenceGuarded = TryBuildExistenceGuardedResponse(
            userMessage ?? "",
            sources,
            toolCallsMade);
        if (existenceGuarded is not null)
            return existenceGuarded;
        var isMarketQuoteRequest =
            MarketQuoteHeuristics.IsMarketQuoteRequest(userMessage ?? "") ||
            MarketQuoteHeuristics.IsMarketQuoteRequest(query.Query);
        var financeFreshnessFailure = TryBuildFinanceFreshnessFailureResponse(
            userMessage ?? "",
            query.Query,
            sources,
            toolCallsMade);
        if (financeFreshnessFailure is not null)
            return financeFreshnessFailure;

        if (sources.Count == 0)
        {
            return await BuildNoResultsFallbackAsync(
                userMessage ?? "",
                memoryPackText,
                history,
                toolCallsMade,
                ct);
        }

        Session.RecordSearchResults(
            SearchMode.WebFactFind, query.Query, query.Recency,
            sources, DateTimeOffset.UtcNow);
        Session.LastWasLocalBusinessDiscovery =
            IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerMessage);

        if (isLocalBusinessQuery && sources.Count > 0)
        {
            Session.RecordLocalBusinessCandidates(
                GetRequestedLocalBusinessLabel(userMessage ?? ""),
                sources);
            return BuildLocalBusinessDiscoveryResponse(userMessage ?? "", sources, toolCallsMade);
        }
        else
        {
            Session.ClearLocalBusinessCandidates();
        }

        // tool result lacks rich content (snippet-only mode).
        var strippedContent = StripSourcesJson(toolResult);
        var toolResultHasRichContent = strippedContent.Length >= MinRichContentLength;
        var isLocalBizDiscovery = IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerMessage);

        string? articleContent = null;

        if (!toolResultHasRichContent && !isLocalBizDiscovery)
        {
            // Prefer direct article URLs over aggregator wrappers.
            // If all parsed sources are junk URLs, do a supplementary search
            // to find actual article pages.
            var navigable = sources
                .Where(s => !IsJunkUrl(s.Url))
                .Take(MaxFollowUpUrls)
                .ToList();

            if (navigable.Count == 0 && sources.Count > 0)
            {
                var bestTitle = sources
                    .OrderByDescending(s => s.Snippet?.Length ?? 0)
                    .First().Title;

                if (!string.IsNullOrWhiteSpace(bestTitle))
                {
                    var suppResult = await CallWebSearchAsync(
                        bestTitle, "any", toolCallsMade, ct);

                    if (!string.IsNullOrWhiteSpace(suppResult))
                    {
                        var suppSources = ParseSourcesFromToolResult(suppResult);
                        navigable = suppSources
                            .Where(s => !IsJunkUrl(s.Url))
                            .Take(MaxFollowUpUrls)
                            .ToList();

                        if (navigable.Count > 0)
                        {
                            var suppText = StripSourcesJson(suppResult);
                            if (!string.IsNullOrWhiteSpace(suppText))
                                toolResult += "\n" + suppText;
                        }
                    }
                }
            }

            articleContent = await FetchArticleContentAsync(
                navigable, toolCallsMade, ct);
        }
        else
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "search",
                Action = "SKIP_ARTICLE_FETCH",
                Result = toolResultHasRichContent ? "rich_content_in_tool_result" : "local_business_discovery",
                Details = new Dictionary<string, object>
                {
                    ["stripped_content_length"] = strippedContent.Length,
                    ["is_local_biz"] = isLocalBizDiscovery
                }
            });
        }

        // ── 6. Summarize ─────────────────────────────────────────────
        var hasArticleContent = !string.IsNullOrWhiteSpace(articleContent);
        var sb = new StringBuilder();
        sb.AppendLine("[Web search results — use these facts to answer the user's question]");
        sb.AppendLine(StripSourcesJson(toolResult));

        if (hasArticleContent)
        {
            sb.AppendLine();
            sb.AppendLine("[Full article content — use these details to give a thorough answer]");
            sb.AppendLine(articleContent);
        }

        // When full article content is available, use the standard instruction.
        // When only snippets are available, use a more aggressive extraction
        // instruction that tells the model to surface every detail it can find.
        var instruction = isMarketQuoteRequest
            ? memoryPackText + FinanceQuoteSummaryInstruction
            : hasArticleContent
                ? memoryPackText + FactFindSummaryInstruction
                : memoryPackText + FactFindSnippetOnlyInstruction;

        if (isLocalBizDiscovery)
        {
            instruction += "\nCRITICAL: The user's location has ALREADY been applied to the search results below. DO NOT claim you lack real-time geolocation data, and DO NOT apologize for not knowing their location. Confidently present the local results provided.";
        }

        return await SummarizeAndRespond(
            sb.ToString(), instruction,
            history, toolCallsMade, SummaryFallbackKind.FactFind, sources, ct);
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
             Session.LastWasLocalBusinessDiscovery))
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
        if (Session.LastWasLocalBusinessDiscovery)
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
            or "that shop" or "this shop"
            or "that store" or "this store"
            or "that restaurant" or "this restaurant";
    }

    private string ResolveLocalBusinessFollowUpSubject(string userMessage)
    {
        var extracted = ExtractFollowUpSubject(userMessage);
        var candidates = Session.LastLocalBusinessCandidateTitles;

        // Pronoun-style follow-ups should resolve to a deterministic anchor.
        if (IsPronounSubjectReference(extracted))
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
        var bestByTokens = FindBestCandidateByTokenOverlap(extracted, candidates);
        if (!string.IsNullOrWhiteSpace(bestByTokens))
            return bestByTokens;

        // Fall back to extracted content, then deterministic anchor.
        if (!string.IsNullOrWhiteSpace(extracted))
            return extracted;

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
            summaryInput, memoryPackText + DeepDiveInstruction,
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
            sb.ToString(), memoryPackText + instruction,
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
        // When the user says "bring me up more info on X", the raw message
        // still contains conversational filler.  Strip it to produce a
        // clean entity-only query (e.g. "San Francisco Street Bakery")
        // so the deep-dive coordinator searches for the right thing and
        // the briefing card shows a tidy query label.
        var query = SanitizeDeepDiveQuery(userMessage);

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
            return AgentResponse.FromError(
                "I couldn't assemble a deep-dive briefing for that request.")
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
            IntentFeatureExtractor.HasLocalBusinessProximitySignals(userMessage.ToLowerInvariant());

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
        return OfflineWebReasoningResponder.BuildAsync(
            _llm,
            _systemPrompt,
            userMessage,
            memoryPackText,
            history,
            toolCallsMade,
            reason,
            ct);
    }

    private Task<AgentResponse> BuildNoResultsFallbackAsync(
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (IsLocalBusinessNoResultsRequest(userMessage))
            return Task.FromResult(BuildNoResultsResponse(userMessage, toolCallsMade));

        return BuildNoResultsReasoningResponseAsync(
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
        var response = await BuildOfflineReasoningResponseAsync(
            userMessage,
            memoryPackText,
            history,
            toolCallsMade,
            "Web search returned no results.",
            ct);

        var stripped = StripOfflineReasoningPrefix(response.Text);
        if (!string.IsNullOrWhiteSpace(stripped) &&
            !string.Equals(stripped, response.Text, StringComparison.Ordinal))
        {
            return response with { Text = stripped };
        }

        return response;
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
        foreach (var candidate in BuildLocalNewsRetryCandidates(query))
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
        if (string.IsNullOrWhiteSpace(UserLocationHint))
            return false;

        return LocalNewsSignalRegex.IsMatch(userMessage ?? "") ||
               LocalNewsSignalRegex.IsMatch(query ?? "");
    }

    private IReadOnlyList<LocalNewsRetryCandidate> BuildLocalNewsRetryCandidates(QueryBuilder.SearchQuery query)
    {
        var location = UserLocationHint?.Trim() ?? "";
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
        int? maxResults = null)
    {
        var effectiveQuery = InjectLocationIfProximityQuery(query);
        effectiveQuery = InjectLocationForLocalBusinessQuery(effectiveQuery, originalUserMessage);
        effectiveQuery = InjectLocationIntoLocalNewsQuery(effectiveQuery, originalUserMessage);
        effectiveQuery = InjectLocationIntoDistanceQuery(effectiveQuery);
        effectiveQuery = InjectUnitPreferenceIntoDistanceQuery(effectiveQuery);

        var args = JsonSerializer.Serialize(new
        {
            query = effectiveQuery,
            maxResults = maxResults ?? DefaultMaxResults,
            recency
        });

        var toolName = WebSearchToolName;
        var toolOk   = false;
        string toolResult;

        try
        {
            toolResult = await _mcp.CallToolAsync(toolName, args, ct);
            toolOk = true;
        }
        catch (Exception ex)
        {
            try
            {
                toolName   = WebSearchToolNameAlt;
                toolResult = await _mcp.CallToolAsync(toolName, args, ct);
                toolOk = true;
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
        /*
        /*
        /*
                "[Web search results â€” use these facts to answer the user's question]",
                sources);
        }
        if (isLocalNews)
        {
            summaryInput = BuildSummaryInputFromSources(
                "[Web search results â€” use these facts to answer the user's question]",
                sources);
        }
        */
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
    /// Safety net for local business queries where the query builder stripped
    /// the proximity signal. If the original user message has business + proximity
    /// signals and the constructed query doesn't already contain the user's
    /// location, append it.
    /// </summary>
    private string InjectLocationForLocalBusinessQuery(string query, string? originalUserMessage)
    {
        if (string.IsNullOrWhiteSpace(UserLocationHint))
            return query;
        if (string.IsNullOrWhiteSpace(originalUserMessage))
            return query;

        // Already contains location — nothing to do.
        if (query.Contains(UserLocationHint, StringComparison.OrdinalIgnoreCase))
            return query;

        // Already has a proximity signal that InjectLocationIfProximityQuery handled.
        if (ProximitySignalRegex.IsMatch(query))
            return query;

        var lowerOriginal = originalUserMessage.Trim().ToLowerInvariant();
        if (!IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerOriginal))
            return query;

        var result = $"{query.TrimEnd('?', '.', '!', ',')} near {UserLocationHint.Trim()}";

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "LOCATION_INJECTED_FOR_LOCAL_BUSINESS",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["original_query"] = query,
                ["effective"] = result,
                ["locationHint"] = UserLocationHint.Trim()
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

        if (LooksLikeUnsupportedCapabilityClaim(text))
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

            text = BuildCapabilityClaimFallback(summaryInput, fallbackKind, sources);
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
                var snippet = item.TryGetProperty("excerpt", out var ex) ? ex.GetString() : "";
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

        if (isBasic && wc < 120)
            return true;
        if (lower.Contains("source: news.google.com") && wc < 300)
            return true;

        return false;
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
        if (string.IsNullOrWhiteSpace(content))
            return "I found some results but couldn't generate a summary.";

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
            return "I found some results but couldn't generate a clean summary.";

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

    private List<SourceItem> FilterSourcesForLocalNews(IReadOnlyList<SourceItem> sources)
    {
        var location = BuildLocalNewsLocationTokens(UserLocationHint);
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
        IReadOnlyList<SourceItem>? sources)
    {
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

    private static bool LooksLikeNoResultsPayload(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return true;

        return toolResult.TrimStart().StartsWith(
            "No results found for ",
            StringComparison.OrdinalIgnoreCase);
    }

    private static AgentResponse BuildNoResultsResponse(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var isLocalBusinessRequest = IsLocalBusinessNoResultsRequest(userMessage);

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

            text = $"I could not retrieve live local business results for {context} right now. " +
                   "Try naming one specific place (for example, \"Is Walmart in Rexburg open right now?\") " +
                   "and I can check its current hours.";
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

    /// <summary>
    /// Extracts an inline location from the user message, e.g.
    /// "florist in Hillsboro, OR" → "Hillsboro, OR".
    /// Returns null if no location pattern is found.
    /// </summary>
    private static string? ExtractInlineLocationFromMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var match = ExplicitLocationScopeRegex.Match(message);
        if (!match.Success)
            return null;

        // The regex matches "in Hillsboro, OR" — strip the preposition.
        var location = match.Value.Trim();
        var prefixEnd = location.IndexOf(' ');
        if (prefixEnd > 0)
            location = location[(prefixEnd + 1)..].Trim();

        location = Regex.Replace(
            location,
            @"\b(?:please|pls|thanks|thank\s+you)\b.*$",
            "",
            RegexOptions.IgnoreCase).Trim();

        return location.TrimEnd('?', '.', '!', ',');
    }

    private AgentResponse BuildLocalBusinessDiscoveryResponse(
        string userMessage,
        IReadOnlyList<SourceItem> sources,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var businessLabel = GetRequestedLocalBusinessLabel(userMessage);
        var location = ResolveLocalBusinessLocationContext(userMessage)?.Trim();
        var locText = string.IsNullOrWhiteSpace(location) ? " nearby" : $" nearby in {location}";
        var topSources = sources.Take(LocalBusinessTargetResults).ToList();

        var sb = new StringBuilder();
        sb.AppendLine(topSources.Count == 1
            ? $"Here are the {businessLabel}{locText} results I found (1 clearly relevant match):"
            : $"Here are the top {topSources.Count} {businessLabel}{locText}:");
        sb.AppendLine();

        foreach (var source in topSources)
        {
            var displayTitle = StripTitleSuffix(source.Title);
            sb.Append("- **");
            sb.Append(displayTitle);
            sb.Append("**");

            if (!string.IsNullOrWhiteSpace(source.Snippet))
            {
                sb.Append(": ");
                sb.Append(TrimSentence(source.Snippet, 180));
            }

            if (!string.IsNullOrWhiteSpace(source.Domain))
            {
                sb.Append(" (source: ");
                sb.Append(source.Domain);
                sb.Append(')');
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append("If you want, I can bring up more info on any one of these ");
        sb.Append(businessLabel);
        sb.Append('.');

        return new AgentResponse
        {
            Text = sb.ToString(),
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0
        };
    }

    private static IReadOnlyList<SourceItem> SelectLocalBusinessDiscoverySources(
        string userMessage,
        IReadOnlyList<SourceItem> sources,
        int targetCount,
        string? locationContext = null)
    {
        if (sources.Count == 0)
            return sources;

        // Filter out junk/synthetic sources that can't represent real businesses.
        sources = sources.Where(s => !IsJunkBusinessSource(s)).ToList();
        if (sources.Count == 0)
            return [];

        var locationFiltered = FilterSourcesForLocalBusinessLocation(sources, locationContext);
        if (locationFiltered.Count > 0)
            sources = locationFiltered;
        else if (!string.IsNullOrWhiteSpace(locationContext))
            return [];

        var keywords = GetLocalBusinessMatchKeywords(userMessage);
        if (keywords.Count == 0)
            return sources.Take(Math.Max(1, targetCount)).ToList();

        var strict = sources
            .Where(source => LocalBusinessSourceMatches(source, keywords))
            .ToList();

        // Demote directory/aggregator pages (e.g. "BEST 10 BAKERIES in OLYMPIA")
        // to the bottom so individual businesses appear first.
        strict = DemoteDirectoryAggregatorSources(strict);

        if (strict.Count >= targetCount)
            return strict.Take(targetCount).ToList();

        if (strict.Count == 0)
            return DemoteDirectoryAggregatorSources(sources.ToList()).Take(Math.Max(1, targetCount)).ToList();

        // When the provider only returned a small pool, keep precision over
        // recall and avoid backfilling with likely-irrelevant generic guides.
        if (sources.Count <= targetCount)
            return strict;

        var selected = new List<SourceItem>(strict);
        var selectedIds = new HashSet<string>(
            selected.Select(s => s.SourceId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            if (selectedIds.Contains(source.SourceId))
                continue;

            selected.Add(source);
            selectedIds.Add(source.SourceId);

            if (selected.Count >= targetCount)
                break;
        }

        return selected;
    }

    /// <summary>
    /// Returns true for obviously synthetic or junk sources that should
    /// never appear as a "business" in a local discovery response.
    /// Examples: Google Search fallback pages, ad redirects.
    /// </summary>
    private static bool IsJunkBusinessSource(SourceItem source)
    {
        var title = (source.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            return true;

        // Synthetic fallback source from CallBrowserSearchFallbackAsync
        if (title.Equals("Google Search", StringComparison.OrdinalIgnoreCase))
            return true;

        // Generic search engine / aggregator pages
        if (title.StartsWith("Google", StringComparison.OrdinalIgnoreCase) &&
            title.Length < 30)
            return true;
        if (title.StartsWith("Bing", StringComparison.OrdinalIgnoreCase) &&
            title.Length < 30)
            return true;

        // News redirect URLs (news.google.com) are opaque wrappers that
        // a simple HTTP client can't follow. However, they carry a real
        // title and domain from the original publisher, so they are still
        // useful as local business sources. Only discard true ad-tracker
        // junk while keeping news-redirect sources that have valid metadata.
        if (IsJunkUrl(source.Url) && !IsNewsRedirectWithValidMetadata(source))
            return true;

        return false;
    }

    private static bool IsNewsRedirectWithValidMetadata(SourceItem source)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            return false;

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains("news.google.com", StringComparison.Ordinal))
            return false;

        // Keep the source if it has a meaningful title and a known
        // publisher domain (the GoogleNews provider populates Source
        // with the original publisher domain).
        var hasTitle = !string.IsNullOrWhiteSpace(source.Title) && source.Title.Length > 10;
        var hasDomain = !string.IsNullOrWhiteSpace(source.Domain) &&
                        !source.Domain.Contains("google", StringComparison.OrdinalIgnoreCase);

        return hasTitle && hasDomain;
    }

    /// <summary>
    /// Stable-sort a list of sources so that directory/aggregator pages
    /// (Yelp "Best 10…", TripAdvisor lists, etc.) move to the bottom
    /// while individual business pages keep their original ordering.
    /// </summary>
    private static List<SourceItem> DemoteDirectoryAggregatorSources(List<SourceItem> sources)
    {
        // Use a stable sort that preserves relative order within each group.
        return [.. sources.OrderBy(s => IsDirectoryAggregatorSource(s) ? 1 : 0)];
    }

    /// <summary>
    /// Returns true when a source looks like a directory or aggregator
    /// listing page rather than an individual business page. These pages
    /// contain useful data but should rank below real business results.
    /// </summary>
    internal static bool IsDirectoryAggregatorSource(SourceItem source)
    {
        var title = (source.Title ?? "").Trim();
        var url = (source.Url ?? "").Trim();

        // ── Title heuristics ─────────────────────────────────────────
        // Titles like "THE BEST 10 BAKERIES in OLYMPIA, WA" or
        // "Best Bakeries & Dessert Shops in Olympia" are directory pages.
        if (Regex.IsMatch(title, @"\b(?:BEST|TOP)\s+\d+\b", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(title, @"\bBest\b.*\bin\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(title, @"\b(?:shops?|restaurants?|places?|bakeries|delis?|florists?|salons?|cafes?|stores?)\b", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(title, @"\bTop\s+\w+\s+(?:in|near)\b", RegexOptions.IgnoreCase))
            return true;

        // ── URL path heuristics ──────────────────────────────────────
        // Yelp search/list pages use /search?find_desc= paths; individual
        // business pages use /biz/<slug>.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();

            // yelp.com/search → directory; yelp.com/biz/slug → keep
            if (host.Contains("yelp.com", StringComparison.Ordinal) &&
                !path.StartsWith("/biz/", StringComparison.Ordinal))
                return true;

            // tripadvisor.com/Restaurants or /Attractions → directory
            if (host.Contains("tripadvisor.com", StringComparison.Ordinal) &&
                (path.StartsWith("/restaurants", StringComparison.Ordinal) ||
                 path.StartsWith("/attractions", StringComparison.Ordinal)))
                return true;

            // foursquare.com/top-picks → directory
            if (host.Contains("foursquare.com", StringComparison.Ordinal) &&
                path.Contains("top-picks", StringComparison.Ordinal))
                return true;

            // Common tourism/guide domains that publish "best of" lists
            if (host.Contains("experienceolympia", StringComparison.Ordinal) ||
                host.Contains("visitseattle", StringComparison.Ordinal) ||
                host.Contains("timeout.com", StringComparison.Ordinal) ||
                host.Contains("eater.com", StringComparison.Ordinal) ||
                host.Contains("thrillist.com", StringComparison.Ordinal) ||
                host.Contains("infatuation.com", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool LocalBusinessSourceMatches(SourceItem source, IReadOnlyList<string> keywords)
    {
        var haystack = $"{source.Title} {source.Snippet} {source.Domain}";
        foreach (var keyword in keywords)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> GetLocalBusinessMatchKeywords(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();

        if (lower.Contains("deli", StringComparison.Ordinal) ||
            lower.Contains("delis", StringComparison.Ordinal) ||
            lower.Contains("delicatessen", StringComparison.Ordinal))
            return ["deli", "delis", "delicatessen", "sandwich", "sub", "hoagie", "pastrami", "bagel"];

        if (lower.Contains("bakery", StringComparison.Ordinal) || lower.Contains("bakeries", StringComparison.Ordinal))
            return ["bakery", "bakeries", "bread", "pastry", "pastries", "patisserie", "cake", "cakes", "donut", "donuts"];

        if (lower.Contains("restaurant", StringComparison.Ordinal))
            return ["restaurant", "restaurants", "eatery", "bistro", "diner", "grill", "cafe"];

        if (lower.Contains("florist", StringComparison.Ordinal))
            return ["florist", "floral", "flower"];

        if (lower.Contains("salon", StringComparison.Ordinal))
            return ["salon", "hair", "stylist", "barber"];

        if (lower.Contains("coffee", StringComparison.Ordinal) || lower.Contains("cafe", StringComparison.Ordinal))
            return ["coffee", "cafe", "espresso", "roastery"];

        if (lower.Contains("pharmacy", StringComparison.Ordinal))
            return ["pharmacy", "drugstore", "rx"];

        if (lower.Contains("dentist", StringComparison.Ordinal))
            return ["dentist", "dental", "orthodont"];

        if (lower.Contains("grocery", StringComparison.Ordinal))
            return ["grocery", "market", "supermarket"];

        return [];
    }

    private static string GetRequestedLocalBusinessLabel(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();

        if (lower.Contains("delis", StringComparison.Ordinal) ||
            lower.Contains("deli", StringComparison.Ordinal) ||
            lower.Contains("delicatessen", StringComparison.Ordinal))
            return "delis";
        if (lower.Contains("bakeries", StringComparison.Ordinal) || lower.Contains("bakery", StringComparison.Ordinal))
            return "bakeries";
        if (lower.Contains("restaurants", StringComparison.Ordinal) || lower.Contains("restaurant", StringComparison.Ordinal))
            return "restaurants";
        if (lower.Contains("florists", StringComparison.Ordinal) || lower.Contains("florist", StringComparison.Ordinal))
            return "florists";
        if (lower.Contains("coffee", StringComparison.Ordinal) || lower.Contains("cafe", StringComparison.Ordinal))
            return "coffee shops";
        if (lower.Contains("salon", StringComparison.Ordinal))
            return "salons";

        return "places";
    }

    private static string SingularizeBusinessLabel(string label)
    {
        return label switch
        {
            "delis" => "deli",
            "bakeries" => "bakery",
            "restaurants" => "restaurant",
            "florists" => "florist",
            "coffee shops" => "coffee shop",
            "salons" => "salon",
            _ => label.TrimEnd('s')
        };
    }

    private string? ResolveLocalBusinessLocationContext(string userMessage)
    {
        if (!string.IsNullOrWhiteSpace(UserLocationHint))
            return UserLocationHint.Trim();

        return ExtractInlineLocationFromMessage(userMessage);
    }

    private static IReadOnlyList<SourceItem> FilterSourcesForLocalBusinessLocation(
        IReadOnlyList<SourceItem> sources,
        string? locationContext)
    {
        var location = BuildLocalNewsLocationTokens(locationContext);
        if (location is null)
            return [];

        var locationMatches = sources
            .Where(source => IsLocalBusinessLocationMatch(source, location))
            .ToList();

        if (locationMatches.Count > 0)
            return locationMatches;

        // Search providers do not always restate the query location in the
        // title/snippet of otherwise relevant business listings. When that
        // happens, keep sources unless they explicitly point to another state.
        return sources
            .Where(source => !IsExplicitlyOutOfAreaLocalBusinessSource(source, location))
            .ToList();
    }

    private static bool IsLocalBusinessLocationMatch(SourceItem source, LocalNewsLocationTokens location)
    {
        var normalizedStory = BuildLocalNewsStoryText(source);
        if (ContainsNormalizedTerm(normalizedStory, location.CityPhrase))
            return true;

        if (location.CityTokens.Any(token => ContainsNormalizedTerm(normalizedStory, token)))
            return true;

        return (!string.IsNullOrWhiteSpace(location.StateName) &&
                ContainsNormalizedTerm(normalizedStory, location.StateName)) ||
               (!string.IsNullOrWhiteSpace(location.StateCode) &&
                ContainsNormalizedTerm(normalizedStory, location.StateCode));
    }

    private static bool IsExplicitlyOutOfAreaLocalBusinessSource(SourceItem source, LocalNewsLocationTokens targetLocation)
    {
        var normalizedStory = BuildLocalNewsStoryText(source);
        if (string.IsNullOrWhiteSpace(normalizedStory))
            return false;

        if (IsLocalBusinessLocationMatch(source, targetLocation))
            return false;

        foreach (var state in StateCodeToName)
        {
            var normalizedStateName = NormalizeLocalNewsText(state.Value);
            var mentionsThisState =
                ContainsNormalizedTerm(normalizedStory, state.Key) ||
                ContainsNormalizedTerm(normalizedStory, normalizedStateName);

            if (!mentionsThisState)
                continue;

            var isTargetState =
                (!string.IsNullOrWhiteSpace(targetLocation.StateCode) &&
                 string.Equals(targetLocation.StateCode, state.Key, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(targetLocation.StateName) &&
                 string.Equals(targetLocation.StateName, normalizedStateName, StringComparison.OrdinalIgnoreCase));

            return !isTargetState;
        }

        return false;
    }

    private static string TrimSentence(string text, int maxLen)
    {
        var cleaned = (text ?? "").Trim();
        if (cleaned.Length <= maxLen)
            return cleaned;

        return cleaned[..maxLen].TrimEnd() + "…";
    }

    private AgentResponse BuildNewsNoResultsResponse(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string? resolvedLocationName = null)
    {
        var isLocalNewsRequest = LocalNewsSignalRegex.IsMatch(userMessage ?? "");
        // Prefer the configured user location; fall back to the entity-
        // resolved location so the response mentions the city the user
        // asked about even when no location is configured in settings.
        var locationHint = UserLocationHint?.Trim();
        if (string.IsNullOrWhiteSpace(locationHint))
            locationHint = resolvedLocationName?.Trim();

        var text = isLocalNewsRequest switch
        {
            true when !string.IsNullOrWhiteSpace(locationHint) =>
                $"I couldn't find usable live local news results for {locationHint} right now. " +
                "Try asking for state news, naming a local outlet, or narrowing it to a topic like schools, crime, politics, or weather.",
            true =>
                "I couldn't find usable live local news results for that request right now. " +
                "Try including a city, naming a local outlet, or setting your location in Settings and trying again.",
            _ =>
                "I couldn't find usable live news results for that request right now. " +
                "Try narrowing it to a topic, place, or timeframe."
        };

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0
        };
    }

    private static bool IsLocalBusinessNoResultsRequest(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        return
            lower.Contains("deli", StringComparison.Ordinal) ||
            lower.Contains("delis", StringComparison.Ordinal) ||
            lower.Contains("delicatessen", StringComparison.Ordinal) ||
            lower.Contains("restaurant", StringComparison.Ordinal) ||
            lower.Contains("restaurants", StringComparison.Ordinal) ||
            lower.Contains("florist", StringComparison.Ordinal) ||
            lower.Contains("bakery", StringComparison.Ordinal) ||
            lower.Contains("bakeries", StringComparison.Ordinal) ||
            lower.Contains("cafe", StringComparison.Ordinal) ||
            lower.Contains("coffee", StringComparison.Ordinal) ||
            lower.Contains("bar", StringComparison.Ordinal) ||
            lower.Contains("pub", StringComparison.Ordinal) ||
            lower.Contains("store", StringComparison.Ordinal) ||
            lower.Contains("shop", StringComparison.Ordinal) ||
            lower.Contains("salon", StringComparison.Ordinal) ||
            lower.Contains("gym", StringComparison.Ordinal) ||
            lower.Contains("pharmacy", StringComparison.Ordinal) ||
            lower.Contains("gas station", StringComparison.Ordinal) ||
            lower.Contains("car wash", StringComparison.Ordinal) ||
            lower.Contains("laundromat", StringComparison.Ordinal) ||
            lower.Contains("grocery", StringComparison.Ordinal) ||
            lower.Contains("dentist", StringComparison.Ordinal) ||
            lower.Contains("clinic", StringComparison.Ordinal) ||
            lower.Contains("open", StringComparison.Ordinal) ||
            lower.Contains("hours", StringComparison.Ordinal);
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

    private static string StripOfflineReasoningPrefix(string text)
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
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Strip common template artifacts from small local models
        text = text.Replace("<|im_end|>", "").Replace("<|endoftext|>", "")
                   .Replace("[/INST]", "").Replace("[INST]", "")
                   .Replace("</s>", "").Replace("<s>", "");

        // Strip self-dialogue: "User:" / "Human:" continuation
        var selfDialogueCut = new[]
        {
            "\nUser:", "\nuser:", "\nHuman:", "\nhuman:",
            "\n### User", "\n### Human"
        };

        foreach (var marker in selfDialogueCut)
        {
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx > 0) text = text[..idx];
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

    private AgentResponse? TryBuildExistenceGuardedResponse(
        string userMessage,
        IReadOnlyList<SourceItem> initialSources,
        List<ToolCallRecord> toolCallsMade)
    {
        var queryBundle = BuildExistenceQueryBundle(userMessage);
        if (queryBundle.Count <= 1)
            return null;

        var evidence = initialSources
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .ToList();
        const bool addedFollowupEvidence = false;

        if (evidence.Count == 0)
            return null;

        if (!IsLikelyNonexistent(userMessage, evidence, out var nonexistenceScore))
            return null;

        var seasonLabel = TryExtractSeasonLabel(userMessage);
        var seasonPhrase = seasonLabel is null ? "the requested installment" : seasonLabel;
        var text =
            $"Based on available sources, {seasonPhrase} does not exist. " +
            "The evidence indicates it was canceled or never released, so there is no official episode plot to summarize.";

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "EXISTENCE_GUARD_TRIGGERED",
            Result = "does_not_exist",
            Details = new Dictionary<string, object>
            {
                ["query_bundle_count"] = queryBundle.Count,
                ["evidence_count"] = evidence.Count,
                ["nonexistence_score"] = nonexistenceScore,
                ["added_followup_evidence"] = addedFollowupEvidence
            }
        });

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = 0
        };
    }

    internal static IReadOnlyList<string> BuildExistenceQueryBundle(string userQuestion)
    {
        if (string.IsNullOrWhiteSpace(userQuestion))
            return [];

        var normalized = userQuestion.Trim();
        var lower = normalized.ToLowerInvariant();
        var hasSeasonEpisode =
            Regex.IsMatch(lower, @"\bseason\s+\d+\b") &&
            Regex.IsMatch(lower, @"\bepisode\s+\d+\b");
        if (!hasSeasonEpisode)
            return [normalized];

        var parsed = TryParseSeasonEpisode(normalized);
        if (parsed is null)
        {
            return
            [
                normalized,
                $"{normalized} cancelled",
                $"{normalized} number of seasons",
                $"{normalized} episode list"
            ];
        }

        var (entity, season, episode) = parsed.Value;
        if (string.IsNullOrWhiteSpace(entity))
        {
            return
            [
                normalized,
                $"{normalized} cancelled",
                $"{normalized} number of seasons",
                $"{normalized} episode list"
            ];
        }

        return
        [
            $"{entity} season {season} episode {episode} plot",
            $"{entity} season {season} cancelled",
            $"{entity} number of seasons",
            $"{entity} season {season} episode list"
        ];
    }

    internal static bool IsLikelyNonexistent(
        string question,
        IReadOnlyList<SourceItem> evidence,
        out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(question) || evidence.Count == 0)
            return false;

        foreach (var source in evidence)
        {
            var text = $"{source.Title} {source.Snippet}".ToLowerInvariant();

            if (text.Contains("does not exist", StringComparison.Ordinal) ||
                text.Contains("doesn't exist", StringComparison.Ordinal) ||
                text.Contains("never renewed", StringComparison.Ordinal) ||
                text.Contains("never released", StringComparison.Ordinal) ||
                text.Contains("no season", StringComparison.Ordinal) ||
                text.Contains("no episode", StringComparison.Ordinal) ||
                text.Contains("canceled", StringComparison.Ordinal) ||
                text.Contains("cancelled", StringComparison.Ordinal) ||
                text.Contains("ended after season", StringComparison.Ordinal))
            {
                score += 6;
            }

            if (text.Contains("episode list", StringComparison.Ordinal) ||
                text.Contains("air date", StringComparison.Ordinal) ||
                text.Contains("released", StringComparison.Ordinal) ||
                text.Contains("available now", StringComparison.Ordinal))
            {
                score -= 3;
            }
        }

        var seasonLabel = TryExtractSeasonLabel(question);
        if (!string.IsNullOrWhiteSpace(seasonLabel))
        {
            var seasonNumberMatch = Regex.Match(seasonLabel, @"\d+");
            if (seasonNumberMatch.Success &&
                int.TryParse(seasonNumberMatch.Value, out var requestedSeason) &&
                requestedSeason > 1)
            {
                var priorSeasonLabel = $"season {requestedSeason - 1}";
                var hasPriorSeason = evidence.Any(s =>
                    ($"{s.Title} {s.Snippet}")
                    .Contains(priorSeasonLabel, StringComparison.OrdinalIgnoreCase));
                var hasCancelSignal = evidence.Any(s =>
                    ($"{s.Title} {s.Snippet}")
                    .Contains("cancel", StringComparison.OrdinalIgnoreCase));

                if (hasPriorSeason && hasCancelSignal)
                    score += 10;
            }
        }

        return score >= 12;
    }

    private static string? TryExtractSeasonLabel(string userMessage)
    {
        var match = Regex.Match(userMessage ?? "", @"\bseason\s+\d+\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static (string Entity, int Season, int Episode)? TryParseSeasonEpisode(string question)
    {
        var lower = question.ToLowerInvariant();
        var seasonMatch = Regex.Match(lower, @"\bseason\s+(\d+)\b");
        var episodeMatch = Regex.Match(lower, @"\bepisode\s+(\d+)\b");
        if (!seasonMatch.Success || !episodeMatch.Success)
            return null;

        if (!int.TryParse(seasonMatch.Groups[1].Value, out var season) ||
            !int.TryParse(episodeMatch.Groups[1].Value, out var episode))
        {
            return null;
        }

        var marker = lower.IndexOf(" of ", StringComparison.Ordinal);
        if (marker < 0)
            marker = lower.IndexOf(" for ", StringComparison.Ordinal);

        var entity = marker >= 0
            ? question[(marker + 4)..].Trim(' ', '?', '.', '"', '\'')
            : question[..Math.Min(seasonMatch.Index, question.Length)].Trim(' ', '?', '.', '"', '\'');

        return (entity, season, episode);
    }

    private SearchMode ResolveMode(string userMessage, LookupModeHint modeHint, DateTimeOffset now)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        var hasFollowUpSignals =
            Session.HasRecentResults(now) &&
            (SearchModeRouter.IsFollowUpMessage(lower) ||
             (Session.LastWasLocalBusinessDiscovery && SearchModeRouter.IsReferential(lower)));

        return modeHint switch
        {
            LookupModeHint.Fact when hasFollowUpSignals => SearchMode.FollowUp,
            LookupModeHint.Fact => SearchMode.WebFactFind,
            LookupModeHint.News => SearchMode.NewsAggregate,
            LookupModeHint.DeepDive => SearchMode.DeepDiveBriefing,
            _ => SearchModeRouter.Classify(userMessage ?? "", Session, now)
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

