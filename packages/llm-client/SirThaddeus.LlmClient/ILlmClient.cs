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
    /// Pings the LLM endpoint and returns the loaded model name if reachable,
    /// or null if the provider is offline / unreachable.
    /// This is transport-only — no state, no side effects.
    /// </summary>
    Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default);
}
