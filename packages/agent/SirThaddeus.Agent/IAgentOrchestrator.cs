using SirThaddeus.Agent.Dialogue;

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
    /// Clears the conversation history, starting a fresh session.
    /// </summary>
    void ResetConversation();

    /// <summary>
    /// Seeds dialogue continuity state from runtime-owned sources.
    /// Agent remains in-memory; runtime owns optional persistence.
    /// </summary>
    void SeedDialogueState(DialogueState state);

    /// <summary>
    /// Current compact continuity snapshot for UI/state consumers.
    /// </summary>
    DialogueContextSnapshot GetContextSnapshot();

    /// <summary>
    /// Locks context updates to explicit user location changes only.
    /// </summary>
    bool ContextLocked { get; set; }

    /// <summary>
    /// Queries the MCP server for available tools and returns the count.
    /// Returns 0 if the MCP server is unreachable. Useful for diagnostics.
    /// </summary>
    Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default);
}
