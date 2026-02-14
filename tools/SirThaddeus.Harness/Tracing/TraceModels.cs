using System.Text.Json.Serialization;

namespace SirThaddeus.Harness.Tracing;

public sealed record TraceStep
{
    [JsonPropertyName("step_index")]
    public int StepIndex { get; init; }

    [JsonPropertyName("step_type")]
    public string StepType { get; init; } = "";

    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("ended_at")]
    public DateTimeOffset? EndedAt { get; init; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("error")]
    public TraceError? Error { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }

    [JsonPropertyName("is_complete")]
    public bool? IsComplete { get; init; }
}

public sealed record TraceError
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("retriable")]
    public bool Retriable { get; init; }
}
