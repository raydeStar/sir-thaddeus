namespace SirThaddeus.LlmClient;

/// <summary>
/// Abstraction for any OpenAI-compatible chat completions endpoint.
/// Swappable between LM Studio, Ollama, OpenAI, or any compatible provider.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Sends a chat completion request and returns the model's response.
    /// </summary>
    Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a chat completion request with an explicit max_tokens cap.
    /// Useful when the orchestrator knows the expected output length.
    /// </summary>
    Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="ChatAsync(IReadOnlyList{ChatMessage}, IReadOnlyList{ToolDefinition}?, CancellationToken)"/>
    /// but forces the model's next action to be a call to
    /// <paramref name="forcedToolName"/> — translated to OpenAI's
    /// <c>tool_choice</c>. Used by routing steps that know the answer
    /// structurally requires a specific tool (e.g. freshness verification
    /// for existence/recency questions).
    ///
    /// <para>Default implementation ignores the forced tool and falls back
    /// to regular auto-routing — so existing fakes and non-OpenAI-compatible
    /// clients keep compiling without a behavior change. Real clients (like
    /// <c>LmStudioClient</c>) override to pass the directive through.</para>
    /// </summary>
    Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        string? forcedToolName,
        CancellationToken cancellationToken = default) =>
        ChatAsync(messages, tools, cancellationToken);

    /// <summary>
    /// Sends a chat completion request with both a max_tokens cap and an
    /// optional forced tool choice.
    /// </summary>
    Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        string? forcedToolName,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(forcedToolName)
            ? ChatAsync(messages, tools, maxTokensOverride, cancellationToken)
            : ChatAsync(messages, tools, forcedToolName, cancellationToken);

    /// <summary>
    /// Chat with an explicit max_tokens cap and a per-call sampling
    /// <paramref name="temperatureOverride"/> that overrides the client's
    /// configured temperature for this request only. This supports callers
    /// that need request-specific sampling without changing the client's
    /// configured default.
    ///
    /// <para>The default implementation ignores the override (so fakes and
    /// non-OpenAI-compatible clients keep compiling unchanged); real clients
    /// like <c>LmStudioClient</c> apply it.</para>
    /// </summary>
    Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        double temperatureOverride,
        CancellationToken cancellationToken = default) =>
        ChatAsync(messages, tools, maxTokensOverride, cancellationToken);

    /// <summary>
    /// Pings the LLM endpoint and returns the loaded model name if reachable,
    /// or null if the provider is offline / unreachable.
    /// This is transport-only — no state, no side effects.
    /// </summary>
    Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default);
}
