namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Lightweight conversation state passed to the lane router.
/// Provides just enough context for classification without coupling
/// to the full orchestrator state.
/// </summary>
public sealed record ConversationContext
{
    /// <summary>Optional conversation identifier for session continuity.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Current dialogue topic, if any.</summary>
    public string? Topic { get; init; }

    /// <summary>Whether the conversation has recent search results in context.</summary>
    public bool HasRecentSearchResults { get; init; }

    /// <summary>Shared empty instance for callers that have no context.</summary>
    public static ConversationContext Empty { get; } = new();
}
