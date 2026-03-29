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
        var explicitNewsLocation = ExtractExplicitNewsLocation(userMessage);
        var isExplicitLocalNewsRequest =
            LocalNewsSignalRegex.IsMatch(userMessage) &&
            !string.IsNullOrWhiteSpace(explicitNewsLocation);

        EntityResolver.ResolvedEntity? entity = null;
        QueryBuilder.SearchQuery query;

        if (isExplicitLocalNewsRequest)
        {
            query = new QueryBuilder.SearchQuery
            {
                Query = $"{explicitNewsLocation} news",
                Recency = "day",
                UsedFallback = true
            };

            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "LOCAL_NEWS_DIRECT_QUERY",
                Result = query.Query
            });
        }
        else
        {
            entity = await _entityResolver.ResolveAsync(
                userMessage, Session, toolCallsMade, ct);

            query = await _queryBuilder.BuildAsync(
                SearchMode.NewsAggregate, userMessage, entity, Session, history, ct);
        }

        var toolResult = await CallWebSearchAsync(
            query.Query, query.Recency, toolCallsMade, ct,
            originalUserMessage: userMessage,
            categories: "news");
        toolResult = await TryRecoverLocalNewsResultsAsync(
            userMessage,
            query,
            toolResult,
            toolCallsMade,
            ct);

        // When the news category returns too few sources, retry with general
        // category.  SearXNG may lack dedicated news engines, so "general"
        // often yields more relevant hits for the same query+recency.
        toolResult = await TryRecoverSparseNewsResultsAsync(
            userMessage, query, toolResult, toolCallsMade, ct);

        var entityLocationName = entity is { Type: "Place" or "place" }
            ? entity.CanonicalName
            : explicitNewsLocation;

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
            var stripped = StripSourcesJson(toolResult);
            if (string.IsNullOrWhiteSpace(stripped))
                return BuildNewsNoResultsResponse(userMessage, toolCallsMade, entityLocationName);

            Session.RecordSearchResults(
                SearchMode.NewsAggregate,
                query.Query,
                query.Recency,
                [],
                DateTimeOffset.UtcNow);

            var rawNewsSummaryInput = "[Web search results — use these facts to answer the user's question]\n" + stripped;
            var rawSummaryResponse = await SummarizeAndRespond(
                rawNewsSummaryInput,
                CombineMemoryAndInstruction(memoryPackText, NewsSummaryInstruction),
                history,
                toolCallsMade,
                SummaryFallbackKind.News,
                null,
                ct);

            if (isLocalNews)
            {
                var locationLabel = explicitNewsLocation;
                if (string.IsNullOrWhiteSpace(locationLabel))
                    locationLabel = UserLocationHint;

                rawSummaryResponse = EnsureLocalNewsLocationMention(rawSummaryResponse, locationLabel);
            }

            return rawSummaryResponse;
        }

        if (isLocalNews)
        {
            sources = FilterSourcesForLocalNews(sources, explicitNewsLocation);
            if (sources.Count == 0)
                return BuildNewsNoResultsResponse(userMessage, toolCallsMade, entityLocationName);
        }

        sources = FilterLowValueNewsSources(sources);

        var clusters = StoryClustering.Cluster(sources);
        Session.LastClusters = clusters;

        if (clusters.Count > 0 && clusters[0].Sources.Count > 0)
            Session.PrimarySourceId = clusters[0].Sources[0].SourceId;

        Session.RecordSearchResults(
            SearchMode.NewsAggregate, query.Query, query.Recency,
            sources, DateTimeOffset.UtcNow);

        var summaryInput = BuildSummaryInputFromSources(
            "[Web search results - use these facts to answer the user's question]",
            sources);

        // For general (non-local, non-market) news, fetch the top articles
        // via browser_navigate so the LLM has real article content instead
        // of raw search snippets (which are often junk portal landing pages).
        if (!isLocalNews && !isMarketQuoteRequest)
        {
            var resolved = ResolveSourceUrls(sources);
            var navigable = resolved
                .Where(s => !IsJunkUrl(s.Url))
                .Take(MaxFollowUpUrls)
                .ToList();

            var articleContent = await FetchArticleContentAsync(
                navigable, toolCallsMade, ct);

            if (!string.IsNullOrWhiteSpace(articleContent))
            {
                summaryInput += "\n\n[Full article content — use these details to give a thorough answer]\n" + articleContent;
            }
        }

        var instruction = isMarketQuoteRequest
            ? CombineMemoryAndInstruction(memoryPackText, FinanceQuoteSummaryInstruction)
            : isLocalNews
                ? CombineMemoryAndInstruction(memoryPackText, LocalNewsSummaryInstruction)
                : CombineMemoryAndInstruction(memoryPackText, NewsSummaryInstruction);

        var response = await SummarizeAndRespond(
            summaryInput, instruction,
            history, toolCallsMade, SummaryFallbackKind.News, sources, ct);

        if (isLocalNews)
        {
            var locationLabel = explicitNewsLocation;
            if (string.IsNullOrWhiteSpace(locationLabel))
                locationLabel = UserLocationHint;

            response = EnsureLocalNewsLocationMention(response, locationLabel);
        }

        return response;
    }

    private static AgentResponse EnsureLocalNewsLocationMention(AgentResponse response, string? locationLabel)
    {
        if (string.IsNullOrWhiteSpace(response.Text) || string.IsNullOrWhiteSpace(locationLabel))
            return response;

        var normalizedLocation = locationLabel.Trim();
        var cityToken = normalizedLocation.Split([','], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(cityToken))
            cityToken = normalizedLocation;

        var text = response.Text;
        if (text.Contains(cityToken, StringComparison.OrdinalIgnoreCase) ||
            text.Contains(normalizedLocation, StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        var prefixed = $"{normalizedLocation} local headlines:\n\n{text}";
        return response with { Text = prefixed };
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

        if (isLocalBusinessQuery)
        {
            var inlineLocation = ExtractInlineLocationFromMessage(userMessage ?? string.Empty);
            var shouldTryOpenPlaces =
                string.IsNullOrWhiteSpace(inlineLocation) &&
                (lowerMessage.Contains("nearby", StringComparison.Ordinal) ||
                 lowerMessage.Contains("near me", StringComparison.Ordinal) ||
                 lowerMessage.Contains("local", StringComparison.Ordinal) ||
                 lowerMessage.Contains("my area", StringComparison.Ordinal) ||
                 lowerMessage.Contains("around here", StringComparison.Ordinal));

            var openPlacesAttempted = false;

            // Nearby local-business requests with no explicit city/state are a
            // good fit for places_discover (Open Places). This avoids empty
            // generic web-search results while keeping explicit-location
            // requests on the existing constrained path.
            if (shouldTryOpenPlaces)
            {
                var (attemptedOpenPlacesCurrent, openPlacesResponse) = await TryHandleLocalBusinessWithOpenPlacesAsync(
                    userMessage ?? string.Empty,
                    localBusinessLocation,
                    toolCallsMade,
                    ct);

                openPlacesAttempted = attemptedOpenPlacesCurrent;

                if (attemptedOpenPlacesCurrent && openPlacesResponse is not null)
                    return openPlacesResponse;
            }

            // If Open Places was explicitly attempted and yielded no usable
            // results, try direct place lookup before broad web search.
            if (openPlacesAttempted)
            {
                var directPlaceFallback = await TryBuildLocalBusinessDirectPlaceFallbackAsync(
                    userMessage ?? string.Empty,
                    toolCallsMade,
                    ct);
                if (directPlaceFallback is not null)
                    return directPlaceFallback;
            }
        }

        var toolResult = await CallWebSearchAsync(
            query.Query, query.Recency, toolCallsMade, ct,
            originalUserMessage: userMessage,
            maxResults: isLocalBusinessQuery ? LocalBusinessFetchMaxResults : null);

        if (isLocalBusinessQuery)
        {
            toolResult = await TryRecoverNoResultsLocalBusinessAsync(
                userMessage ?? string.Empty,
                query.Query,
                toolResult,
                localBusinessLocation,
                toolCallsMade,
                ct);
        }
        else
        {
            toolResult = await TryRecoverNoResultsWebFactFindAsync(
                userMessage ?? string.Empty,
                query.Query,
                query.Recency,
                toolResult,
                toolCallsMade,
                ct);
        }

        var hasStructuredToolError = WebToolFailureMapper.TryParseStructuredError(toolResult, out var toolErrorCode, out var toolErrorMessage);
        var hasUnavailableError = hasStructuredToolError &&
                      (($"{toolErrorCode} {toolErrorMessage}").Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                       ($"{toolErrorCode} {toolErrorMessage}").Contains("tool_unavailable", StringComparison.OrdinalIgnoreCase));

        var isNoResults = string.IsNullOrWhiteSpace(toolResult) ||
                          LooksLikeNoResultsPayload(toolResult) ||
                  hasUnavailableError ||
                  WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is not null;

        if (!isNoResults)
        {
            var rawSources = ParseSourcesFromToolResult(toolResult);
            var strippedToolResult = StripSourcesJson(toolResult);
            if (rawSources.Count == 0 && string.IsNullOrWhiteSpace(strippedToolResult))
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
            // For existence queries, use LLM general knowledge before falling
            // through to generic error handling — search provider flakiness
            // should not prevent a confident factual answer.
            var existenceResponse = await TryBuildExistenceOfflineReasoningResponseAsync(
                userMessage ?? string.Empty, toolCallsMade, ct);
            if (existenceResponse is not null)
                return existenceResponse;

            if (WebToolFailureMapper.TryBuildFailureResponse(toolResult, toolCallsMade) is { } factFailure)
            {
                // Return recognized errors (unavailable, timeout, policy) directly.
                // Wrapping in offline reasoning strips the prefix containing keywords.
                return factFailure;
            }

            if (LooksLikeNoResultsPayload(toolResult))
            {
                if (isLocalBusinessQuery)
                {
                    var directPlaceFallback = await TryBuildLocalBusinessDirectPlaceFallbackAsync(
                        userMessage ?? string.Empty,
                        toolCallsMade,
                        ct);
                    if (directPlaceFallback is not null)
                        return directPlaceFallback;

                    return BuildNoResultsResponse(userMessage ?? string.Empty, toolCallsMade);
                }

                return await BuildNoResultsFallbackAsync(
                    userMessage ?? "", memoryPackText, history, toolCallsMade, ct);
            }

            if (isLocalBusinessQuery)
            {
                var directPlaceFallback = await TryBuildLocalBusinessDirectPlaceFallbackAsync(
                    userMessage ?? string.Empty,
                    toolCallsMade,
                    ct);
                if (directPlaceFallback is not null)
                    return directPlaceFallback;

                return BuildNoResultsResponse(userMessage ?? string.Empty, toolCallsMade);
            }

            return await BuildOfflineReasoningResponseAsync(
                userMessage ?? "", memoryPackText, history, toolCallsMade, "Web search returned no results.", ct);
        }

        var sources = ParseSourcesFromToolResult(toolResult);
        var strippedContentOnly = StripSourcesJson(toolResult);
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

        var releasedProductExistence = await TryBuildReleasedProductExistenceResponseAsync(
            userMessage ?? "",
            sources,
            toolCallsMade,
            ct);
        if (releasedProductExistence is not null)
            return releasedProductExistence;

        var knownLatestVersionAnswer = OfflineWebReasoningResponder.TryBuildKnownLatestVersionAnswer(
            userMessage ?? string.Empty,
            memoryPackText);
        if (!string.IsNullOrWhiteSpace(knownLatestVersionAnswer))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "KNOWN_LATEST_VERSION_ANSWER",
                Result = "deterministic",
                Details = new Dictionary<string, object>
                {
                    ["user_message"] = Truncate(userMessage ?? string.Empty, 120),
                    ["source_count"] = sources.Count
                }
            });

            return new AgentResponse
            {
                Text = knownLatestVersionAnswer,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = 0
            };
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
            if (!string.IsNullOrWhiteSpace(strippedContentOnly))
            {
                var rawFactSummaryInput = "[Web search results — use these facts to answer the user's question]\n" + strippedContentOnly;
                var snippetInstruction = CombineMemoryAndInstruction(memoryPackText, FactFindSnippetOnlyInstruction);
                return await SummarizeAndRespond(
                    rawFactSummaryInput,
                    snippetInstruction,
                    history,
                    toolCallsMade,
                    SummaryFallbackKind.FactFind,
                    null,
                    ct);
            }

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
            // Resolve Google News RSS redirect URLs to actual article
            // URLs before filtering. This recovers navigable sources
            // that would otherwise be discarded as junk.
            var resolved = ResolveSourceUrls(sources);
            var navigable = resolved
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
                        navigable = ResolveSourceUrls(suppSources)
                            .Where(s => !IsJunkUrl(s.Url))
                            .Take(MaxFollowUpUrls)
                            .ToList();

                        // Always enrich toolResult with supplementary
                        // snippets — the LLM benefits from wider context
                        // even when no navigable URLs are found.
                        var suppText = StripSourcesJson(suppResult);
                        if (!string.IsNullOrWhiteSpace(suppText))
                            toolResult += "\n" + suppText;
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
            ? CombineMemoryAndInstruction(memoryPackText, FinanceQuoteSummaryInstruction)
            : hasArticleContent
                ? CombineMemoryAndInstruction(memoryPackText, FactFindSummaryInstruction)
                : CombineMemoryAndInstruction(memoryPackText, FactFindSnippetOnlyInstruction);

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
        string primaryRecency,
        string initialToolResult,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (!LooksLikeNoResultsPayload(initialToolResult) &&
            WebToolFailureMapper.TryBuildFailureResponse(initialToolResult, toolCallsMade) is null)
        {
            return initialToolResult;
        }

        // Skip recovery when the tool reported a structured error (unavailable,
        // timeout, etc.) — retrying with different queries won't help.
        if (WebToolFailureMapper.TryBuildFailureResponse(initialToolResult, toolCallsMade) is not null)
            return initialToolResult;

        // ── Phase 1: broaden recency with the same query ─────────────
        // When the LLM chose a tight recency (day/week/month) and got
        // zero results, widen the time window before rewriting the query.
        var lastResult = initialToolResult;
        var broaderWindows = GetBroaderRecencyWindows(primaryRecency);

        foreach (var broader in broaderWindows)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "NO_RESULTS_RECENCY_BROADEN",
                Result = "retrying",
                Details = new Dictionary<string, object>
                {
                    ["query"] = primaryQuery,
                    ["original_recency"] = primaryRecency,
                    ["broadened_recency"] = broader
                }
            });

            lastResult = await CallWebSearchAsync(
                primaryQuery, broader, toolCallsMade, ct,
                originalUserMessage: userMessage);

            if (HasUsableSearchResults(lastResult))
            {
                _audit.Append(new AuditEvent
                {
                    Actor = "search",
                    Action = "NO_RESULTS_RECENCY_BROADEN",
                    Result = "recovered",
                    Details = new Dictionary<string, object>
                    {
                        ["query"] = primaryQuery,
                        ["broadened_recency"] = broader
                    }
                });
                return lastResult;
            }
        }

        // ── Phase 2: rewrite query text with "any" recency ───────────
        var retryCandidates = BuildNoResultsRetryCandidates(userMessage, primaryQuery);

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

            if (HasUsableSearchResults(lastResult))
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

    private async Task<string> TryRecoverNoResultsLocalBusinessAsync(
        string userMessage,
        string primaryQuery,
        string initialToolResult,
        string? localBusinessLocation,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        if (!LooksLikeNoResultsPayload(initialToolResult) &&
            WebToolFailureMapper.TryBuildFailureResponse(initialToolResult, toolCallsMade) is null)
        {
            return initialToolResult;
        }

        // Skip recovery when the tool reported a structured hard failure.
        if (WebToolFailureMapper.TryBuildFailureResponse(initialToolResult, toolCallsMade) is not null)
            return initialToolResult;

        var label = GetRequestedLocalBusinessLabel(userMessage);
        var singular = SingularizeBusinessLabel(label);
        var resolvedLocation = string.IsNullOrWhiteSpace(localBusinessLocation)
            ? UserLocationHint
            : localBusinessLocation;

        var candidates = new List<string>();
        void AddCandidate(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return;

            var normalized = query.Trim();
            if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                candidates.Add(normalized);
        }

        AddCandidate(primaryQuery);

        if (!string.IsNullOrWhiteSpace(resolvedLocation))
        {
            AddCandidate($"{label} near {resolvedLocation}");
            AddCandidate($"{singular} near {resolvedLocation}");
            AddCandidate($"{label} in {resolvedLocation}");
            AddCandidate($"best {label} in {resolvedLocation}");
            AddCandidate($"{label} {resolvedLocation}");
        }
        else
        {
            AddCandidate($"{label} near me");
            AddCandidate($"{singular} near me");
            AddCandidate(label);
        }

        var lastResult = initialToolResult;
        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, primaryQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "LOCAL_BUSINESS_NO_RESULTS_RETRY",
                Result = "retrying",
                Details = new Dictionary<string, object>
                {
                    ["query"] = candidate,
                    ["location"] = resolvedLocation ?? "(none)"
                }
            });

            lastResult = await CallWebSearchAsync(
                candidate,
                "any",
                toolCallsMade,
                ct,
                originalUserMessage: userMessage,
                maxResults: LocalBusinessFetchMaxResults);

            if (HasUsableSearchResults(lastResult))
            {
                _audit.Append(new AuditEvent
                {
                    Actor = "search",
                    Action = "LOCAL_BUSINESS_NO_RESULTS_RETRY",
                    Result = "recovered",
                    Details = new Dictionary<string, object>
                    {
                        ["query"] = candidate,
                        ["location"] = resolvedLocation ?? "(none)"
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

    /// <summary>
    /// Returns progressively broader recency windows to try when the
    /// initial search returned zero results. For example, if the LLM
    /// chose "day", returns ["week", "any"]. Already-broad windows
    /// return an empty list.
    /// </summary>
    private static IReadOnlyList<string> GetBroaderRecencyWindows(string currentRecency)
    {
        return (currentRecency ?? "any").ToLowerInvariant() switch
        {
            "day"   => ["week", "any"],
            "week"  => ["month", "any"],
            "month" => ["any"],
            _       => []
        };
    }
}
