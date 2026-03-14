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
    /// <summary>
    /// Maps a <see cref="RouterOutput"/> back to the legacy
    /// <see cref="ChatIntent"/> enum for code that still uses it
    /// (WebLookup deterministic path).
    /// </summary>
    private async Task<MemoryContextResult> GetMemoryContextSafeAsync(string userMessage, CancellationToken cancellationToken)
    {
        if (!MemoryEnabled)
            return new MemoryContextResult();

        try
        {
            return await _memoryContextProvider.GetContextAsync(
                new MemoryContextRequest
                {
                    UserMessage = userMessage,
                    MemoryEnabled = MemoryEnabled,
                    IsColdGreeting = IsColdGreeting(userMessage),
                    ActiveProfileId = ActiveProfileId,
                    Timeout = MemoryRetrievalTimeout
                },
                cancellationToken);
        }
        catch
        {
            // Memory is best-effort; on failure or timeout, return empty
            return new MemoryContextResult();
        }
    }

    private static ChatIntent MapRouteToLegacyIntent(RouterOutput route)
    {
        return route.Intent switch
        {
            Intents.ChatOnly      => ChatIntent.Casual,
            Intents.UtilityDeterministic => ChatIntent.Casual,
            Intents.MemoryRead    => ChatIntent.Casual,
            Intents.LookupFact    => ChatIntent.WebLookup,
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
            Intents.LookupNews => LookupModeHint.News,
            Intents.LookupDeepDive => LookupModeHint.DeepDive,
            _ => LookupModeHint.Auto
        };
    }

    private static bool IsDeterministicInlineRoute(RouterOutput route) =>
        string.Equals(route.Intent, Intents.UtilityDeterministic, StringComparison.OrdinalIgnoreCase);

    private static bool IsLookupIntent(string intent) =>
        intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupNews, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decides whether the Footman LLM router should run for this request.
    /// Uses <see cref="ActionTier"/> to enforce the authority model:
    /// <list type="bullet">
    ///   <item>Tier 0 (RetrievalSafeLocal): Footman always bypassed.</item>
    ///   <item>Tier 1 (RetrievalSafeExternal): Footman bypassed when the
    ///         deterministic signal is strong (confirmed by intent-specific
    ///         heuristics). Runs only when confidence is low and the
    ///         deterministic match is weak — but even then, its output is
    ///         subject to typed-block-reason enforcement.</item>
    ///   <item>Tier 2 (PlanComplex): Footman always runs.</item>
    /// </list>
    /// </summary>
    internal static bool ShouldRunFootmanForRoute(
        RouterOutput route,
        string lowerIncoming,
        IntentFeatureExtractor.WebLookupHeuristicEvidence webEvidence)
    {
        var tier = ActionTierClassifier.Classify(route, lowerIncoming, webEvidence);

        // Tier 0 — deterministic direct execution, no Footman.
        if (tier == ActionTier.RetrievalSafeLocal)
            return false;

        // Tier 2 — Footman retains full authority.
        if (tier == ActionTier.PlanComplex)
            return true;

        // Tier 1 — retrieval-safe-external.
        // Bypass Footman for strong deterministic signals to prevent
        // stochastic downgrades into chat-only paths.

        if (route.Intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerIncoming))
        {
            return false;
        }

        if (route.Intent.Equals(Intents.LookupNews, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lowerIncoming))
        {
            return false;
        }

        // Local business discovery has a strong deterministic signal:
        // "business term + proximity cue" is unambiguous.
        if (route.Intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lowerIncoming))
        {
            return false;
        }

        if (route.Intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) &&
            webEvidence.ShouldLookup &&
            webEvidence.Score >= 2.8)
        {
            return false;
        }

        // LookupSearch follow-ups are validated by Tier-1 via
        // IsFollowUpMessage + HasRecentSearchResults. The deterministic
        // session context is authoritative — skip the Footman to prevent
        // stochastic downgrades from losing web_search on follow-ups.
        if (route.Intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase) &&
            Search.SearchModeRouter.IsFollowUpMessage(lowerIncoming))
        {
            return false;
        }

        // ScreenObserve with a confirmed screen-request signal.
        if (route.Intent.Equals(Intents.ScreenObserve, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeScreenRequest(lowerIncoming))
        {
            return false;
        }

        // Low-confidence Tier 1 — let Footman refine (but its downgrade
        // authority is still limited by typed block reasons).
        if (route.Confidence < 0.95)
            return true;

        // High-confidence Tier 1 without a matching heuristic bypass:
        // still allow Footman to refine arguments when the route already
        // carries web/search needs.
        if (IsLookupIntent(route.Intent))
            return true;

        return route.NeedsWeb || route.NeedsSearch || route.NeedsBrowserAutomation;
    }

    /// <summary>
    /// Determines whether a Footman downgrade from a lookup intent to a
    /// non-lookup intent should be blocked.
    ///
    /// Uses the <see cref="ActionTier"/> model:
    /// <list type="bullet">
    ///   <item>Tier 0/1: downgrade blocked unless the Footman provides a
    ///         typed <see cref="FootmanBlockReason"/> that is valid for
    ///         the tier.</item>
    ///   <item>Tier 2: existing behavior (Footman authoritative).</item>
    /// </list>
    /// </summary>
    internal static bool ShouldBlockFootmanLookupDowngrade(
        string lowerIncoming,
        RouterOutput baseRoute,
        RouterOutput footmanRoute,
        IntentFeatureExtractor.WebLookupHeuristicEvidence webEvidence,
        FootmanBlockReason blockReason = FootmanBlockReason.None)
    {
        if (!IsLookupIntent(baseRoute.Intent))
            return false;

        // Footman kept it as a lookup — no downgrade, nothing to block.
        if (IsLookupIntent(footmanRoute.Intent))
            return false;

        // Classify the base route to determine authority boundaries.
        var tier = ActionTierClassifier.Classify(baseRoute, lowerIncoming, webEvidence);

        // For Tier 0 and Tier 1, block the downgrade unless the Footman
        // supplied a valid typed block reason for this tier.
        if (tier == ActionTier.RetrievalSafeLocal ||
            tier == ActionTier.RetrievalSafeExternal)
        {
            return !FootmanBlockReasonPolicy.IsValidBlockForTier(blockReason, tier);
        }

        // Tier 2 — preserve legacy heuristic blocks for important intent
        // families (deep-dive, news, strong fact, search follow-up).
        if (baseRoute.Intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerIncoming))
        {
            return true;
        }

        if (baseRoute.Intent.Equals(Intents.LookupNews, StringComparison.OrdinalIgnoreCase) &&
            IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lowerIncoming))
        {
            return true;
        }

        if (baseRoute.Intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) &&
            IsLookupFloorEligiblePrompt(lowerIncoming) &&
            webEvidence.ShouldLookup &&
            webEvidence.Score >= 2.8)
        {
            return true;
        }

        if (baseRoute.Intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? GetLookupFloorIntent(
        string lowerIncoming,
        RouterOutput finalRoute,
        IntentFeatureExtractor.WebLookupHeuristicEvidence webEvidence)
    {
        if (IsLookupIntent(finalRoute.Intent))
            return null;

        if (!IsLookupFloorEligiblePrompt(lowerIncoming))
            return null;

        if (IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerIncoming))
            return Intents.LookupDeepDive;

        if (IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lowerIncoming))
            return Intents.LookupNews;

        // Local business discovery is a strong deterministic signal.
        if (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lowerIncoming))
            return Intents.LookupFact;

        if (webEvidence.ShouldLookup && webEvidence.Score >= 2.8)
            return Intents.LookupFact;

        return null;
    }

    private static bool IsLookupFloorEligiblePrompt(string lowerIncoming)
    {
        if (string.IsNullOrWhiteSpace(lowerIncoming))
            return false;

        if (IntentFeatureExtractor.LooksLikeGreeting(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeConversationalCheckIn(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeVoiceMicCheck(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeFileRequest(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeScreenRequest(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeSystemCommand(lowerIncoming) ||
            lowerIncoming.Contains("tell me about yourself", StringComparison.Ordinal) ||
            lowerIncoming.Contains("about your favorite", StringComparison.Ordinal) ||
            lowerIncoming.Contains("favorite thing", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what do you think", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what's your opinion", StringComparison.Ordinal) ||
            lowerIncoming.Contains("whats your opinion", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what's your take", StringComparison.Ordinal) ||
            lowerIncoming.Contains("whats your take", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool HasRefusalOrUncertaintySignals(string rawDraft, string processedDraft)
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
            "i was not able to"
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
