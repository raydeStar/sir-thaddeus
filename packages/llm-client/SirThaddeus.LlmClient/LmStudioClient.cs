using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SirThaddeus.LlmClient;

/// <summary>
/// LLM client for LM Studio (or any OpenAI-compatible endpoint).
/// Sends chat completion requests with optional tool definitions and
/// parses tool_calls from the response.
/// </summary>
public sealed class LmStudioClient : ILlmClient, ILlmUsageTelemetry, IDisposable
{
    private HttpClient _http;
    private readonly object _optionsGate = new();
    private LlmClientOptions _options;
    private readonly JsonSerializerOptions _json;
    private long _promptTokensTotal;
    private long _completionTokensTotal;
    private long _totalTokensTotal;

    public LmStudioClient(LlmClientOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress ??= new Uri(options.BaseUrl.TrimEnd('/'));
        
        // ── Sir Thaddeus notes: A butler must exhibit patience! ───
        // Local GPUs require time to sweep their VRAM floors. 
        // 120 seconds is too hasty; 300 seconds ensures enterprise stability.
        _http.Timeout = TimeSpan.FromSeconds(300);

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>
    /// Applies updated transport/model settings at runtime.
    /// Creates a fresh HttpClient because .NET forbids changing
    /// BaseAddress after the first request has been sent.
    /// </summary>
    public void UpdateOptions(LlmClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_optionsGate)
        {
            _options = options;

            var targetBase = options.BaseUrl.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(targetBase))
            {
                // HttpClient.BaseAddress is immutable after first use.
                // Replace the entire client to avoid InvalidOperationException.
                var oldClient = _http;
                _http = new HttpClient
                {
                    BaseAddress = new Uri(targetBase),
                    Timeout = TimeSpan.FromSeconds(300)
                };

                // Dispose the old client on a background thread to avoid
                // blocking if a request is in flight.
                Task.Run(() =>
                {
                    try { oldClient.Dispose(); }
                    catch { /* best effort */ }
                });
            }
        }
    }

    /// <inheritdoc />
    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        return await ChatCoreAsync(messages, tools, maxTokensOverride: null, cancellationToken);
    }

    /// <summary>
    /// Chat with an explicit max_tokens cap. Useful for intent-specific
    /// calls where the orchestrator knows the expected output length
    /// (e.g., casual chat = short, web summary = medium).
    /// </summary>
    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        CancellationToken cancellationToken = default)
    {
        return await ChatCoreAsync(messages, tools, maxTokensOverride, cancellationToken);
    }

    private async Task<LlmResponse> ChatCoreAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var requestMessages = tools is { Count: > 0 }
            ? messages
            : NormalizeMessagesForPlainChat(messages);

        // ── Attempt 1: full request with stop + repetition_penalty ───
        var body = BuildRequestBody(requestMessages, tools, maxTokensOverride, includeExtras: true);

        var response = await _http.PostAsJsonAsync(
            "/v1/chat/completions", body, _json, cancellationToken);

        if (response.IsSuccessStatusCode)
            return await ParseResponse(response, cancellationToken);

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // ── Self-healing: regex failure → retry without extras ────────
        // Sir Thaddeus notes: When the magic fizzles, try a simpler spell.
        if ((int)response.StatusCode == 400 &&
            errorBody.Contains("Failed to process regex", StringComparison.OrdinalIgnoreCase))
        {
            var bare = BuildRequestBody(requestMessages, tools, maxTokensOverride, includeExtras: false);

            response = await _http.PostAsJsonAsync(
                "/v1/chat/completions", bare, _json, cancellationToken);

            if (response.IsSuccessStatusCode)
                return await ParseResponse(response, cancellationToken);

            errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            
            // If the bare request still fails, it is highly likely the local model 
            // is not properly instructed for tool schemas. We must inform the user elegantly.
            throw new HttpRequestException(
                $"Enterprise Alert: The local model failed to parse the tool schema. " +
                $"Please ensure you are using an 'Instruct' or tool-calling capable model in LM Studio. " +
                $"Original LLM error: {(int)response.StatusCode} ({response.ReasonPhrase}): {errorBody}");
        }

        throw new HttpRequestException(
            $"LLM returned {(int)response.StatusCode} ({response.ReasonPhrase}): {errorBody}");
    }

    /// <summary>
    /// Some chat templates (including popular LM Studio defaults) expect
    /// at most one leading system message, followed by strict user/assistant
    /// alternation. When tools are disabled, strip tool scaffolding and
    /// compact role runs so plain-chat requests stay template-safe.
    /// </summary>
    private static IReadOnlyList<ChatMessage> NormalizeMessagesForPlainChat(
        IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
            return messages;

        ChatMessage? system = null;
        var turns = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            var role = message.Role?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(role))
                continue;

            if (role == "system")
            {
                if (system is null && !string.IsNullOrWhiteSpace(message.Content))
                    system = ChatMessage.System(message.Content!);
                continue;
            }

            if (role == "tool")
                continue;

            if (role == "assistant" &&
                message.ToolCalls is { Count: > 0 } &&
                string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if ((role == "user" || role == "assistant") &&
                !string.IsNullOrWhiteSpace(message.Content))
            {
                turns.Add(role == "user"
                    ? ChatMessage.User(message.Content!)
                    : ChatMessage.Assistant(message.Content!));
            }
        }

        var alternating = new List<ChatMessage>(turns.Count);
        foreach (var turn in turns)
        {
            if (alternating.Count == 0)
            {
                // Templates usually expect the first conversational turn to be user.
                if (turn.Role == "assistant")
                    continue;

                alternating.Add(turn);
                continue;
            }

            var previous = alternating[^1];
            if (string.Equals(previous.Role, turn.Role, StringComparison.Ordinal))
            {
                var merged = string.Concat(
                    previous.Content?.TrimEnd(),
                    "\n",
                    turn.Content?.TrimStart());

                alternating[^1] = turn.Role == "user"
                    ? ChatMessage.User(merged)
                    : ChatMessage.Assistant(merged);
                continue;
            }

            alternating.Add(turn);
        }

        // Never send an empty message array to the backend.
        if (alternating.Count == 0)
            return system is null
                ? [ChatMessage.User("Hello")]
                : [system, ChatMessage.User("Hello")];

        if (system is null)
            return alternating;

        var normalized = new List<ChatMessage>(alternating.Count + 1) { system };
        normalized.AddRange(alternating);
        return normalized;
    }

    // ─────────────────────────────────────────────────────────────────
    // Request / Response Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the JSON request body. When <paramref name="includeExtras"/>
    /// is false, non-standard parameters (stop sequences, repetition
    /// penalty) are omitted for maximum model compatibility.
    /// </summary>
    private Dictionary<string, object> BuildRequestBody(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride,
        bool includeExtras)
    {
        var options = GetOptionsSnapshot();
        var body = new Dictionary<string, object>
        {
            ["model"]       = options.Model,
            ["messages"]    = messages,
            ["max_tokens"]  = maxTokensOverride ?? options.MaxTokens,
            ["temperature"] = options.Temperature,
            ["stream"]      = false
        };

        if (includeExtras)
        {
            // Repetition penalty — not part of the OpenAI spec, but
            // supported by llama.cpp / LM Studio for most models.
            if (options.RepetitionPenalty is > 0 and not 1.0)
                body["repetition_penalty"] = options.RepetitionPenalty;

            // Stop sequences — plain-text only (no template tokens).
            if (options.StopSequences is { Length: > 0 })
                body["stop"] = options.StopSequences;
        }

        if (tools is { Count: > 0 })
        {
            body["tools"]       = tools;
            body["tool_choice"] = "auto";
        }
        // When tools is null/empty, intentionally omit both fields.
        // Sending tools:[] or tool_choice:"none" can trigger LM Studio's
        // grammar engine to compile an empty/degenerate pattern, which
        // fails with "Failed to process regex" on some models.

        return body;
    }

    /// <summary>
    /// Reads and deserializes a successful chat completion response.
    /// </summary>
    private async Task<LlmResponse> ParseResponse(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var completion = JsonSerializer.Deserialize<CompletionResponse>(raw, _json);
        TrackUsage(completion?.Usage);

        if (completion?.Choices is not { Count: > 0 })
        {
            return new LlmResponse
            {
                IsComplete    = true,
                Content       = "[No response from model]",
                FinishReason  = "error"
            };
        }

        var choice  = completion.Choices[0];
        var message = choice.Message;

        var hasToolCalls = message?.ToolCalls is { Count: > 0 };

        return new LlmResponse
        {
            IsComplete   = !hasToolCalls,
            Content      = message?.Content,
            ToolCalls    = message?.ToolCalls,
            FinishReason = choice.FinishReason,
            Usage        = completion.Usage
        };
    }

    public LlmUsageSnapshot GetUsageSnapshot()
    {
        var options = GetOptionsSnapshot();
        var contextWindow = options.ContextWindowTokens > 0
            ? options.ContextWindowTokens
            : 8192;

        return new LlmUsageSnapshot
        {
            PromptTokens = System.Threading.Interlocked.Read(ref _promptTokensTotal),
            CompletionTokens = System.Threading.Interlocked.Read(ref _completionTokensTotal),
            TotalTokens = System.Threading.Interlocked.Read(ref _totalTokensTotal),
            ContextWindowTokens = contextWindow
        };
    }

    /// <inheritdoc />
    public async Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync("/v1/models", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(raw);

            // LM Studio's /v1/models returns { data: [{ id: "model-name", ... }] }
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0)
            {
                return data[0].TryGetProperty("id", out var id)
                    ? id.GetString() ?? "unknown"
                    : "connected";
            }

            return "connected";
        }
        catch
        {
            // Endpoint not reachable — LM Studio is likely not running
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private LlmClientOptions GetOptionsSnapshot()
    {
        lock (_optionsGate)
            return _options;
    }

    private void TrackUsage(TokenUsage? usage)
    {
        if (usage is null)
            return;

        if (usage.PromptTokens > 0)
        {
            System.Threading.Interlocked.Add(
                ref _promptTokensTotal,
                usage.PromptTokens);
        }

        if (usage.CompletionTokens > 0)
        {
            System.Threading.Interlocked.Add(
                ref _completionTokensTotal,
                usage.CompletionTokens);
        }

        if (usage.TotalTokens > 0)
        {
            System.Threading.Interlocked.Add(
                ref _totalTokensTotal,
                usage.TotalTokens);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Internal DTOs matching the OpenAI response shape
    // ─────────────────────────────────────────────────────────────────

    private sealed record CompletionResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("choices")]
        public List<CompletionChoice>? Choices { get; init; }

        [JsonPropertyName("usage")]
        public TokenUsage? Usage { get; init; }
    }

    private sealed record CompletionChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("message")]
        public ChoiceMessage? Message { get; init; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; init; }
    }

    private sealed record ChoiceMessage
    {
        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("tool_calls")]
        public List<ToolCallRequest>? ToolCalls { get; init; }
    }
}
