using System.Text;
using SirThaddeus.AuditLog;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Search;

public sealed partial class SearchOrchestrator
{
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
        var entity = await _entityResolver.ResolveAsync(
            userMessage, Session, toolCallsMade, ct);

        var query = await _queryBuilder.BuildAsync(
            SearchMode.NewsAggregate, userMessage, entity, Session, history, ct);

        var toolResult = await CallWebSearchAsync(
            query.Query, query.Recency, toolCallsMade, ct,
            originalUserMessage: userMessage);
        toolResult = await TryRecoverLocalNewsResultsAsync(
            userMessage,
            query,
            toolResult,
            toolCallsMade,
            ct);

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

        var sources = ParseSourcesFromToolResult(toolResult);
        var explicitNewsLocation = ExtractExplicitNewsLocation(userMessage);
        var isLocalNews = LocalNewsSignalRegex.IsMatch(userMessage) &&
                          (!string.IsNullOrWhiteSpace(UserLocationHint) ||
                           !string.IsNullOrWhiteSpace(explicitNewsLocation));
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

        if (isLocalNews)
        {
            sources = FilterSourcesForLocalNews(sources, explicitNewsLocation);
            if (sources.Count == 0)
                return BuildNewsNoResultsResponse(userMessage, toolCallsMade, entityLocationName);
        }

        var clusters = StoryClustering.Cluster(sources);
        Session.LastClusters = clusters;

        if (clusters.Count > 0 && clusters[0].Sources.Count > 0)
            Session.PrimarySourceId = clusters[0].Sources[0].SourceId;

        Session.RecordSearchResults(
            SearchMode.NewsAggregate, query.Query, query.Recency,
            sources, DateTimeOffset.UtcNow);

        var summaryInput = "[Web search results — use these facts to answer the user's question]\n" +
                           StripSourcesJson(toolResult);

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

