namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Describes how much conversational context the primary model should receive
/// for a given turn. Selected by the Footman router alongside the
/// <see cref="AgentState"/> to minimise context poisoning and token waste.
/// </summary>
public enum ContextPolicy
{
    /// <summary>No prior history — isolated single-turn query.</summary>
    None,

    /// <summary>Only the last assistant message (for follow-ups).</summary>
    LastAssistantOnly,

    /// <summary>Last N turns (configurable, default 3).</summary>
    LastTurns,

    /// <summary>Full chat session snapshot — all history retained.</summary>
    ChatSessionSnapshot,

    /// <summary>Screen snapshot context — latest screen capture appended.</summary>
    ScreenSnapshot
}

/// <summary>
/// Deterministic mapping from <see cref="AgentState"/> to a default
/// <see cref="ContextPolicy"/>. The Footman may override this, but
/// if it abstains or returns an invalid policy, these defaults apply.
/// </summary>
public static class ContextPolicyDefaults
{
    /// <summary>
    /// Returns the default context policy for a given agent state.
    /// </summary>
    public static ContextPolicy For(AgentState state) => state switch
    {
        AgentState.Chat               => ContextPolicy.ChatSessionSnapshot,
        AgentState.SearchFact         => ContextPolicy.None,
        AgentState.SearchNews         => ContextPolicy.None,
        AgentState.SearchDeepDive     => ContextPolicy.LastTurns,
        AgentState.ScreenObserve      => ContextPolicy.ScreenSnapshot,
        AgentState.FileTask           => ContextPolicy.LastTurns,
        AgentState.SystemTask         => ContextPolicy.LastTurns,
        AgentState.MemoryWrite        => ContextPolicy.LastAssistantOnly,
        AgentState.MemoryRead         => ContextPolicy.LastAssistantOnly,
        AgentState.BrowseOnce         => ContextPolicy.None,
        AgentState.UtilityDeterministic => ContextPolicy.None,
        AgentState.Fallback           => ContextPolicy.ChatSessionSnapshot,
        _                             => ContextPolicy.ChatSessionSnapshot
    };

    /// <summary>
    /// Attempts to parse a raw string (from Footman JSON) into a <see cref="ContextPolicy"/>.
    /// Returns <c>null</c> on unrecognised values so callers can fall back to defaults.
    /// </summary>
    public static ContextPolicy? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim().Replace("_", "");
        if (Enum.TryParse<ContextPolicy>(normalized, ignoreCase: true, out var result))
            return result;

        return null;
    }
}
