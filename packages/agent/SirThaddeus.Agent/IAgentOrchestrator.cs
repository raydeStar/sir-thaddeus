namespace SirThaddeus.Agent;

/// <summary>
/// The agent's main processing interface.
/// Takes a user message, runs the LLM + tool loop, and returns a response.
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// Processes a user message through the full agent loop:
    /// user text -> LLM -> (tool calls -> execute -> LLM)* -> final response.
    /// </summary>
    /// <param name="userMessage">The user's input text.</param>
    /// <param name="cancellationToken">Cancellation for STOP ALL or timeout.</param>
    /// <returns>The agent's final response with audit trail.</returns>
    Task<AgentResponse> ProcessAsync(string userMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a user message with optional conversation scoping
    /// metadata for memory retrieval/persistence.
    /// </summary>
    /// <param name="userMessage">The user's input text.</param>
    /// <param name="conversationId">Conversation identifier for memory scoping.</param>
    /// <param name="cancellationToken">Cancellation for STOP ALL or timeout.</param>
    /// <returns>The agent's final response with audit trail.</returns>
    Task<AgentResponse> ProcessAsync(
        string userMessage,
        string? conversationId,
        CancellationToken cancellationToken = default);

}