        var lowerMessage = (userMessage ?? "").Trim().ToLowerInvariant();
        var localBusinessLocation = ResolveLocalBusinessLocationContext(userMessage ?? "");
        if (string.IsNullOrWhiteSpace(localBusinessLocation) &&
            IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerMessage))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
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
                Actor = "search",
                Action = "SKIP_ENTITY_RESOLUTION",
                Result = "local_business_discovery"
            });
        }

        var query = await _queryBuilder.BuildAsync(
            SearchMode.WebFactFind, userMessage ?? "", entity, Session, history, ct);

        var toolResult = await CallWebSearchAsync(
            query.Query, query.Recency, toolCallsMade, ct,
            originalUserMessage: userMessage,
            maxResults: isLocalBusinessQuery ? LocalBusinessFetchMaxResults : null);

        if (!isLocalBusinessQuery)
        {
            toolResult = await TryRecoverNoResultsWebFactFindAsync(
                userMessage ?? string.Empty,
                query.Query,
                toolResult,
                toolCallsMade,
                ct);
        }

        var isNoResults = string.IsNullOrWhiteSpace(toolResult) ||
                          LooksLikeNoResultsPayload(toolResult) ||
                          WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is not null;

        if (!isNoResults)
        {
            var rawSources = ParseSourcesFromToolResult(toolResult);
            if (rawSources.Count == 0)
                isNoResults = true;
        }

        if (isNoResults && isLocalBusinessQuery)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
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
                Actor = "search",
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
            return await EnrichLocalBusinessDiscoveryAsync(
                userMessage ?? "", sources, localBusinessLocation, toolCallsMade, ct);
        }

        Session.ClearLocalBusinessCandidates();

        var strippedContent = StripSourcesJson(toolResult);
        var toolResultHasRichContent = strippedContent.Length >= MinRichContentLength;
        var isLocalBizDiscovery = IntentFeatureExtractor.HasLocalBusinessProximitySignals(lowerMessage);

        string? articleContent = null;

        if (!toolResultHasRichContent && !isLocalBizDiscovery)
        {
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
                Actor = "search",
                Action = "SKIP_ARTICLE_FETCH",
                Result = toolResultHasRichContent ? "rich_content_in_tool_result" : "local_business_discovery",
                Details = new Dictionary<string, object>
                {
                    ["stripped_content_length"] = strippedContent.Length,
                    ["is_local_biz"] = isLocalBizDiscovery
                }
            });
        }

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

    private async Task<string> TryRecoverNoResultsWebFactFindAsync(
        string userMessage,
        string primaryQuery,
        string initialToolResult,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (!LooksLikeNoResultsPayload(initialToolResult) &&
            WebToolFailureMapper.TryBuildFailureResponse(initialToolResult, toolCallsMade) is null)
        {
            return initialToolResult;
        }

        var retryCandidates = BuildNoResultsRetryCandidates(userMessage, primaryQuery);
        var lastResult = initialToolResult;

        foreach (var candidate in retryCandidates)
        {
            if (string.Equals(candidate, primaryQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "NO_RESULTS_QUERY_RETRY",
                Result = "retrying",
                Details = new Dictionary<string, object>
                {
                    ["query"] = candidate
                }
            });

            lastResult = await CallWebSearchAsync(
                candidate,
                "any",
                toolCallsMade,
                ct,
                originalUserMessage: userMessage);

            if (!LooksLikeNoResultsPayload(lastResult) &&
                WebToolFailureMapper.TryBuildFailureResponse(lastResult, toolCallsMade) is null &&
                ParseSourcesFromToolResult(lastResult).Count > 0)
            {
                _audit.Append(new AuditEvent
                {
                    Actor = "search",
                    Action = "NO_RESULTS_QUERY_RETRY",
                    Result = "recovered",
                    Details = new Dictionary<string, object>
                    {
                        ["query"] = candidate
                    }
                });
                return lastResult;
            }
        }

        return lastResult;
    }

    private static IReadOnlyList<string> BuildNoResultsRetryCandidates(string userMessage, string primaryQuery)
    {
        var candidates = new List<string>();
        var safeMessage = userMessage ?? string.Empty;

        void AddCandidate(string? q)
        {
            if (string.IsNullOrWhiteSpace(q))
                return;

            var trimmed = q.Trim();
            if (trimmed.Length > 90)
                trimmed = trimmed[..90].TrimEnd();
            if (trimmed.Length < 8)
                return;
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                candidates.Add(trimmed);
        }

        AddCandidate(primaryQuery);

        var lower = safeMessage.ToLowerInvariant();

        // Flight-specific retry candidates.
        if (lower.Contains("flight") && lower.Contains(" from ") && lower.Contains(" to "))
        {
            var fromIdx = lower.IndexOf(" from ", StringComparison.Ordinal);
            var toIdx = lower.IndexOf(" to ", StringComparison.Ordinal);
            if (fromIdx >= 0 && toIdx > fromIdx)
            {
            var fromPart = safeMessage.Substring(fromIdx + 6, toIdx - (fromIdx + 6)).Trim();
            var tail = safeMessage[(toIdx + 4)..];
                var endIdx = tail.IndexOfAny([',', '.', '?']);
                var toPart = (endIdx >= 0 ? tail[..endIdx] : tail).Trim();
                AddCandidate($"cheap flights {fromPart} to {toPart}");
                AddCandidate($"{fromPart} to {toPart} flights");
            }
        }

        // Product availability retry candidates.
        if (lower.Contains("in stock") || lower.Contains("purchase") || lower.Contains("buy"))
        {
            var productRelaxed = safeMessage
                .Replace("give me a direct purchase link", "", StringComparison.OrdinalIgnoreCase)
                .Replace("and give me", "", StringComparison.OrdinalIgnoreCase)
                .Replace("find me", "", StringComparison.OrdinalIgnoreCase)
                .Replace("can you", "", StringComparison.OrdinalIgnoreCase)
                .Replace("online", "", StringComparison.OrdinalIgnoreCase)
                .Replace("?", " ")
                .Replace("  ", " ")
                .Trim();
            AddCandidate(productRelaxed);
        }

        var relaxed = safeMessage
            .Replace("verify it's still available", "", StringComparison.OrdinalIgnoreCase)
            .Replace("verify it is still available", "", StringComparison.OrdinalIgnoreCase)
            .Replace("still available", "", StringComparison.OrdinalIgnoreCase)
            .Replace("and verify", "", StringComparison.OrdinalIgnoreCase)
            .Replace("can you find", "", StringComparison.OrdinalIgnoreCase)
            .Replace("can you", "", StringComparison.OrdinalIgnoreCase)
            .Replace("find me", "", StringComparison.OrdinalIgnoreCase)
            .Replace("give me", "", StringComparison.OrdinalIgnoreCase)
            .Replace("please", "", StringComparison.OrdinalIgnoreCase)
            .Replace("next month", "", StringComparison.OrdinalIgnoreCase)
            .Replace("?", " ")
            .Replace("  ", " ")
            .Trim();

        AddCandidate(relaxed);
        return candidates.Take(3).ToList();
    }
}
