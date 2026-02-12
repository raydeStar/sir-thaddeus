using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using SirThaddeus.AuditLog;
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

public sealed class SearchOrchestrator
{
    private readonly ILlmClient       _llm;
    private readonly IMcpToolClient   _mcp;
    private readonly IAuditLogger     _audit;
    private readonly string           _systemPrompt;
    private readonly EntityResolver   _entityResolver;
    private readonly QueryBuilder     _queryBuilder;

    /// <summary>Formal search state — survives history trimming.</summary>
    public SearchSession Session { get; } = new();

    // ── Tool name conventions (try both casings) ─────────────────────
    private const string WebSearchToolName    = "web_search";
    private const string WebSearchToolNameAlt = "WebSearch";
    private const string BrowseToolName       = "browser_navigate";
    private const string BrowseToolNameAlt    = "BrowserNavigate";

    // ── Bounds ───────────────────────────────────────────────────────
    private const int DefaultMaxResults    = 5;
    private const int MaxFollowUpUrls      = 2;
    private const int MaxArticleChars      = 3000;
    private const int MaxTokensWebSummary  = 768;

    private static readonly Regex PercentDirectionRegex = new(
        @"\b(?<dir>up|higher|gained|gain|rose|rallied|advanced|down|lower|fell|dropped|declined|slipped)\b[^%\n]{0,40}?(?<pct>[+-]?\d+(?:\.\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DirectionAfterPercentRegex = new(
        @"(?<pct>[+-]?\d+(?:\.\d+)?)\s*%[^.\n]{0,30}\b(?<dir>up|higher|gained|gain|rose|rallied|advanced|down|lower|fell|dropped|declined|slipped)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SignedPercentRegex = new(
        @"(?<pct>[+-]\d+(?:\.\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LevelRegex = new(
        @"\b(?:at|to|around|near|closed at|closing at|trading at)\s+\$?(?<lvl>(?:\d{1,3}(?:,\d{3})+|\d{4,6})(?:\.\d+)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingMarketLineWithLevelRegex = new(
        @"^(?<prefix>(?:the\s+)?(?:dow jones|nasdaq|s&p 500|stock market)\s+is\s+(?:up|down)\s+\d+(?:\.\d+)?%\s+today)\s*,\s*at\s+\$?[0-9][0-9,]*(?:\.\d+)?\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Source metadata delimiter (matches WebSearchTools output) ─────
    private const string SourcesJsonDelimiter = "<!-- SOURCES_JSON -->";

    // ── LLM Instructions ─────────────────────────────────────────────
    private const string NewsSummaryInstruction =
        "\n\nSearch results are in the next message. " +
        "Synthesize across all sources. Cross-reference where " +
        "they agree or differ. Be thorough. No URLs. " +
        "ONLY use facts from the provided sources. " +
        "Do NOT invent or guess details not in the results.";

    private const string FactFindSummaryInstruction =
        "\n\nSearch results and article content are in the next message. " +
        "Synthesize into a clear, factual answer. Lead with the bottom line. " +
        "Include key facts. No URLs. " +
        "ONLY use facts from the provided sources.";

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
        CancellationToken ct)
    {
        var now  = DateTimeOffset.UtcNow;
        var mode = SearchModeRouter.Classify(userMessage, Session, now);

        _audit.Append(new AuditEvent
        {
            Actor  = "agent",
            Action = "SEARCH_MODE_CLASSIFIED",
            Result = mode.ToString(),
            Details = new Dictionary<string, object>
            {
                ["user_message"]     = Truncate(userMessage, 80),
                ["has_prior_results"] = Session.HasRecentResults(now)
            }
        });

        try
        {
            var response = mode switch
            {
                SearchMode.FollowUp      => await ExecuteFollowUpAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                SearchMode.NewsAggregate  => await ExecuteNewsAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                SearchMode.WebFactFind    => await ExecuteFactFindAsync(userMessage, memoryPackText, history, toolCallsMade, ct),
                _                         => await ExecuteFactFindAsync(userMessage, memoryPackText, history, toolCallsMade, ct)
            };

            return AddMarketLeadLineIfHelpful(userMessage, response, toolCallsMade);
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
            query.Query, query.Recency, toolCallsMade, ct);

        if (string.IsNullOrWhiteSpace(toolResult))
            return AgentResponse.FromError("Search returned no results.");

        // ── 4. Parse results into SourceItems ────────────────────────
        var sources = ParseSourcesFromToolResult(toolResult);

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
        var summaryInput = "[Search results — reference only, do not display to user]\n" +
                           StripSourcesJson(toolResult);

        return await SummarizeAndRespond(
            summaryInput, memoryPackText + NewsSummaryInstruction,
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
        // ── 1. Entity resolution (always attempt) ────────────────────
        var entity = await _entityResolver.ResolveAsync(
            userMessage, Session, toolCallsMade, ct);

        // ── 2. Query construction (factfind-mode) ────────────────────
        var query = await _queryBuilder.BuildAsync(
            SearchMode.WebFactFind, userMessage, entity, Session, history, ct);

        // ── 3. web_search via MCP ────────────────────────────────────
        var toolResult = await CallWebSearchAsync(
            query.Query, query.Recency, toolCallsMade, ct);

        if (string.IsNullOrWhiteSpace(toolResult))
            return AgentResponse.FromError("Search returned no results.");

        // ── 4. Parse and record results ──────────────────────────────
        var sources = ParseSourcesFromToolResult(toolResult);
        Session.RecordSearchResults(
            SearchMode.WebFactFind, query.Query, query.Recency,
            sources, DateTimeOffset.UtcNow);

        // ── 5. Fetch top 1-2 articles for deep synthesis ─────────────
        var articlesToFetch = sources.Take(2).ToList();
        var articleContent  = await FetchArticleContentAsync(
            articlesToFetch, toolCallsMade, ct);

        // ── 6. Summarize ─────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine("[Search results — reference only, do not display to user]");
        sb.AppendLine(StripSourcesJson(toolResult));

        if (!string.IsNullOrWhiteSpace(articleContent))
        {
            sb.AppendLine();
            sb.AppendLine("[Full article content — reference only, do not display to user]");
            sb.AppendLine(articleContent);
        }

        return await SummarizeAndRespond(
            sb.ToString(), memoryPackText + FactFindSummaryInstruction,
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

        var summaryInput = "[Primary article content — reference only, do not display to user]\n" +
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
            sb.AppendLine("[Primary article content — reference only, do not display to user]");
            sb.AppendLine(primaryContent);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(toolResult))
        {
            sb.AppendLine("[Related coverage search results — reference only, do not display to user]");
            sb.AppendLine(StripSourcesJson(toolResult));
        }

        var instruction = !string.IsNullOrWhiteSpace(primaryContent)
            ? MoreSourcesInstruction
            : NewsSummaryInstruction;

        return await SummarizeAndRespond(
            sb.ToString(), memoryPackText + instruction,
            history, toolCallsMade, ct);
    }

    // ─────────────────────────────────────────────────────────────────
    // Shared Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls web_search via MCP with fallback to PascalCase tool name.
    /// </summary>
    private async Task<string> CallWebSearchAsync(
        string query, string recency,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var args = JsonSerializer.Serialize(new
        {
            query,
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

        var fetchTasks = sources.Take(MaxFollowUpUrls).Select(async source =>
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

        var sb = new StringBuilder();
        foreach (var (title, content) in results)
        {
            if (string.IsNullOrWhiteSpace(content))
                continue;
            if (IsLowSignalContent(content))
                continue;

            sb.AppendLine($"=== {title} ===");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        var combined = sb.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(combined) ? null : combined;
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
        var messages = InjectInstruction(history, instruction);
        messages.Add(ChatMessage.User(summaryInput));

        LlmResponse response;
        try
        {
            response = await _llm.ChatAsync(messages, tools: null, MaxTokensWebSummary, ct);
        }
        catch (HttpRequestException)
        {
            // LM Studio regex failure — try minimal context
            var minimal = new List<ChatMessage>
            {
                ChatMessage.System(_systemPrompt + " " + instruction),
                ChatMessage.User(summaryInput)
            };

            try
            {
                response = await _llm.ChatAsync(minimal, tools: null, MaxTokensWebSummary, ct);
            }
            catch
            {
                return new AgentResponse
                {
                    Text = BuildExtractiveFallback(summaryInput),
                    Success       = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = 1
                };
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
            LlmRoundTrips = 1
        };
    }

    private static AgentResponse AddMarketLeadLineIfHelpful(
        string userMessage,
        AgentResponse response,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
            return response;

        if (!LooksLikeMarketQuoteRequest(userMessage))
            return response;

        var hasExistingLead = TryGetLeadingMarketLine(response.Text, out var existingLeadLine, out var textWithoutLeadingLine);
        if (TryBuildMarketLeadLine(userMessage, toolCallsMade, out var leadLine))
        {
            if (!hasExistingLead)
            {
                return response with
                {
                    Text = $"{leadLine}\n\n{response.Text.TrimStart()}"
                };
            }

            var merged = string.IsNullOrWhiteSpace(textWithoutLeadingLine)
                ? leadLine
                : $"{leadLine}\n\n{textWithoutLeadingLine}";
            return response with { Text = merged };
        }

        // If we cannot verify a market level from tool evidence, avoid
        // publishing a potentially hallucinated level in the lead line.
        if (hasExistingLead &&
            TryRemoveNumericLevelFromLeadLine(existingLeadLine, out var sanitizedLead))
        {
            var merged = string.IsNullOrWhiteSpace(textWithoutLeadingLine)
                ? sanitizedLead
                : $"{sanitizedLead}\n\n{textWithoutLeadingLine}";
            return response with { Text = merged };
        }

        return response;
    }

    private static bool LooksLikeMarketQuoteRequest(string message)
    {
        var lower = (message ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        return lower.Contains("dow jones", StringComparison.Ordinal) ||
               lower.Contains("djia", StringComparison.Ordinal) ||
               lower.Contains("nasdaq", StringComparison.Ordinal) ||
               lower.Contains("s&p", StringComparison.Ordinal) ||
               lower.Contains("s and p", StringComparison.Ordinal) ||
               lower.Contains("sp500", StringComparison.Ordinal) ||
               lower.Contains("stock market", StringComparison.Ordinal) ||
               lower.Contains("stocks", StringComparison.Ordinal) ||
               lower.Contains("market index", StringComparison.Ordinal);
    }

    private static bool StartsWithMarketLeadLine(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("The Dow Jones is ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Dow Jones is ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("The Nasdaq is ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Nasdaq is ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("The S&P 500 is ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("S&P 500 is ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("The stock market is ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildMarketLeadLine(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        out string leadLine)
    {
        leadLine = "";

        var marketLabel = ResolveMarketLabel(userMessage);
        var aliases = GetMarketAliases(marketLabel);
        var corpus = BuildMarketParsingCorpus(toolCallsMade);
        if (string.IsNullOrWhiteSpace(corpus))
            return false;

        if (!TryExtractDirectionAndPercent(corpus, aliases, out var direction, out var percent))
            return false;

        if (TryExtractMarketLevel(corpus, aliases, marketLabel, out var level))
        {
            leadLine = $"The {marketLabel} is {direction} {percent} today, at {level}.";
            return true;
        }

        leadLine = $"The {marketLabel} is {direction} {percent} today.";
        return true;
    }

    private static string ResolveMarketLabel(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();
        if (lower.Contains("dow jones", StringComparison.Ordinal) || lower.Contains("djia", StringComparison.Ordinal))
            return "Dow Jones";
        if (lower.Contains("nasdaq", StringComparison.Ordinal))
            return "Nasdaq";
        if (lower.Contains("s&p", StringComparison.Ordinal) ||
            lower.Contains("s and p", StringComparison.Ordinal) ||
            lower.Contains("sp500", StringComparison.Ordinal))
            return "S&P 500";
        return "stock market";
    }

    private static string[] GetMarketAliases(string marketLabel) =>
        marketLabel switch
        {
            "Dow Jones" => ["dow jones", "djia", "dow"],
            "Nasdaq" => ["nasdaq", "nasdaq composite"],
            "S&P 500" => ["s&p 500", "s&p", "s and p", "sp500"],
            _ => ["stock market", "stocks", "market"]
        };

    private static string BuildMarketParsingCorpus(
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var sb = new StringBuilder();

        foreach (var call in toolCallsMade)
        {
            if (!call.Success || string.IsNullOrWhiteSpace(call.Result))
                continue;

            if (call.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine(StripSourcesJson(call.Result));
                continue;
            }

            if (call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine(call.Result);
            }
        }

        return sb.ToString();
    }

    private static bool TryExtractDirectionAndPercent(
        string corpus,
        string[] aliases,
        out string direction,
        out string percent)
    {
        direction = "";
        percent = "";

        if (TryExtractAliasBoundDirectionAndPercent(corpus, aliases, out direction, out percent))
            return true;

        var window = ExtractMarketWindow(corpus, aliases);
        if (TryExtractDirectionAndPercentFromText(window, out direction, out percent))
            return true;

        return TryExtractDirectionAndPercentFromText(corpus, out direction, out percent);
    }

    private static bool TryExtractDirectionAndPercentFromText(
        string text,
        out string direction,
        out string percent)
    {
        direction = "";
        percent = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var directional = PercentDirectionRegex.Match(text);
        if (directional.Success)
        {
            direction = NormalizeDirection(directional.Groups["dir"].Value);
            percent = NormalizePercentValue(directional.Groups["pct"].Value);
            return !string.IsNullOrWhiteSpace(direction) && !string.IsNullOrWhiteSpace(percent);
        }

        var reverseDirectional = DirectionAfterPercentRegex.Match(text);
        if (reverseDirectional.Success)
        {
            direction = NormalizeDirection(reverseDirectional.Groups["dir"].Value);
            percent = NormalizePercentValue(reverseDirectional.Groups["pct"].Value);
            return !string.IsNullOrWhiteSpace(direction) && !string.IsNullOrWhiteSpace(percent);
        }

        var signed = SignedPercentRegex.Match(text);
        if (!signed.Success)
            return false;

        var raw = signed.Groups["pct"].Value.Trim();
        if (raw.Length == 0)
            return false;

        direction = raw.StartsWith("-", StringComparison.Ordinal) ? "down" : "up";
        percent = NormalizePercentValue(raw.TrimStart('+', '-'));
        return !string.IsNullOrWhiteSpace(percent);
    }

    private static bool TryExtractMarketLevel(
        string corpus,
        string[] aliases,
        string marketLabel,
        out string level)
    {
        level = "";

        if (TryExtractAliasBoundLevel(corpus, aliases, marketLabel, out level))
            return true;

        var window = ExtractMarketWindow(corpus, aliases);
        if (TryExtractLevelFromText(window, marketLabel, out level))
            return true;

        return TryExtractLevelFromText(corpus, marketLabel, out level);
    }

    private static bool TryExtractLevelFromText(string text, string marketLabel, out string level)
    {
        level = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (Match match in LevelRegex.Matches(text))
        {
            if (!match.Success)
                continue;

            var raw = match.Groups["lvl"].Value.Trim();
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (!TryParseMarketLevel(raw, out var numeric))
                continue;

            if (!IsPlausibleMarketLevel(numeric, marketLabel))
                continue;

            level = FormatMarketLevel(raw, numeric);
            return true;
        }

        return false;
    }

    private static string ExtractMarketWindow(string corpus, string[] aliases)
    {
        if (string.IsNullOrWhiteSpace(corpus) || aliases.Length == 0)
            return corpus;

        var lower = corpus.ToLowerInvariant();
        foreach (var alias in aliases)
        {
            var idx = lower.IndexOf(alias, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var start = Math.Max(0, idx - 80);
            var length = Math.Min(corpus.Length - start, 260);
            return corpus.Substring(start, length);
        }

        return corpus;
    }

    private static string NormalizeDirection(string raw)
    {
        var lower = (raw ?? "").Trim().ToLowerInvariant();
        return lower switch
        {
            "up" or "higher" or "gained" or "gain" or "rose" or "rallied" or "advanced" => "up",
            "down" or "lower" or "fell" or "dropped" or "declined" or "slipped" => "down",
            _ => ""
        };
    }

    private static string NormalizePercentValue(string raw)
    {
        var cleaned = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return "";

        return cleaned.EndsWith('%') ? cleaned : $"{cleaned}%";
    }

    private static bool TryExtractAliasBoundDirectionAndPercent(
        string corpus,
        string[] aliases,
        out string direction,
        out string percent)
    {
        direction = "";
        percent = "";

        if (string.IsNullOrWhiteSpace(corpus) || aliases.Length == 0)
            return false;

        foreach (var alias in aliases
                     .Where(a => !string.IsNullOrWhiteSpace(a))
                     .OrderByDescending(a => a.Length))
        {
            var escapedAlias = Regex.Escape(alias);
            var aliasDirectional = new Regex(
                $@"\b{escapedAlias}\b[^%\n]{{0,120}}?\b(?<dir>up|higher|gained|gain|rose|rallied|advanced|down|lower|fell|dropped|declined|slipped)\b[^%\n]{{0,40}}?(?<pct>[+-]?\d+(?:\.\d+)?)\s*%",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var match = aliasDirectional.Match(corpus);
            if (match.Success)
            {
                direction = NormalizeDirection(match.Groups["dir"].Value);
                percent = NormalizePercentValue(match.Groups["pct"].Value);
                if (!string.IsNullOrWhiteSpace(direction) && !string.IsNullOrWhiteSpace(percent))
                    return true;
            }

            var aliasSignedPercent = new Regex(
                $@"\b{escapedAlias}\b[^%\n]{{0,120}}?(?<pct>[+-]\d+(?:\.\d+)?)\s*%",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var signed = aliasSignedPercent.Match(corpus);
            if (signed.Success)
            {
                var raw = signed.Groups["pct"].Value.Trim();
                direction = raw.StartsWith("-", StringComparison.Ordinal) ? "down" : "up";
                percent = NormalizePercentValue(raw.TrimStart('+', '-'));
                if (!string.IsNullOrWhiteSpace(percent))
                    return true;
            }
        }

        return false;
    }

    private static bool TryExtractAliasBoundLevel(
        string corpus,
        string[] aliases,
        string marketLabel,
        out string level)
    {
        level = "";
        if (string.IsNullOrWhiteSpace(corpus) || aliases.Length == 0)
            return false;

        foreach (var alias in aliases
                     .Where(a => !string.IsNullOrWhiteSpace(a))
                     .OrderByDescending(a => a.Length))
        {
            var escapedAlias = Regex.Escape(alias);
            var levelPattern = @"(?:\d{1,3}(?:,\d{3})+|\d{4,6})(?:\.\d+)?";

            var aliasBeforeLevel = new Regex(
                $@"\b{escapedAlias}\b[^.\n]{{0,140}}?\b(?:at|to|around|near|closed at|closing at|trading at)\s+\$?(?<lvl>{levelPattern})\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var match = aliasBeforeLevel.Match(corpus);
            if (TryResolveLevelMatch(match, marketLabel, out level))
                return true;

            var aliasAfterLevel = new Regex(
                $@"\b(?:at|to|around|near|closed at|closing at|trading at)\s+\$?(?<lvl>{levelPattern})\b[^.\n]{{0,70}}?\b{escapedAlias}\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            match = aliasAfterLevel.Match(corpus);
            if (TryResolveLevelMatch(match, marketLabel, out level))
                return true;
        }

        return false;
    }

    private static bool TryResolveLevelMatch(Match match, string marketLabel, out string level)
    {
        level = "";
        if (!match.Success)
            return false;

        var raw = match.Groups["lvl"].Value.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!TryParseMarketLevel(raw, out var numeric))
            return false;

        if (!IsPlausibleMarketLevel(numeric, marketLabel))
            return false;

        level = FormatMarketLevel(raw, numeric);
        return true;
    }

    private static bool TryGetLeadingMarketLine(
        string text,
        out string leadLine,
        out string remainder)
    {
        leadLine = "";
        remainder = text;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var firstContentIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                firstContentIndex = i;
                break;
            }
        }

        if (firstContentIndex < 0)
            return false;

        var candidate = lines[firstContentIndex].Trim();
        if (!StartsWithMarketLeadLine(candidate))
            return false;

        leadLine = candidate;
        lines.RemoveAt(firstContentIndex);
        while (firstContentIndex < lines.Count && string.IsNullOrWhiteSpace(lines[firstContentIndex]))
            lines.RemoveAt(firstContentIndex);

        remainder = string.Join("\n", lines).Trim();
        return true;
    }

    private static bool TryRemoveNumericLevelFromLeadLine(
        string line,
        out string sanitized)
    {
        sanitized = line;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var match = LeadingMarketLineWithLevelRegex.Match(line.Trim());
        if (!match.Success)
            return false;

        var prefix = match.Groups["prefix"].Value.Trim();
        if (string.IsNullOrWhiteSpace(prefix))
            return false;

        sanitized = $"{prefix}.";
        return true;
    }

    private static bool TryParseMarketLevel(string raw, out double value)
    {
        var cleaned = (raw ?? "").Trim().Replace(",", "");
        return double.TryParse(
            cleaned,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool IsPlausibleMarketLevel(double value, string marketLabel)
    {
        if (value <= 0)
            return false;

        var (min, max) = GetMarketLevelBounds(marketLabel);
        return value >= min && value <= max;
    }

    private static (double Min, double Max) GetMarketLevelBounds(string marketLabel) =>
        marketLabel switch
        {
            "Dow Jones" => (20_000d, 60_000d),
            "Nasdaq" => (5_000d, 25_000d),
            "S&P 500" => (2_000d, 8_000d),
            _ => (1_000d, 60_000d)
        };

    private static string FormatMarketLevel(string rawValue, double numericValue)
    {
        if (rawValue.Contains(',', StringComparison.Ordinal))
            return rawValue;

        if (rawValue.Contains('.', StringComparison.Ordinal))
            return rawValue;

        return numericValue.ToString("N0", CultureInfo.InvariantCulture);
    }

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

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                sources.Add(new SourceItem
                {
                    SourceId = SourceItem.ComputeSourceId(url!),
                    Url      = url!,
                    Title    = title ?? "",
                    Domain   = domain ?? ""
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
        return idx >= 0 ? toolResult[..idx].TrimEnd() : toolResult.TrimEnd();
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
}
