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
    /// Chain-of-thought / reasoning content when the model surfaces it in
    /// a dedicated field (e.g. LM Studio <c>reasoning_content</c>).
    /// Always null for non-thinking models.
    /// Never shown to the user — for diagnostics/logging only.
    /// </summary>
    public string? ReasoningContent { get; init; }

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
    public long RequestCount { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long TotalTokens { get; init; }
    public int ContextWindowTokens { get; init; }
}

public enum LlmTaskKind
{
    Chat,
    EmailClassification,
    EmailSummary,
    CalendarBrief,
    HealthBrief,
    DeepReasoning,
    CreativeWriting
}

public enum LlmRequestPriority
{
    UserFacing,
    Background
}

public sealed record LlmRequestContext
{
    public LlmTaskKind TaskKind { get; init; } = LlmTaskKind.Chat;
    public LlmRequestPriority Priority { get; init; } = LlmRequestPriority.UserFacing;
    public string? OperationName { get; init; }

    /// <summary>
    /// Per-request sampling temperature. When set, overrides the client's
    /// configured temperature for this call only. Null = use the configured
    /// temperature.
    /// </summary>
    public double? TemperatureOverride { get; init; }
}

public sealed record LlmRuntimeHealthSnapshot
{
    public bool LmStudioReachable { get; init; }
    public string? ModelConfigured { get; init; }
    public string? ModelLoadedOrReported { get; init; }
    public bool WarmupCompleted { get; init; }
    public int ActiveRequests { get; init; }
    public int QueuedRequests { get; init; }
    public long LastRequestDurationMs { get; init; }
    public long LastQueueWaitMs { get; init; }
    public int LastEstimatedInputTokens { get; init; }
    public int LastRequestedOutputTokens { get; init; }
    public string LastTaskKind { get; init; } = LlmTaskKind.Chat.ToString();
    public bool LastRequestWasBackground { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastWarmupAt { get; init; }
    public DateTimeOffset? LastRequestAt { get; init; }
}

public sealed record LlmWarmupResult
{
    public bool Reachable { get; init; }
    public bool Completed { get; init; }
    public string? Model { get; init; }
    public string? Error { get; init; }
    public LlmRuntimeHealthSnapshot Snapshot { get; init; } = new();
}

/// <summary>
/// Optional telemetry surface for callers that want token usage stats.
/// </summary>
public interface ILlmUsageTelemetry
{
    LlmUsageSnapshot GetUsageSnapshot();
}

public interface ILlmRuntimeDiagnostics
{
    LlmRuntimeHealthSnapshot GetRuntimeHealthSnapshot();
}

public interface ILlmWarmupClient
{
    Task<LlmWarmupResult> WarmupAsync(CancellationToken cancellationToken = default);
}

public interface ILlmModelRouter
{
    string GetModelForTask(LlmTaskKind taskKind);
}

public sealed class ConfiguredLlmModelRouter : ILlmModelRouter
{
    private readonly string _defaultModel;
    private readonly IReadOnlyDictionary<LlmTaskKind, string> _routes;

    public ConfiguredLlmModelRouter(string defaultModel, IReadOnlyDictionary<LlmTaskKind, string>? routes = null)
    {
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel) ? "auto" : defaultModel.Trim();
        _routes = routes ?? new Dictionary<LlmTaskKind, string>();
    }

    public string GetModelForTask(LlmTaskKind taskKind)
    {
        return _routes.TryGetValue(taskKind, out var routed) && !string.IsNullOrWhiteSpace(routed)
            ? routed.Trim()
            : _defaultModel;
    }
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

    public string ChatCompletionPath { get; init; } = "/v1/chat/completions";

    public string ModelsPath { get; init; } = "/v1/models";

    public string ModelLoadPath { get; init; } = "/v1/models/load";

    /// <summary>
    /// Model identifier to use.
    /// </summary>
    public string Model { get; init; } = "auto";

    public string? PreloadModelKey { get; init; }

    public bool EnableStartupWarmup { get; init; } = true;

    public bool EnableKeepWarm { get; init; } = true;

    public int ContextLength { get; init; } = 4096;

    public bool FlashAttention { get; init; } = true;

    public bool OffloadKvCacheToGpu { get; init; } = true;

    public int MaxConcurrentLlmRequests { get; init; } = 1;

    public int WarmupTimeoutSeconds { get; init; } = 120;

    public int KeepWarmIntervalMinutes { get; init; } = 30;

    public int MaxInputTokensSoftCap { get; init; } = 4000;

    public int MaxOutputTokensDefault { get; init; } = 700;

    public int RequestTimeoutSeconds { get; init; } = 300;

    public IReadOnlyDictionary<LlmTaskKind, string> ModelRoutes { get; init; } =
        new Dictionary<LlmTaskKind, string>();

    /// <summary>
    /// Maximum tokens in the response.
    /// </summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// Approximate context window size used for context-fill percentage.
    /// </summary>
    public int ContextWindowTokens { get; init; } = 16384;

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

    /// <summary>
    /// Returns true when the configured model name indicates a thinking /
    /// chain-of-thought model (e.g. "lfm2.5-1.2b-thinking", "qwq",
    /// "deepseek-r1", "o1"). Used to automatically boost
    /// <see cref="MaxTokens"/> so the think-block budget is not eaten
    /// by the token cap before the actual answer is produced.
    /// </summary>
    public bool IsThinkingModel()
    {
        var lower = (Model ?? "").ToLowerInvariant();
        return lower.Contains("thinking") ||
               lower.Contains("-think") ||
               lower.Contains("qwq") ||
               lower.StartsWith("o1") ||
               lower.Contains("deepseek-r1");
    }

    /// <summary>
    /// Effective max_tokens to send to the API. For thinking models the
    /// value is boosted to ensure the chain-of-thought budget doesn't
    /// truncate the actual answer.  The minimum boost is 4096; the
    /// configured value is always respected when it is already larger.
    /// </summary>
    public int EffectiveMaxTokens(int? explicitOverride = null)
    {
        var requested = explicitOverride ?? (MaxTokens > 0 ? MaxTokens : MaxOutputTokensDefault);
        if (!IsThinkingModel()) return requested;
        const int ThinkingMinTokens = 4096;
        return Math.Max(requested, ThinkingMinTokens);
    }
}
