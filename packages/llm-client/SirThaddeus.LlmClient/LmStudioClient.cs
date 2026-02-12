using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SirThaddeus.LlmClient;

/// <summary>
/// LLM client for LM Studio (or any OpenAI-compatible endpoint).
/// Sends chat completion requests with optional tool definitions and
/// parses tool_calls from the response.
/// </summary>
public sealed class LmStudioClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly LlmClientOptions _options;
    private readonly JsonSerializerOptions _json;

    public LmStudioClient(LlmClientOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress ??= new Uri(options.BaseUrl.TrimEnd('/'));
        _http.Timeout = TimeSpan.FromSeconds(120);

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
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
        // Some models / model backends choke on non-standard params
        // (repetition_penalty) or certain stop sequences. If we detect
        // the characteristic "Failed to process regex" 400, retry once
        // with a bare request. This keeps the client model-agnostic.
        if ((int)response.StatusCode == 400 &&
            errorBody.Contains("Failed to process regex", StringComparison.OrdinalIgnoreCase))
        {
            var bare = BuildRequestBody(requestMessages, tools, maxTokensOverride, includeExtras: false);

            response = await _http.PostAsJsonAsync(
                "/v1/chat/completions", bare, _json, cancellationToken);

            if (response.IsSuccessStatusCode)
                return await ParseResponse(response, cancellationToken);

            errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
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
        var body = new Dictionary<string, object>
        {
            ["model"]       = _options.Model,
            ["messages"]    = messages,
            ["max_tokens"]  = maxTokensOverride ?? _options.MaxTokens,
            ["temperature"] = _options.Temperature,
            ["stream"]      = false
        };

        if (includeExtras)
        {
            // Repetition penalty — not part of the OpenAI spec, but
            // supported by llama.cpp / LM Studio for most models.
            if (_options.RepetitionPenalty is > 0 and not 1.0)
                body["repetition_penalty"] = _options.RepetitionPenalty;

            // Stop sequences — plain-text only (no template tokens).
            if (_options.StopSequences is { Length: > 0 })
                body["stop"] = _options.StopSequences;
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
