using System.Text.Json.Serialization;

namespace SirThaddeus.AuditLog;

/// <summary>
/// Represents a single entry in the audit log.
/// Designed to be append-only and human-readable.
/// </summary>
public sealed record AuditEvent
{
    /// <summary>
    /// Schema version for forward compatibility.
    /// </summary>
    [JsonPropertyName("event_version")]
    public string EventVersion { get; init; } = "1.0";

    /// <summary>
    /// Timestamp of the event in UTC.
    /// </summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The entity that performed the action (e.g., "runtime", "user", "service").
    /// </summary>
    [JsonPropertyName("actor")]
    public required string Actor { get; init; }

    /// <summary>
    /// The action that was performed (e.g., "STATE_CHANGE", "STOP_ALL", "BROWSER_NAVIGATE").
    /// </summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>
    /// The target of the action, if applicable.
    /// </summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>
    /// The result of the action (e.g., "ok", "denied", "error").
    /// </summary>
    [JsonPropertyName("result")]
    public string Result { get; init; } = "ok";

    /// <summary>
    /// Optional permission token ID that authorized this action.
    /// Present for actions that required a permission token.
    /// </summary>
    [JsonPropertyName("permission_token_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionTokenId { get; init; }

    /// <summary>
    /// Additional details about the action.
    /// </summary>
    [JsonPropertyName("details")]
    public Dictionary<string, object>? Details { get; init; }
}
