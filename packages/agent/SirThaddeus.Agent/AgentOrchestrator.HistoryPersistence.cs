using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    /// <summary>
    /// Adds a user message to the history and triggers trimming.
    /// </summary>
    public void AddUserMessageToHistory(string message)
    {
        _history.Add(ChatMessage.User(message));
        TrimHistory();
        LogEvent("AGENT_USER_MESSAGE", message);

        if (MemoryEnabled && _autoMemoryExtractor != null && !SafeModeEnabled)
        {
            _autoMemoryExtractor.FireAndForgetConversationChunk(
                message,
                _currentConversationId,
                _currentTurnTag ?? $"turn-{_turnSequence:000000}",
                role: "user");
        }
    }

    /// <summary>
    /// Appends a new assistant message to the history.
    /// </summary>
    public void AppendAssistantMessage(string message)
    {
        _history.Add(ChatMessage.Assistant(message));

        if (MemoryEnabled && _autoMemoryExtractor != null && !SafeModeEnabled)
        {
            _autoMemoryExtractor.FireAndForgetConversationChunk(
                message,
                _currentConversationId,
                _currentTurnTag ?? $"turn-{_turnSequence:000000}",
                role: "assistant");
        }
    }
}
