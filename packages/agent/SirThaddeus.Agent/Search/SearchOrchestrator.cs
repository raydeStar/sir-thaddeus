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
    private const int MaxFollowUpUrls      = 2;
    private const int MaxArticleChars      = 3000;
    private const int MaxTokensWebSummary  = 1024;
    private const int MaxTokensWebSummaryRetry = 2048;
    private const int MinRichContentLength = 1500;
    private static readonly TimeSpan FinanceQuoteFreshnessMaxAge = TimeSpan.FromHours(6);

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

        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return await BuildOfflineReasoningResponseAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                "Web search returned no results.",
                ct);
        }
        if (LooksLikeNoResultsPayload(toolResult))
        {
            return await BuildNoResultsFallbackAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                ct);
        }
        if (WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is { } newsFailure)
        {
            return await BuildOfflineReasoningResponseAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                newsFailure.Text,
                ct);
        }

        // ── 4. Parse results into SourceItems ────────────────────────
        var sources = ParseSourcesFromToolResult(toolResult);
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
            return await BuildNoResultsFallbackAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                ct);
        }

        // ── 5. Story clustering ──────────────────────────────────────
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

        var isLocalNews = !string.IsNullOrWhiteSpace(UserLocationHint) &&
                          LocalNewsSignalRegex.IsMatch(userMessage);

        var instruction = isMarketQuoteRequest
            ? memoryPackText + FinanceQuoteSummaryInstruction
            : isLocalNews
                ? memoryPackText + LocalNewsSummaryInstruction
                : memoryPackText + NewsSummaryInstruction;

        return await SummarizeAndRespond(
            summaryInput, instruction,
            history, toolCallsMade, ct);
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
        if (string.IsNullOrWhiteSpace(UserLocationHint) &&
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
                       "You can set your location in **Settings → Profile**, " +
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
                userMessage, Session, toolCallsMade, ct);
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
            SearchMode.WebFactFind, userMessage, entity, Session, history, ct);

        // ── 3. web_search via MCP ────────────────────────────────────
        var toolResult = await CallWebSearchAsync(
            query.Query, query.Recency, toolCallsMade, ct,
            originalUserMessage: userMessage);

        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return await BuildOfflineReasoningResponseAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                "Web search returned no results.",
                ct);
        }
        if (LooksLikeNoResultsPayload(toolResult))
        {
            return await BuildNoResultsFallbackAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                ct);
        }
        if (WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is { } factFailure)
        {
            return await BuildOfflineReasoningResponseAsync(
                userMessage,
                memoryPackText,
                history,
                toolCallsMade,
                factFailure.Text,
                ct);
        }

        // ── 4. Parse and record results ──────────────────────────────
        var sources = ParseSourcesFromToolResult(toolResult);
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
            return await BuildNoResultsFallbackAsync(
                userMessage,
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

        // ── 5. Fetch top articles for deep synthesis ──────────────────
        // The MCP WebSearch tool already auto-reads pages via ContentExtractor
        // and embeds up to 1000-char excerpts in the output. For most queries
        // this is sufficient. Only re-fetch via browser_navigate when the
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

        return await SummarizeAndRespond(
            sb.ToString(), instruction,
            history, toolCallsMade, ct);
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
            var subject = ExtractFollowUpSubject(userMessage);
            if (!string.IsNullOrWhiteSpace(subject))
            {
                _audit.Append(new AuditEvent
                {
                    Actor  = "agent",
                    Action = "FOLLOWUP_PLACE_BRIEFING_REDIRECT",
                    Result = subject
                });
                return await ExecuteDeepDiveBriefingAsync(subject, toolCallsMade, ct);
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
    /// Strips common follow-up prefixes to isolate the subject the user
    /// is asking about. "Tell me more about Left Bank Pastry" → "Left Bank Pastry".
    /// </summary>
    private static string ExtractFollowUpSubject(string userMessage)
    {
        var trimmed = (userMessage ?? "").Trim();

        ReadOnlySpan<string> prefixes =
        [
            "tell me more about ",
            "tell me about ",
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
            "can you tell me about ",
            "can you tell me more about ",
            "show me more about ",
            "show me about ",
            "show me "
        ];

        foreach (var prefix in prefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..].TrimEnd('?', '.', '!');
        }

        return trimmed;
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
            history, toolCallsMade, ct);
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
        if (!string.IsNullOrWhiteSpace(toolResult))
        {
            var newSources = ParseSourcesFromToolResult(toolResult);
            Session.AppendResults(newSources, DateTimeOffset.UtcNow);
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
            history, toolCallsMade, ct);
    }

    /// <summary>
    /// Produces a structured deep-dive briefing payload for place/product lookups.
    /// </summary>
    private async Task<AgentResponse> ExecuteDeepDiveBriefingAsync(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var timezone = TimeZoneInfo.Local.Id;
        var locale = CultureInfo.CurrentCulture.Name;

        var result = await _deepDiveCoordinator.BuildPlaceBriefingAsync(
            query: userMessage,
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
            query: userMessage,
            recency: "any",
            results: sourceItems,
            now: now);

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
        string? originalUserMessage = null)
    {
        var effectiveQuery = InjectLocationIfProximityQuery(query);
        effectiveQuery = InjectLocationForLocalBusinessQuery(effectiveQuery, originalUserMessage);
        effectiveQuery = InjectLocationIntoLocalNewsQuery(effectiveQuery, originalUserMessage);
        effectiveQuery = InjectLocationIntoDistanceQuery(effectiveQuery);
        effectiveQuery = InjectUnitPreferenceIntoDistanceQuery(effectiveQuery);

        var args = JsonSerializer.Serialize(new
        {
            query = effectiveQuery,
            maxResults = DefaultMaxResults,
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

        return toolResult;
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
                    Text = BuildExtractiveFallback(summaryInput),
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

                if (!string.IsNullOrWhiteSpace(expanded.Content))
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
            text = BuildExtractiveFallback(summaryInput);

        // Strip template garbage
        text = StripTemplateTokens(text);
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
        if (string.IsNullOrWhiteSpace(toolResult))
            return sources;

        var delimIdx = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        if (delimIdx < 0)
            return sources;

        var jsonPart = toolResult[(delimIdx + SourcesJsonDelimiter.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(jsonPart))
            return sources;

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return sources;

            foreach (var item in doc.RootElement.EnumerateArray())
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

    private static string BuildExtractiveFallback(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "I found some results but couldn't generate a summary.";

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 10 &&
                        !l.StartsWith("[", StringComparison.Ordinal) &&
                        !l.StartsWith("===", StringComparison.Ordinal))
            .Take(5)
            .ToList();

        return lines.Count > 0
            ? string.Join("\n\n", lines)
            : "I found some results but couldn't generate a clean summary.";
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

        var text = isLocalBusinessRequest
            ? "I could not retrieve live local business results for that request right now. " +
              "Try naming one specific place (for example, \"Is Walmart in Rexburg open right now?\") " +
              "and I can check its current hours."
            : "I could not retrieve usable web results for that request right now. " +
              "Try a more specific query with a clear name, place, or timeframe.";

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

    private SearchMode ResolveMode(string userMessage, LookupModeHint modeHint, DateTimeOffset now)
    {
        return modeHint switch
        {
            LookupModeHint.Fact => SearchMode.WebFactFind,
            LookupModeHint.News => SearchMode.NewsAggregate,
            LookupModeHint.DeepDive => SearchMode.DeepDiveBriefing,
            _ => SearchModeRouter.Classify(userMessage, Session, now)
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
