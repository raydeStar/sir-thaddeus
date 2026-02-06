using System.Text.Json.Serialization;
using SirThaddeus.PermissionBroker;

namespace SirThaddeus.ToolRunner;

/// <summary>
/// Represents a request to invoke a tool.
/// Schema-like structure matching common AI tool call formats.
/// </summary>
public sealed record ToolCall
{
    /// <summary>
    /// Unique identifier for this tool call.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// The name of the tool to invoke.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Arguments to pass to the tool (JSON-serializable).
    /// </summary>
    [JsonPropertyName("arguments")]
    public Dictionary<string, object>? Arguments { get; init; }

    /// <summary>
    /// The capability required to execute this tool.
    /// </summary>
    [JsonPropertyName("required_capability")]
    public required Capability RequiredCapability { get; init; }

    /// <summary>
    /// Human-readable description of what this call intends to do.
    /// Displayed in permission prompts.
    /// </summary>
    [JsonPropertyName("purpose")]
    public required string Purpose { get; init; }

    /// <summary>
    /// Generates a unique tool call ID.
    /// </summary>
    public static string GenerateId() => $"call_{Guid.NewGuid():N}";
}
