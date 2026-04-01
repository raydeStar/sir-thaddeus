using System.Text.Json.Serialization;

namespace SirThaddeus.Config;

/// <summary>
/// Control-plane settings added for v1 release hardening.
/// Kept in a separate partial file so the primary settings file
/// does not continue growing as a monolith.
/// </summary>
public sealed partial record AppSettings
{
    public const int CurrentSchemaVersion = 4;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("runtimeSafety")]
    public RuntimeSafetySettings RuntimeSafety { get; init; } = new();

    [JsonPropertyName("toolBudgets")]
    public ToolBudgetSettings ToolBudgets { get; init; } = new();
}

/// <summary>
/// Global runtime safety controls persisted in settings.
/// </summary>
public sealed record RuntimeSafetySettings
{
    [JsonPropertyName("panicMode")]
    public bool PanicMode { get; init; }

    [JsonPropertyName("safeMode")]
    public bool SafeMode { get; init; }

    [JsonPropertyName("safeModeReason")]
    public string SafeModeReason { get; init; } = "";

    [JsonPropertyName("safeModeSinceUtc")]
    public string SafeModeSinceUtc { get; init; } = "";

    [JsonPropertyName("strictHandshake")]
    public bool StrictHandshake { get; init; } = true;

    [JsonPropertyName("requiredProtocolVersion")]
    public string RequiredProtocolVersion { get; init; } = "2024-11-05";

    [JsonPropertyName("requiredServerContractVersion")]
    public string RequiredServerContractVersion { get; init; } = "1.0";
}

/// <summary>
/// Hard caps to prevent runaway tool loops.
/// </summary>
public sealed record ToolBudgetSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("maxToolCallsPerTurn")]
    public int MaxToolCallsPerTurn { get; init; } = 8;

    [JsonPropertyName("maxToolCallsPerSession")]
    public int MaxToolCallsPerSession { get; init; } = 200;

    [JsonPropertyName("maxWebPullsPerTurn")]
    public int MaxWebPullsPerTurn { get; init; } = 8;

    [JsonPropertyName("maxFileOpsPerMinute")]
    public int MaxFileOpsPerMinute { get; init; } = 30;

    public ToolBudgetSettings Normalize() => this with
    {
        MaxToolCallsPerTurn = Math.Clamp(MaxToolCallsPerTurn, 1, 100),
        MaxToolCallsPerSession = Math.Clamp(MaxToolCallsPerSession, 1, 10_000),
        MaxWebPullsPerTurn = Math.Clamp(MaxWebPullsPerTurn, 0, 25),
        MaxFileOpsPerMinute = Math.Clamp(MaxFileOpsPerMinute, 0, 600)
    };
}

