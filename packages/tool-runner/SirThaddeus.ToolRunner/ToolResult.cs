using System.Text.Json.Serialization;

namespace SirThaddeus.ToolRunner;

/// <summary>
/// Represents the outcome of a tool execution.
/// </summary>
public sealed record ToolResult
{
    /// <summary>
    /// The ID of the tool call this result corresponds to.
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Whether the tool executed successfully.
    /// </summary>
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    /// <summary>
    /// The output of the tool (may be serialized data, text, etc.).
    /// </summary>
    [JsonPropertyName("output")]
    public object? Output { get; init; }

    /// <summary>
    /// Error message if the tool failed.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>
    /// Execution duration in milliseconds.
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    /// <summary>
    /// The permission token ID that authorized this execution.
    /// </summary>
    [JsonPropertyName("permission_token_id")]
    public string? PermissionTokenId { get; init; }

    // ─────────────────────────────────────────────────────────────────
    // Factory Methods
    // ─────────────────────────────────────────────────────────────────

    public static ToolResult Ok(string toolCallId, object? output, long? durationMs = null, string? tokenId = null)
        => new()
        {
            ToolCallId = toolCallId,
            Success = true,
            Output = output,
            DurationMs = durationMs,
            PermissionTokenId = tokenId
        };

    public static ToolResult Fail(string toolCallId, string error, long? durationMs = null)
        => new()
        {
            ToolCallId = toolCallId,
            Success = false,
            Error = error,
            DurationMs = durationMs
        };

    public static ToolResult PermissionDenied(string toolCallId, string reason)
        => new()
        {
            ToolCallId = toolCallId,
            Success = false,
            Error = $"Permission denied: {reason}"
        };
}
