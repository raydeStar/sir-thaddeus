using System.Text.Json.Serialization;

namespace SirThaddeus.LlmClient;

// ─────────────────────────────────────────────────────────────────────────
// Chat Messages
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A single message in a conversation (system, user, assistant, or tool).
/// </summary>
public sealed record ChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    // ── Factory helpers ──────────────────────────────────────────────

    public static ChatMessage System(string content) => new() { Role = "system", Content = content };
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };
    public static ChatMessage Assistant(string content) => new() { Role = "assistant", Content = content };
    public static ChatMessage AssistantToolCalls(IReadOnlyList<ToolCallRequest> calls) => new()
    {
        Role = "assistant",
        ToolCalls = calls
    };
    public static ChatMessage ToolResult(string toolCallId, string content) => new()
    {
        Role = "tool",
        ToolCallId = toolCallId,
        Content = content
    };
}

// ─────────────────────────────────────────────────────────────────────────
// Tool Definitions (sent to the model)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A tool the model can choose to invoke.
/// Follows the OpenAI function-calling schema.
/// </summary>
public sealed record ToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required FunctionDefinition Function { get; init; }
}

/// <summary>
/// Schema for a callable function.
/// </summary>
public sealed record FunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("parameters")]
    public required object Parameters { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────
// Tool Call Requests (from the model)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A tool invocation requested by the model in its response.
/// </summary>
public sealed record ToolCallRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required FunctionCallDetails Function { get; init; }
}

/// <summary>
/// The function name + arguments the model wants to call.
/// </summary>
public sealed record FunctionCallDetails
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// JSON-encoded arguments string as returned by the model.
    /// </summary>
    [JsonPropertyName("arguments")]
    public required string Arguments { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────
// LLM Response
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Parsed response from the LLM.
/// </summary>
public sealed record LlmResponse
{
    /// <summary>
    /// True if the model produced a final text answer (no more tool calls).
    /// </summary>
    public required bool IsComplete { get; init; }

    /// <summary>
    /// The text content of the response (may be null when tool calls are present).
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Tool calls the model wants to make before producing a final answer.
    /// </summary>
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; init; }

    /// <summary>
    /// The raw finish reason from the API.
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>
    /// Token usage statistics if available.
    /// </summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>
/// Token consumption statistics.
/// </summary>
public sealed record TokenUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }
}

/// <summary>
/// Cumulative token usage counters captured by the transport.
/// </summary>
public sealed record LlmUsageSnapshot
{
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long TotalTokens { get; init; }
    public int ContextWindowTokens { get; init; }
}

/// <summary>
/// Optional telemetry surface for callers that want token usage stats.
/// </summary>
public interface ILlmUsageTelemetry
{
    LlmUsageSnapshot GetUsageSnapshot();
}

// ─────────────────────────────────────────────────────────────────────────
// Client Options
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Configuration for the LLM client.
/// Maps 1:1 with the "llm" section in settings.json.
/// </summary>
public sealed record LlmClientOptions
{
    /// <summary>
    /// Base URL of the OpenAI-compatible API (e.g. http://localhost:1234).
    /// </summary>
    public string BaseUrl { get; init; } = "http://localhost:1234";

    /// <summary>
    /// Model identifier to use.
    /// </summary>
    public string Model { get; init; } = "local-model";

    /// <summary>
    /// Maximum tokens in the response.
    /// </summary>
    public int MaxTokens { get; init; } = 2048;

    /// <summary>
    /// Approximate context window size used for context-fill percentage.
    /// </summary>
    public int ContextWindowTokens { get; init; } = 8192;

    /// <summary>
    /// Sampling temperature (0.0 = deterministic, 1.0 = creative).
    /// </summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>
    /// Repetition penalty — discourages the model from repeating tokens.
    /// 1.0 = no penalty. 1.1–1.15 works well for small local models
    /// that tend to loop or echo their own instructions.
    /// </summary>
    public double RepetitionPenalty { get; init; } = 1.1;

    /// <summary>
    /// Stop sequences — generation halts when any of these strings
    /// appear in the output. Prevents the model from generating fake
    /// multi-turn dialogue (a common issue with small local models).
    ///
    /// Only plain-text markers here. Template-specific tokens (im_end,
    /// eot_id, etc.) are handled natively by LM Studio's chat template
    /// engine — sending them as stop sequences can cause grammar
    /// conflicts and "Failed to process regex" 400s.
    /// </summary>
    public string[] StopSequences { get; init; } =
    [
        "\nUser:",    "\nuser:",
        "\nHuman:",   "\nhuman:",
        "\n### User",
        "\n### Human"
    ];
}
