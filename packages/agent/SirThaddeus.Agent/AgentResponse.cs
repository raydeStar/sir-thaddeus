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

    public static AgentResponse FromError(string error) => new()
    {
        Text = error,
        Success = false,
        Error = error
    };
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
