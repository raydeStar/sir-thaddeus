namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Classifies a routed action into authority tiers that determine how
/// the Footman interacts with the deterministic routing decision.
///
/// <list type="bullet">
///   <item>
///     <term>Tier 0 — <see cref="RetrievalSafeLocal"/></term>
///     <description>
///       Deterministic direct execution.  Footman is bypassed entirely.
///       Examples: local memory lookup, audit log read, deterministic
///       utility (time, math, conversions).
///     </description>
///   </item>
///   <item>
///     <term>Tier 1 — <see cref="RetrievalSafeExternal"/></term>
///     <description>
///       Deterministic routing is authoritative for whether a retrieval
///       action should happen.  Footman may refine query/arguments but
///       cannot veto or downgrade the action without a typed block
///       reason (see <see cref="FootmanBlockReason"/>).
///       Examples: web search, news lookup, deep dive, local business
///       discovery, browser navigate, search follow-up.
///     </description>
///   </item>
///   <item>
///     <term>Tier 2 — <see cref="PlanComplex"/></term>
///     <description>
///       Footman-led planning.  The Footman may veto, replan, or select
///       tools freely.  Examples: multi-step tasks, ambiguous intent,
///       write operations, system commands, general tooling.
///     </description>
///   </item>
/// </list>
/// </summary>
public enum ActionTier
{
    /// <summary>
    /// Tier 0 — safe local retrieval / deterministic utility.
    /// Footman bypassed; deterministic routing executes directly.
    /// </summary>
    RetrievalSafeLocal,

    /// <summary>
    /// Tier 1 — safe external retrieval (web, browser, places).
    /// Deterministic routing decides the action; Footman may only
    /// refine arguments, not veto, unless it provides a typed block
    /// reason from <see cref="FootmanBlockReason"/>.
    /// </summary>
    RetrievalSafeExternal,

    /// <summary>
    /// Tier 2 — complex / write / ambiguous actions.
    /// Footman retains full planning authority.
    /// </summary>
    PlanComplex
}

/// <summary>
/// Deterministic classifier that maps a <see cref="RouterOutput"/> and
/// heuristic evidence into the appropriate <see cref="ActionTier"/>.
/// This runs before Footman invocation to decide Footman's authority
/// scope for the current request.
/// </summary>
public static class ActionTierClassifier
{
    /// <summary>
    /// Classifies the given route into an <see cref="ActionTier"/> using
    /// the route intent, confidence, and heuristic signals.
    /// </summary>
    public static ActionTier Classify(
        RouterOutput route,
        string lowerIncoming,
        IntentFeatureExtractor.WebLookupHeuristicEvidence webEvidence)
    {
        // ── Tier 0: deterministic local / utility ────────────────────
        if (route.Intent.Equals(Intents.UtilityDeterministic, StringComparison.OrdinalIgnoreCase))
            return ActionTier.RetrievalSafeLocal;

        if (route.Intent.Equals(Intents.MemoryRead, StringComparison.OrdinalIgnoreCase))
            return ActionTier.RetrievalSafeLocal;

        // ChatOnly with no web/tool needs is safe-local (greeting, logic puzzle).
        if (route.Intent.Equals(Intents.ChatOnly, StringComparison.OrdinalIgnoreCase) &&
            !route.NeedsWeb && !route.NeedsSearch && !route.NeedsBrowserAutomation &&
            !route.NeedsFileAccess && !route.NeedsScreenRead && !route.NeedsSystemExecute)
        {
            return ActionTier.RetrievalSafeLocal;
        }

        // ── Tier 1: safe external retrieval ──────────────────────────
        // All lookup intents are retrieval-safe-external by definition.
        if (IsLookupIntent(route.Intent))
            return ActionTier.RetrievalSafeExternal;

        // BrowseOnce is a bounded read-only retrieval.
        if (route.Intent.Equals(Intents.BrowseOnce, StringComparison.OrdinalIgnoreCase))
            return ActionTier.RetrievalSafeExternal;

        // ScreenObserve is a bounded read-only capture.
        if (route.Intent.Equals(Intents.ScreenObserve, StringComparison.OrdinalIgnoreCase))
            return ActionTier.RetrievalSafeExternal;

        // ── Tier 2: everything else (write, system, ambiguous) ───────
        return ActionTier.PlanComplex;
    }

    private static bool IsLookupIntent(string intent) =>
        intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupProduct, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupNews, StringComparison.OrdinalIgnoreCase) ||
        intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase);
}
