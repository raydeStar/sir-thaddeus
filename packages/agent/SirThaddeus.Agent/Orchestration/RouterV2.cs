namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// The top-level 3-tier router:
/// Tier 1: Hard rules / Regex / Fast paths
/// Tier 2: Nearest-neighbor embedding classifier
/// Tier 3: Strict JSON LLM fallback
/// </summary>
public sealed class RouterV2
{
    private readonly NnIntentClassifier _nnClassifier;
    private readonly LlmIntentClassifier _llmClassifier;

    public RouterV2(NnIntentClassifier nnClassifier, LlmIntentClassifier llmClassifier)
    {
        _nnClassifier = nnClassifier;
        _llmClassifier = llmClassifier;
    }

    public async Task<IntentDecisionV2> RouteAsync(string userMessage, CancellationToken cancellationToken)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();

        // ── Tier 1: Hard rules ──────────────────────────────────────────
        var tier1Decision = CheckHardRules(lower);
        if (tier1Decision != null)
        {
            return tier1Decision;
        }

        // ── Tier 2: Nearest-neighbor classifier ─────────────────────────
        var nnResult = await _nnClassifier.ClassifyAsync(lower, cancellationToken);
        if (nnResult != null)
        {
            // If the NN classifies it, we still need to extract slots. We can either do regex extraction here
            // or fast-path simple intents that don't strictly require complex LLM slot extraction.
            if (nnResult.Value.Intent == "ChatOnly")
            {
                return new IntentDecisionV2
                {
                    Intent = "ChatOnly",
                    Confidence = nnResult.Value.Confidence,
                    RouteReasonCodes = ["tier2_nn"]
                };
            }
            
            // For now, if NN gives us an intent that requires slots, we will still use the LLM to get the exact slots,
            // but we can pass the intent as a hint, or just let the LLM do the whole thing.
            // A more robust implementation would use pure Regex for slots here if possible.
        }

        // ── Tier 3: LLM fallback ────────────────────────────────────────
        var llmDecision = await _llmClassifier.ClassifyAsync(userMessage ?? "", cancellationToken);
        return llmDecision with
        {
            RouteReasonCodes = ["tier3_llm"]
        };
    }

    private static IntentDecisionV2? CheckHardRules(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
        {
            return new IntentDecisionV2 { Intent = "ChatOnly", Confidence = 1.0, RouteReasonCodes = ["tier1_empty"] };
        }

        // ── Slash commands ────────────────────────────────────────────
        if (lower.StartsWith("/search ", StringComparison.Ordinal) || lower.StartsWith("search:", StringComparison.Ordinal))
        {
            var query = lower.StartsWith("/search ") ? lower[8..] : lower[7..];
            return new IntentDecisionV2
            {
                Intent = "LookupFact",
                Confidence = 1.0,
                Slots = new IntentSlots.SearchSlots { Query = query.Trim() },
                RouteReasonCodes = ["tier1_slash_command"]
            };
        }

        if (lower.StartsWith("/news ", StringComparison.Ordinal) || lower.StartsWith("news:", StringComparison.Ordinal))
        {
            var query = lower.StartsWith("/news ") ? lower[6..] : lower[5..];
            return new IntentDecisionV2
            {
                Intent = "LookupNews",
                Confidence = 1.0,
                Slots = new IntentSlots.SearchSlots { Query = query.Trim() },
                RouteReasonCodes = ["tier1_slash_command"]
            };
        }

        if (lower.StartsWith("/chat ", StringComparison.Ordinal) || lower.StartsWith("chat:", StringComparison.Ordinal))
        {
            return new IntentDecisionV2
            {
                Intent = "ChatOnly",
                Confidence = 1.0,
                RouteReasonCodes = ["tier1_slash_command"]
            };
        }

        // ── Heuristic hard rules (IntentFeatureExtractor) ─────────────
        if (Routing.IntentFeatureExtractor.LooksLikeScreenRequest(lower))
        {
            return new IntentDecisionV2
            {
                Intent = "ScreenObserve",
                Confidence = 1.0,
                RouteReasonCodes = ["tier1_heuristic"]
            };
        }

        // Deep-dive before local business: specific-place queries take priority
        if (Routing.IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower))
        {
            return new IntentDecisionV2
            {
                Intent = "LookupDeepDive",
                Confidence = 1.0,
                RouteReasonCodes = ["tier1_heuristic"]
            };
        }

        if (Routing.IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower))
        {
            return new IntentDecisionV2
            {
                Intent = "LookupNews",
                Confidence = 1.0,
                RouteReasonCodes = ["tier1_heuristic"]
            };
        }

        if (Routing.IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lower))
        {
            return new IntentDecisionV2
            {
                Intent = "LookupFact",
                Confidence = 1.0,
                RouteReasonCodes = ["tier1_heuristic"]
            };
        }

        return null;
    }
}
