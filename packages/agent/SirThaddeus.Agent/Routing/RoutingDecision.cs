using System.Text.Json.Serialization;

namespace SirThaddeus.Agent.Routing;

/// <summary>
/// The structured output envelope from the Footman router.
/// Consumed by the orchestrator to configure context, tool gating,
/// and primary-model dispatch.
/// </summary>
public sealed record RoutingDecision
{
    /// <summary>Schema version for forward compatibility. Must be 1.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Echo of the request ID for correlation.</summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    /// <summary>The next agent state (what the primary model should do).</summary>
    [JsonPropertyName("nextState")]
    public AgentState NextState { get; init; } = AgentState.Fallback;

    /// <summary>
    /// How much conversational context the primary model should receive.
    /// If the Footman omits this, <see cref="ContextPolicyDefaults.For"/>
    /// provides the default based on <see cref="NextState"/>.
    /// </summary>
    [JsonPropertyName("contextPolicy")]
    public ContextPolicy ContextPolicy { get; init; } = ContextPolicy.ChatSessionSnapshot;

    /// <summary>Footman's confidence in the routing decision (0.0–1.0).</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    /// <summary>
    /// If true, the Footman is explicitly abstaining — the orchestrator
    /// should fall back to the primary model with conservative defaults.
    /// </summary>
    [JsonPropertyName("abstain")]
    public bool Abstain { get; init; }

    /// <summary>
    /// Machine-readable code explaining the routing rationale.
    /// Examples: "heuristic_greeting", "llm_low_confidence", "parse_failure".
    /// </summary>
    [JsonPropertyName("reasonCode")]
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>
    /// Typed block-reason parsed from <see cref="ReasonCode"/>.
    /// When the Footman wants to veto a deterministic route, this must
    /// be a value accepted by
    /// <see cref="FootmanBlockReasonPolicy.IsValidBlockForTier"/>.
    /// Populated during parse/validate in
    /// <see cref="FastLlmFootmanRouter"/>.
    /// </summary>
    [JsonIgnore]
    public FootmanBlockReason BlockReason { get; init; } = FootmanBlockReason.None;

    // ── Derived properties (not serialized from Footman) ─────────────

    /// <summary>
    /// The deterministic tool family mask computed from <see cref="NextState"/>.
    /// Populated by the orchestrator after receiving the Footman decision.
    /// </summary>
    [JsonIgnore]
    public ToolFamily AllowedToolFamilies =>
        ToolFamilyPolicy.AllowedFamilies(NextState);

    /// <summary>
    /// The effective context policy, falling back to defaults if the
    /// Footman didn't specify one or if the decision is an abstain/fallback.
    /// </summary>
    [JsonIgnore]
    public ContextPolicy EffectiveContextPolicy =>
        Abstain ? ContextPolicyDefaults.For(AgentState.Fallback)
                : ContextPolicy;

    /// <summary>
    /// Whether this decision should be treated as authoritative (confidence
    /// above threshold and not abstaining) or as a soft suggestion.
    /// </summary>
    [JsonIgnore]
    public bool IsAuthoritative =>
        !Abstain && Confidence >= ConfidenceThreshold;

    /// <summary>
    /// The minimum confidence required for a Footman decision to be
    /// treated as authoritative. Below this, the orchestrator falls back.
    /// </summary>
    public const double ConfidenceThreshold = 0.60;

    // ── Factory methods ──────────────────────────────────────────────

    /// <summary>
    /// Creates a fallback decision used when the Footman fails, times out,
    /// or produces unparseable output.
    /// </summary>
    public static RoutingDecision CreateFallback(string requestId, string reasonCode) => new()
    {
        SchemaVersion = 1,
        RequestId = requestId,
        NextState = AgentState.Fallback,
        ContextPolicy = ContextPolicy.ChatSessionSnapshot,
        Confidence = 0.0,
        Abstain = true,
        ReasonCode = reasonCode
    };

    /// <summary>
    /// Creates a decision from a deterministic tripwire match (pre-Footman).
    /// </summary>
    public static RoutingDecision CreateDeterministic(
        string requestId,
        AgentState state,
        string reasonCode,
        double confidence = 1.0) => new()
    {
        SchemaVersion = 1,
        RequestId = requestId,
        NextState = state,
        ContextPolicy = ContextPolicyDefaults.For(state),
        Confidence = confidence,
        Abstain = false,
        ReasonCode = reasonCode
    };
}

/// <summary>
/// The Footman router contract. Implementations receive minimal input
/// (user message + deterministic features) and return a
/// <see cref="RoutingDecision"/>.
/// </summary>
public interface IFootmanRouter
{
    /// <summary>
    /// Routes a user message to an <see cref="AgentState"/> with context
    /// policy and confidence. Must complete quickly (target &lt; 200ms).
    /// </summary>
    Task<RoutingDecision> RouteAsync(
        string userMessage,
        RoutingFeatures features,
        CancellationToken cancellationToken = default);
}
