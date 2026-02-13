using SirThaddeus.Agent.Dialogue;

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
