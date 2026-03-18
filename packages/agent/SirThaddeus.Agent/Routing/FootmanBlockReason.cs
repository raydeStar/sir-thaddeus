namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Machine-readable reason codes that the Footman must supply when it
/// wants to block or veto a deterministic routing decision.
///
/// The orchestrator checks this code before accepting a Footman block.
/// For <see cref="ActionTier.RetrievalSafeExternal"/> (Tier 1) routes,
/// only certain reason codes are considered valid block justifications.
/// Free-form "decline" behavior without a recognized code is rejected.
/// </summary>
public enum FootmanBlockReason
{
    /// <summary>No block — the Footman is not attempting to veto.</summary>
    None,

    /// <summary>
    /// Hard safety concern — content policy, harmful intent, etc.
    /// Valid block for all tiers.
    /// </summary>
    SafetyBlock,

    /// <summary>
    /// The user's request is outside the tool's documented scope or
    /// the tool cannot fulfill the request as stated.
    /// Valid block for Tier 1 and Tier 2.
    /// </summary>
    PolicyScopeMismatch,

    /// <summary>
    /// A required parameter is missing and cannot be inferred.
    /// Valid block for Tier 1 (Footman can request clarification).
    /// </summary>
    MissingRequiredParam,

    /// <summary>
    /// The target tool is unavailable (disabled, not connected, etc.).
    /// Valid block for all tiers.
    /// </summary>
    ToolUnavailable,

    /// <summary>
    /// The user's intent is genuinely ambiguous and cannot be resolved
    /// without clarification. Valid only for Tier 2 (complex planning).
    /// </summary>
    AmbiguousIntent,

    /// <summary>
    /// The Footman returned a reason code that could not be mapped to a
    /// known value. Treated as "no valid block reason" — the
    /// deterministic route proceeds for Tier 0/1.
    /// </summary>
    Unknown
}

/// <summary>
/// Parsing and policy helpers for <see cref="FootmanBlockReason"/>.
/// </summary>
public static class FootmanBlockReasonPolicy
{
    /// <summary>
    /// Attempts to parse a raw reason-code string (from Footman JSON)
    /// into a <see cref="FootmanBlockReason"/>. Returns
    /// <see cref="FootmanBlockReason.Unknown"/> for unrecognized values.
    /// </summary>
    public static FootmanBlockReason Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return FootmanBlockReason.None;

        // Normalize: lowercase, trim, collapse separators to underscore
        var normalized = raw.Trim().ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_');

        return normalized switch
        {
            "safety_block" or "safety" or "content_policy" => FootmanBlockReason.SafetyBlock,
            "policy_scope_mismatch" or "scope_mismatch" or "out_of_scope" => FootmanBlockReason.PolicyScopeMismatch,
            "missing_required_param" or "missing_param" or "missing_parameter" => FootmanBlockReason.MissingRequiredParam,
            "tool_unavailable" or "tool_disabled" or "tool_not_available" => FootmanBlockReason.ToolUnavailable,
            "ambiguous_intent" or "ambiguous" or "too_ambiguous" or "unclear" => FootmanBlockReason.AmbiguousIntent,
            _ => FootmanBlockReason.Unknown
        };
    }

    /// <summary>
    /// Determines whether the given block reason is sufficient to veto a
    /// deterministic routing decision at the specified action tier.
    ///
    /// <para>
    /// Tier 0 (RetrievalSafeLocal): only <see cref="FootmanBlockReason.SafetyBlock"/>
    /// and <see cref="FootmanBlockReason.ToolUnavailable"/> can block.
    /// </para>
    /// <para>
    /// Tier 1 (RetrievalSafeExternal): adds <see cref="FootmanBlockReason.PolicyScopeMismatch"/>
    /// and <see cref="FootmanBlockReason.MissingRequiredParam"/>.
    /// </para>
    /// <para>
    /// Tier 2 (PlanComplex): any non-Unknown reason is accepted.
    /// </para>
    /// </summary>
    public static bool IsValidBlockForTier(FootmanBlockReason reason, ActionTier tier)
    {
        // Safety and tool-unavailable override everything.
        if (reason == FootmanBlockReason.SafetyBlock ||
            reason == FootmanBlockReason.ToolUnavailable)
        {
            return true;
        }

        return tier switch
        {
            ActionTier.RetrievalSafeLocal =>
                // Only safety/tool-unavailable can block Tier 0 (handled above).
                false,

            ActionTier.RetrievalSafeExternal =>
                // Tier 1 also accepts scope-mismatch and missing-param.
                reason == FootmanBlockReason.PolicyScopeMismatch ||
                reason == FootmanBlockReason.MissingRequiredParam,

            ActionTier.PlanComplex =>
                // Tier 2 accepts any known reason.
                reason != FootmanBlockReason.None &&
                reason != FootmanBlockReason.Unknown,

            _ => false
        };
    }
}
