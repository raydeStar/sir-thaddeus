using System.Text.Json.Serialization;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Harness.Models;

public sealed record HarnessFixture
{
    [JsonPropertyName("test_id")]
    public string TestId { get; init; } = "";

    [JsonPropertyName("recorded_at_utc")]
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("available_tools")]
    public IReadOnlyList<string> AvailableTools { get; init; } = [];

    [JsonPropertyName("llm_turns")]
    public IReadOnlyList<RecordedLlmTurn> LlmTurns { get; init; } = [];

    [JsonPropertyName("tool_turns")]
    public IReadOnlyList<RecordedToolTurn> ToolTurns { get; init; } = [];

    [JsonPropertyName("metadata")]
    public HarnessFixtureMetadata Metadata { get; init; } = new();
}

public sealed record HarnessFixtureMetadata
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = "";

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }
}

public sealed record RecordedLlmTurn
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("is_complete")]
    public bool IsComplete { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; init; }

    [JsonPropertyName("max_tokens_override")]
    public int? MaxTokensOverride { get; init; }
}

public sealed record RecordedToolTurn
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("tool_name")]
    public string ToolName { get; init; } = "";

    [JsonPropertyName("arguments_json")]
    public string ArgumentsJson { get; init; } = "{}";

    [JsonPropertyName("result_text")]
    public string ResultText { get; init; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;
}
