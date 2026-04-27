using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Planning;
using SirThaddeus.Agent.Search.DeepDive;
using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Agent;

/// <summary>
/// The final output from the agent after processing a user message.
/// </summary>
public sealed record AgentResponse
{
    /// <summary>
    /// The assistant's final text reply to the user.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Whether the agent completed successfully (vs cancelled / errored).
    /// </summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// Tool calls that were executed during this turn.
    /// </summary>
    public IReadOnlyList<ToolCallRecord> ToolCallsMade { get; init; } = [];

    /// <summary>
    /// Total number of LLM round-trips for this turn.
    /// </summary>
    public int LlmRoundTrips { get; init; }

    /// <summary>
    /// Per-turn token usage for UI telemetry, when available.
    /// </summary>
    public AgentTokenUsage? TokenUsage { get; init; }

    /// <summary>
    /// When true, the desktop chat UI should skip source-card rendering
    /// for this response, even if tool output contains source metadata.
    /// Activity logs are still written.
    /// </summary>
    public bool SuppressSourceCardsUi { get; init; }

    /// <summary>
    /// Structured source citations surfaced this turn — e.g. results
    /// extracted from <c>web_search</c>'s trailing
    /// <c>&lt;!-- SOURCES_JSON --&gt;</c> block. The runtime persists
    /// these on the assistant <c>ChatMessage</c> so the UI can render
    /// rich preview cards (thumbnails, favicons, domain badges). Empty
    /// on turns that didn't invoke a citation-producing tool.
    /// </summary>
    public IReadOnlyList<AgentSource> Sources { get; init; } = [];

    /// <summary>
    /// When true, the desktop chat UI should not append the "tool activity"
    /// chat bubble for this response. Tool input/output remains in logs.
    /// </summary>
    public bool SuppressToolActivityUi { get; init; }

    /// <summary>
    /// Error message if the request failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Compact continuity snapshot for UI context chips and lock/mismatch indicators.
    /// </summary>
    public DialogueContextSnapshot? ContextSnapshot { get; init; }

    /// <summary>
    /// True when the first-principles pipeline generated this answer.
    /// </summary>
    public bool GuardrailsUsed { get; init; }

    /// <summary>
    /// Short user-facing rationale triplet (Goal / Constraint / Decision).
    /// Never includes chain-of-thought.
    /// </summary>
    public IReadOnlyList<string> GuardrailsRationale { get; init; } = [];

    /// <summary>
    /// Optional structured payload for the dedicated briefing panel.
    /// </summary>
    public DeepDiveBriefing? DeepDiveBriefing { get; init; }

    /// <summary>
    /// When true, tool-backed responses may apply personality presentation
    /// formatting (for example, signature note) while still skipping any
    /// content-altering reduction pass.
    /// </summary>
    public bool AllowToolResultPersonalityPresentation { get; init; }

    /// <summary>
    /// When true, the response is incomplete — one or more required fields
    /// from the completion contract could not be satisfied. The LLM's answer
    /// is still returned (best-effort), but the UI may want to indicate
    /// that some information is missing.
    /// </summary>
    public bool IsPartial { get; init; }

    /// <summary>
    /// Names of required fields that could not be satisfied by tool results.
    /// Empty when <see cref="IsPartial"/> is false.
    /// </summary>
    public IReadOnlyList<string> MissingFields { get; init; } = [];

    /// <summary>
    /// Correlation ID linking this response to its orchestrator turn.
    /// Matches the <see cref="Orchestration.Correlation.RunContext.CorrelationId"/>
    /// and all audit events for this turn.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Typed execution plan produced before any tool call.
    /// Null when planning was skipped or failed to produce a valid plan.
    /// UI displays this collapsed above the response.
    /// </summary>
    public TaskPlan? Plan { get; init; }

    /// <summary>
    /// Deterministic completion confidence from the completion checker.
    /// Null when completion contracts were not evaluated for this turn.
    /// </summary>
    public double? CompletionConfidence { get; init; }

    /// <summary>
    /// Completion stop reason (for diagnostics/UI), for example:
    /// "complete", "missing_required_fields", or
    /// "evidence_or_count_requirements_unmet".
    /// </summary>
    public string? CompletionStopReason { get; init; }

    /// <summary>
    /// High-level workflow completion reason used by checklist/confidence runs.
    /// Null when workflow orchestration is not enabled.
    /// </summary>
    public CompletionReason? WorkflowCompletionReason { get; init; }

    /// <summary>
    /// User-facing confidence band for workflow-enabled runs.
    /// Null when confidence evaluation is disabled.
    /// </summary>
    public string? WorkflowConfidenceBand { get; init; }

    public static AgentResponse FromError(string error) => new()
    {
        Text = error,
        Success = false,
        Error = error
    };
}

/// <summary>
/// Compact per-turn token usage payload for runtime UI counters.
/// </summary>
public sealed record AgentTokenUsage
{
    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
    public int TotalTokens { get; init; }
    public int ContextWindowTokens { get; init; }
    public int ContextFillPercent { get; init; }
}

/// <summary>
/// Record of a single tool call executed during the agent loop.
/// </summary>
public sealed record ToolCallRecord
{
    public required string ToolName { get; init; }
    public required string Arguments { get; init; }
    public required string Result { get; init; }
    public bool Success { get; init; }
}

/// <summary>
/// Agent-package representation of a citation surfaced with the
/// assistant's reply. Mirrors <c>Thaddeus.SharedTypes.ChatMessageSource</c>
/// but lives here so the agent package doesn't need a dependency on the
/// shared-types runtime layer. The runtime facade converts between the
/// two shapes when persisting the assistant message.
/// </summary>
public sealed record AgentSource
{
    public required string Url { get; init; }
    public string? Title { get; init; }
    public string? Domain { get; init; }
    public string? Excerpt { get; init; }
    /// <summary>data-URL for the favicon (e.g. "data:image/png;base64,...").</summary>
    public string? Favicon { get; init; }
    /// <summary>Absolute URL of a representative image.</summary>
    public string? Thumbnail { get; init; }
    /// <summary>ISO-8601 publish timestamp when the source is a dated article.</summary>
    public string? PublishedAt { get; init; }
}
