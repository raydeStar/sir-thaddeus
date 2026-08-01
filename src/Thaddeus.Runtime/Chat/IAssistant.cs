using SirThaddeus.Agent.Pipeline;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Common shape for any assistant that can produce a reply for a given thread.
/// Implementations (StubAssistant, LmStudioAssistant) stream deltas through
/// <see cref="ChatTurnPublisher"/> as they go and persist the final
/// <see cref="ChatMessage"/> to the thread store before returning.
/// </summary>
public interface IAssistant
{
    /// <summary>
    /// Generate, stream, and persist an assistant reply for the supplied user
    /// turn. The returned <see cref="ChatMessage"/> is the persisted assistant
    /// message (already appended to the thread).
    /// </summary>
    Task<ChatMessage> RespondAsync(string threadId, string userText, CancellationToken ct);

    Task<ChatMessage> RespondAsync(
        string threadId,
        string userText,
        AssistantTurnOptions options,
        CancellationToken ct) =>
        RespondAsync(threadId, userText, ct);
}

public sealed record AssistantTurnOptions(
    bool EphemeralMemory = false,
    WikiMutationTarget? WikiMutationTarget = null);
