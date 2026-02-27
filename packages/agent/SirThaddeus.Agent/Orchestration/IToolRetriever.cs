using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// Filters the massive global set of tools down to the Top-K most relevant
/// tools based on the user's intent and query, preventing LLM distraction.
/// </summary>
public interface IToolRetriever
{
    /// <summary>
    /// Returns the most relevant tools for the given intent and user message.
    /// </summary>
    Task<IReadOnlyList<ToolDefinition>> RetrieveAsync(
        IntentDecisionV2 decision,
        string userMessage,
        IReadOnlyList<ToolDefinition> allowedTools,
        CancellationToken cancellationToken);
}
