using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine.Formatting;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    private sealed record RouteResolutionResult(
        RouterOutput Route,
        IntentFeatureExtractor.WebLookupHeuristicEvidence WebEvidence,
        RoutingDecision? FootmanDecision);

    /// <summary>
    /// Runs lane classification before any tool is loaded and logs the result.
    /// </summary>
    private async Task<LaneRoutingResult> ClassifyLaneAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        var ctx = new ConversationContext
        {
            ConversationId = _currentConversationId,
            Topic = _dialogueStore.Get().Topic,
            HasRecentSearchResults = _searchOrchestrator.Session.HasRecentResults(_timeProvider.GetUtcNow())
        };

        var result = await _laneRouter.ClassifyAsync(userMessage, ctx, cancellationToken);

        LogEvent("LANE_ROUTER",
            $"lane={result.Lane}, confidence={result.Confidence:F2}, " +
            $"elapsed_ms={result.ElapsedMs:F1}, rationale={result.Rationale}");

        return result;
    }

    /// <summary>
    /// Maps a <see cref="RouterOutput"/> back to the legacy
    /// <see cref="ChatIntent"/> enum for code that still uses it
    /// (WebLookup deterministic path).
    /// </summary>
    private async Task<MemoryContextResult> GetMemoryContextSafeAsync(
        string userMessage,
        string? conversationId,
        TimeSpan? timeoutOverride,
        CancellationToken cancellationToken)
    {
        if (!MemoryEnabled)
            return new MemoryContextResult();

        try
        {
            return await _memoryContextProvider.GetContextAsync(
                new MemoryContextRequest
                {
                    UserMessage = userMessage,
                    ConversationId = conversationId,
                    MemoryEnabled = MemoryEnabled,
                    IsColdGreeting = IsColdGreeting(userMessage),
                    ActiveProfileId = ActiveProfileId,
                    Timeout = timeoutOverride ?? MemoryRetrievalTimeout
                },
                cancellationToken);
        }
        catch
        {
            // Memory is best-effort; on failure or timeout, return empty
            return new MemoryContextResult();
        }
    }

    private Task<MemoryContextResult> GetMemoryContextSafeAsync(
        string userMessage,
        string? conversationId,
        CancellationToken cancellationToken)
        => GetMemoryContextSafeAsync(userMessage, conversationId, timeoutOverride: null, cancellationToken);

    private static ChatIntent MapRouteToLegacyIntent(RouterOutput route)
    {
        return route.Intent switch
        {
            Intents.ChatOnly      => ChatIntent.Casual,
            Intents.UtilityDeterministic => ChatIntent.Casual,
            Intents.MemoryRead    => ChatIntent.Casual,
            Intents.LookupFact    => ChatIntent.WebLookup,
            Intents.LookupProduct => ChatIntent.WebLookup,
            Intents.LookupNews    => ChatIntent.WebLookup,
            Intents.LookupDeepDive => ChatIntent.WebLookup,
            Intents.LookupSearch  => ChatIntent.WebLookup,
            _                     => ChatIntent.Tooling
        };
    }

    private static LookupModeHint ResolveLookupModeHint(RouterOutput route)
    {
        return route.Intent switch
        {
            Intents.LookupFact => LookupModeHint.Fact,
            Intents.LookupProduct => LookupModeHint.Product,
            Intents.LookupNews => LookupModeHint.News,
            Intents.LookupDeepDive => LookupModeHint.DeepDive,
            _ => LookupModeHint.Auto
        };
    }

    private static LookupModeHint NormalizeLookupModeHint(
        LookupModeHint lookupModeHint,
        string lowerIncoming,
        Action<string, string> logEvent)
    {
        if (lookupModeHint == LookupModeHint.DeepDive &&
            IntentFeatureExtractor.LooksLikeGenericLocalBusinessDiscovery(lowerIncoming))
        {
            logEvent(
                "LOOKUP_MODE_LOCAL_BUSINESS_OVERRIDE",
                "Forced generic local-business discovery onto the fact-find pipeline.");
            return LookupModeHint.Fact;
        }

        if (lookupModeHint == LookupModeHint.DeepDive &&
            !IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerIncoming))
        {
            logEvent(
                "LOOKUP_MODE_DEEPDIVE_SAFETY_DOWNGRADE",
                "Downgraded a misrouted deep-dive lookup to fact-find because the prompt lacks deep-dive signals.");
            return LookupModeHint.Fact;
        }

        return lookupModeHint;
    }

    private static bool IsDeterministicInlineRoute(RouterOutput route) =>
        string.Equals(route.Intent, Intents.UtilityDeterministic, StringComparison.OrdinalIgnoreCase);

    private async Task<RouteResolutionResult> ResolveRouteAsync(
        string userMessage,
        string lowerIncoming,
        CancellationToken cancellationToken)
    {
        var hasRecentRationale = HasRecentFirstPrinciplesRationale();
        var hasRecentSearchResults = _searchOrchestrator.Session.HasRecentResults(_timeProvider.GetUtcNow());
        var routeRequest = new RouterRequest
        {
            UserMessage = userMessage,
            HasRecentFirstPrinciplesRationale = hasRecentRationale,
            HasRecentSearchResults = hasRecentSearchResults
        };

        var route = await _router.RouteAsync(routeRequest, cancellationToken);
        var webEvidence = IntentFeatureExtractor.GetWebLookupHeuristicEvidence(lowerIncoming);
        var routeIntentBeforeFootman = route.Intent;
        var routeConfidenceBeforeFootman = route.Confidence;

        LogEvent("ROUTER_WEB_EVIDENCE",
            $"phase=pre_footman, score={webEvidence.Score:0.0}, " +
            $"reason={webEvidence.ReasonCode}, shouldLookup={webEvidence.ShouldLookup}, " +
            $"confidence={webEvidence.Confidence:0.00}");

        LogEvent("ROUTER_OUTPUT",
            $"intent={route.Intent}, confidence={route.Confidence:F2}, " +
            $"web={route.NeedsWeb}, screen={route.NeedsScreenRead}, " +
            $"file={route.NeedsFileAccess}, memory_w={route.NeedsMemoryWrite}, " +
            $"system={route.NeedsSystemExecute}, risk={route.RiskLevel}, " +
            $"capabilities=[{string.Join(", ", route.RequiredCapabilities)}]");

        if (!RouteArbitrationPolicy.IsLookupIntent(route.Intent) &&
            _searchOrchestrator.Session.LastWasLocalBusinessDiscovery &&
            _searchOrchestrator.Session.LastLocalBusinessCandidateTitles.Any(candidate =>
                !string.IsNullOrWhiteSpace(candidate) &&
                userMessage.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            route = DefaultRouter.MakeRoute(
                Intents.LookupSearch,
                confidence: Math.Max(route.Confidence, 0.95),
                needsWeb: true,
                needsSearch: true,
                needsBrowser: true);

            LogEvent("ROUTER_LOCAL_BUSINESS_PROMOTION",
                "Promoted non-lookup route due to explicit mention of a prior local-business candidate.");
        }

        RoutingDecision? footmanDecision = null;
        var actionTier = ActionTierClassifier.Classify(route, lowerIncoming, webEvidence);
        if (_footmanRouter is not null && RouteArbitrationPolicy.ShouldRunFootmanForRoute(route, lowerIncoming, webEvidence))
        {
            var features = RoutingFeatures.Extract(
                userMessage,
                hasRecentRationale: hasRecentRationale,
                hasRecentSearchResults: hasRecentSearchResults);

            footmanDecision = await _footmanRouter.RouteAsync(
                userMessage, features, cancellationToken);

            if (footmanDecision.IsAuthoritative)
            {
                var footmanIntent = AgentStateMapper.ToIntentString(footmanDecision.NextState);
                var footmanRoute = DefaultRouter.MakeRoute(
                    footmanIntent,
                    confidence: footmanDecision.Confidence,
                    needsWeb: footmanDecision.AllowedToolFamilies.HasFlag(ToolFamily.WebSearch),
                    needsBrowser: footmanDecision.AllowedToolFamilies.HasFlag(ToolFamily.BrowserNavigate),
                    needsScreen: footmanDecision.AllowedToolFamilies.HasFlag(ToolFamily.ScreenCapture),
                    needsFile: footmanDecision.AllowedToolFamilies.HasFlag(ToolFamily.FileSystem),
                    needsSystem: footmanDecision.AllowedToolFamilies.HasFlag(ToolFamily.SystemExecute),
                    needsMemoryWrite: footmanDecision.AllowedToolFamilies.HasFlag(ToolFamily.MemoryWrite),
                    needsMemoryRead: footmanDecision.AllowedToolFamilies.HasFlag(ToolFamily.MemoryRead));

                if (RouteArbitrationPolicy.ShouldBlockFootmanLookupDowngrade(
                        lowerIncoming, route, footmanRoute, webEvidence,
                        footmanDecision.BlockReason))
                {
                    LogEvent("FOOTMAN_DOWNGRADE_BLOCKED",
                        $"baseIntent={route.Intent}, proposedIntent={footmanIntent}, " +
                        $"reason={footmanDecision.ReasonCode}, blockReason={footmanDecision.BlockReason}, " +
                        $"actionTier={actionTier}, webScore={webEvidence.Score:0.0}, " +
                        $"webReason={webEvidence.ReasonCode}");

                    LogRouterDisagreement(
                        userMessage, route, footmanIntent,
                        footmanDecision, actionTier, "downgrade_blocked");
                }
                else
                {
                    var isDisagreement = !string.Equals(
                        route.Intent, footmanIntent, StringComparison.OrdinalIgnoreCase);

                    route = footmanRoute;
                    LogEvent("FOOTMAN_OVERRIDE",
                        $"state={footmanDecision.NextState}, intent={footmanIntent}, " +
                        $"contextPolicy={footmanDecision.EffectiveContextPolicy}, " +
                        $"confidence={footmanDecision.Confidence:F2}, reason={footmanDecision.ReasonCode}, " +
                        $"actionTier={actionTier}");

                    if (isDisagreement)
                    {
                        LogRouterDisagreement(
                            userMessage, routeBeforeFootman: new RouterOutput
                            {
                                Intent = routeIntentBeforeFootman,
                                Confidence = routeConfidenceBeforeFootman,
                                NeedsWeb = true,
                                NeedsSearch = true
                            },
                            footmanIntent, footmanDecision, actionTier,
                            "footman_accepted");
                    }
                }
            }
            else
            {
                LogEvent("FOOTMAN_DEFERRED",
                    $"abstain={footmanDecision.Abstain}, confidence={footmanDecision.Confidence:F2}, " +
                    $"reason={footmanDecision.ReasonCode}, actionTier={actionTier} — keeping tripwire route");
            }
        }
        else
        {
            LogEvent("FOOTMAN_SKIPPED",
                $"actionTier={actionTier}, intent={route.Intent}, " +
                $"confidence={route.Confidence:F2}");
        }

        LogEvent("ROUTER_WEB_EVIDENCE",
            $"phase=post_footman, baseIntent={routeIntentBeforeFootman}, " +
            $"baseConfidence={routeConfidenceBeforeFootman:F2}, finalIntent={route.Intent}, " +
            $"finalConfidence={route.Confidence:F2}, needsWeb={route.NeedsWeb}, " +
            $"needsSearch={route.NeedsSearch}");

        if (footmanDecision is { IsAuthoritative: true })
            ApplyFootmanContextPolicy(footmanDecision.EffectiveContextPolicy);

        if (RouteArbitrationPolicy.IsLookupIntent(route.Intent) &&
            !route.Intent.Equals(Intents.LookupNews, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lowerIncoming))
        {
            route = DefaultRouter.MakeRoute(
                Intents.LookupNews,
                confidence: Math.Max(route.Confidence, 0.93),
                needsWeb: true,
                needsSearch: true);

            LogEvent("ROUTER_NEWS_NORMALIZATION",
                "Normalized misrouted lookup intent to LookupNews for an explicit news request.");
        }

        if (RouteArbitrationPolicy.IsLookupIntent(route.Intent) &&
            !route.Intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerIncoming) &&
            !IntentFeatureExtractor.LooksLikeGenericLocalBusinessDiscovery(lowerIncoming))
        {
            route = DefaultRouter.MakeRoute(
                Intents.LookupDeepDive,
                confidence: Math.Max(route.Confidence, 0.95),
                needsWeb: true,
                needsSearch: true,
                needsBrowser: true);

            LogEvent("ROUTER_DEEPDIVE_NORMALIZATION",
                "Normalized misrouted lookup intent to LookupDeepDive for an explicit deep-dive request.");
        }

        var shouldNormalizeSelfContainedLookupToChat =
            !webEvidence.ShouldLookup &&
            footmanDecision?.IsAuthoritative != true &&
            IntentFeatureExtractor.LooksLikeSelfContainedKnowledgeOrReasoningPrompt(lowerIncoming) &&
            RouteArbitrationPolicy.IsLookupIntent(route.Intent);

        if (!route.Intent.Equals(Intents.ChatOnly, StringComparison.OrdinalIgnoreCase) &&
            !route.Intent.Equals(Intents.UtilityDeterministic, StringComparison.OrdinalIgnoreCase) &&
            !route.Intent.Equals(Intents.ScreenObserve, StringComparison.OrdinalIgnoreCase) &&
            !route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
            !route.Intent.Equals(Intents.SystemTask, StringComparison.OrdinalIgnoreCase) &&
            !route.Intent.Equals(Intents.BrowseOnce, StringComparison.OrdinalIgnoreCase) &&
            shouldNormalizeSelfContainedLookupToChat)
        {
            route = DefaultRouter.MakeRoute(
                Intents.ChatOnly,
                confidence: Math.Max(route.Confidence, 0.80));

            LogEvent("ROUTER_CHAT_NORMALIZATION",
                "Downgraded misrouted prompt to ChatOnly because it is a self-contained knowledge/reasoning request.");
        }

        // ── Heuristic safety valve: downgrade WebLookup → ChatOnly ───
        // When the deterministic heuristic says "no web lookup needed"
        // AND Footman did not authoritatively confirm the lookup,
        // the LLM router likely over-indexed on surface topic words.
        // Placed before GetLookupFloorIntent so strong deterministic
        // signals (deep-dive, news, local biz) can still upgrade back.
        if (RouteArbitrationPolicy.IsLookupIntent(route.Intent) &&
            !webEvidence.ShouldLookup &&
            route.Confidence < 0.35 &&
            footmanDecision?.IsAuthoritative != true)
        {
            route = DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: route.Confidence);
            LogEvent("HEURISTIC_LOOKUP_DOWNGRADE",
                $"webScore={webEvidence.Score:0.0}, reason={webEvidence.ReasonCode} — " +
                "low heuristic evidence and no Footman confirmation, routing to chat");
        }

        var lookupFloorIntent = RouteArbitrationPolicy.GetLookupFloorIntent(lowerIncoming, route, webEvidence);
        if (!string.IsNullOrWhiteSpace(lookupFloorIntent))
        {
            route = lookupFloorIntent switch
            {
                Intents.LookupDeepDive => DefaultRouter.MakeRoute(
                    Intents.LookupDeepDive,
                    confidence: 0.95,
                    needsWeb: true,
                    needsSearch: true,
                    needsBrowser: true),
                Intents.LookupProduct => DefaultRouter.MakeRoute(
                    Intents.LookupProduct,
                    confidence: Math.Clamp(Math.Max(0.90, webEvidence.Confidence), 0.90, 0.96),
                    needsWeb: true,
                    needsSearch: true),
                Intents.LookupNews => DefaultRouter.MakeRoute(
                    Intents.LookupNews,
                    confidence: 0.93,
                    needsWeb: true,
                    needsSearch: true),
                _ => DefaultRouter.MakeRoute(
                    Intents.LookupFact,
                    confidence: Math.Clamp(Math.Max(0.88, webEvidence.Confidence), 0.88, 0.96),
                    needsWeb: true,
                    needsSearch: true)
            };

            LogEvent("LOOKUP_FLOOR_UPGRADE",
                $"intent={route.Intent}, webScore={webEvidence.Score:0.0}, " +
                $"webReason={webEvidence.ReasonCode}, shouldLookup={webEvidence.ShouldLookup}");
        }

        if (!DeepDiveEnabled &&
            route.Intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase))
        {
            route = DefaultRouter.MakeRoute(
                Intents.LookupFact,
                confidence: Math.Clamp(Math.Max(route.Confidence, 0.88), 0.88, 0.96),
                needsWeb: true,
                needsSearch: true);

            LogEvent(
                "ROUTER_PROFILE_DEEPDIVE_DOWNGRADE",
                "Normalized LookupDeepDive to LookupFact because advanced deep-dive is disabled for the active product profile.");
        }

        return new RouteResolutionResult(route, webEvidence, footmanDecision);
    }

    /// <summary>
    /// Heuristic over an assistant draft that detects "I don't know /
    /// I can't / I'm not sure"-shaped responses. Used by the legacy
    /// orchestrator's search fallback path, and re-exposed as public so
    /// the pipeline's <c>SearchFallbackStep</c> trigger can share the
    /// same refusal-detection logic instead of drifting.
    /// </summary>
    public static bool HasRefusalOrUncertaintySignals(string rawDraft, string processedDraft)
    {
        if (string.IsNullOrWhiteSpace(processedDraft))
            return true;

        var lower = processedDraft.Trim().ToLowerInvariant();
        ReadOnlySpan<string> markers =
        [
            "i don't know",
            "i dont know",
            "i'm not sure",
            "im not sure",
            "not sure",
            "i can't",
            "i cant",
            "i cannot",
            "unable to",
            "can't answer",
            "cannot answer",
            "don't have enough information",
            "do not have enough information",
            "not enough information",
            "i couldn't find",
            "i could not find",
            "i wasn't able to",
            "i was not able to",
            // Observed in real refusal drafts after web_search returned
            // results but the model couldn't pull the specific datum the
            // user asked for (e.g. weather returned timestamps instead of
            // temperature). Kept in sync with the markers above.
            "i couldn't retrieve",
            "i could not retrieve",
            "couldn't retrieve",
            "could not retrieve",
            "failed to retrieve",
            "unable to retrieve",
            "only provided",
            "didn't return",
            "did not return",
            "no direct answer",
            "no clear answer",
            "try searching",
            "i'll try to find",
            "ill try to find",
            "check back in a moment"
        ];

        foreach (var marker in markers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        if (string.IsNullOrWhiteSpace(rawDraft))
            return true;

        return false;
    }

    private static UtilityRouter.UtilityResult ToUtilityResult(DeterministicUtilityMatch match)
    {
        return new UtilityRouter.UtilityResult
        {
            Category = match.Result.Category,
            Answer = match.Result.Answer
        };
    }

    private AgentResponse? TryBuildFirstPrinciplesFollowUpResponse(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        int roundTrips)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (!LooksLikeReasoningFollowUp(lower))
            return null;

        if (!HasRecentFirstPrinciplesRationale())
            return null;

        var age = _timeProvider.GetUtcNow() - _lastFirstPrinciplesAt;

        var goal = ExtractRationaleValue(
            _lastFirstPrinciplesRationale,
            prefix: "Goal:",
            fallback: "complete the real-world objective");
        var constraint = ExtractRationaleValue(
            _lastFirstPrinciplesRationale,
            prefix: "Constraint:",
            fallback: "pick the option that is physically feasible and goal-aligned");
        var decision = ExtractRationaleValue(
            _lastFirstPrinciplesRationale,
            prefix: "Decision:",
            fallback: "choose the option that directly completes the task");

        var text =
            $"Because the goal was to {goal}. " +
            $"The deciding constraint was: {constraint}. " +
            $"So the choice was: {decision}.";

        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "FIRST_PRINCIPLES_FOLLOWUP",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["ageSeconds"] = Math.Max(0, (long)age.TotalSeconds)
            }
        });

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips,
            GuardrailsUsed = true,
            GuardrailsRationale = _lastFirstPrinciplesRationale.Take(3).ToArray()
        };
    }

    private bool HasRecentFirstPrinciplesRationale()
    {
        if (_lastFirstPrinciplesAt == default ||
            _lastFirstPrinciplesRationale.Count < 3)
        {
            return false;
        }

        var age = _timeProvider.GetUtcNow() - _lastFirstPrinciplesAt;
        return age <= FirstPrinciplesFollowUpTtl;
    }

    private static string ExtractRationaleValue(
        IReadOnlyList<string> rationale,
        string prefix,
        string fallback)
    {
        foreach (var line in rationale)
        {
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[prefix.Length..].Trim();
            value = value.TrimEnd('.', ';', ':').Trim();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        return fallback;
    }

    private static bool LooksLikeReasoningFollowUp(string lower)
        => IntentFeatureExtractor.LooksLikeReasoningFollowUp(lower);

    // Back-compat seam for reflection-based tests while greeting detection
    // logic now lives in IntentFeatureExtractor.
    private static bool LooksLikeGreeting(string lower)
        => IntentFeatureExtractor.LooksLikeGreeting(lower);

    /// <summary>
    /// Emits a structured <c>ROUTER_DISAGREEMENT</c> audit event whenever
    /// the deterministic router and the Footman produced different intents.
    /// This is the primary diagnostic for investigating Footman overrides
    /// and blocked downgrades.
    /// </summary>
    private void LogRouterDisagreement(
        string userMessage,
        RouterOutput routeBeforeFootman,
        string footmanIntent,
        RoutingDecision footmanDecision,
        ActionTier actionTier,
        string arbitrationResult)
    {
        try
        {
            _audit.Append(new AuditEvent
            {
                Actor = "agent",
                Action = "ROUTER_DISAGREEMENT",
                Result = arbitrationResult,
                Details = new Dictionary<string, object>
                {
                    ["userMessage"] = Truncate(userMessage ?? "", 120),
                    ["deterministicIntent"] = routeBeforeFootman.Intent,
                    ["deterministicConfidence"] = routeBeforeFootman.Confidence,
                    ["footmanIntent"] = footmanIntent,
                    ["footmanConfidence"] = footmanDecision.Confidence,
                    ["footmanReasonCode"] = footmanDecision.ReasonCode,
                    ["footmanBlockReason"] = footmanDecision.BlockReason.ToString(),
                    ["actionTier"] = actionTier.ToString(),
                    ["arbitrationResult"] = arbitrationResult
                }
            });
        }
        catch
        {
            // Audit logging is best-effort; agent logic must proceed.
        }
    }

    /// <summary>
    /// Detects a "cold greeting" — the very first user message after a
    /// conversation reset, and it looks like a simple hello/hi/hey.
    /// When true, memory retrieval uses <c>mode = "greet"</c> for
    /// shallow context (profile + 1-2 nuggets, no deep digging).
    /// </summary>
    private bool IsColdGreeting(string userMessage)
    {
        // Cold-start: history should contain only the system prompt +
        // the current user message (which hasn't been added yet at this
        // point, or has just been added).  Accept 1 (system only) or
        // 2 (system + this user message) entries.
        var userTurns = _history.Count(m => m.Role == "user");
        if (userTurns > 1)
            return false;

        return LooksLikeGreeting(userMessage.ToLowerInvariant().Trim());
    }
}
