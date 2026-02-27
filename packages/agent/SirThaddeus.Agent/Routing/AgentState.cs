namespace SirThaddeus.Agent.Routing;

/// <summary>
/// The state taxonomy that the Footman router selects from.
/// Each value maps 1:1 to a <see cref="ContextPolicy"/> and a set of
/// allowed <see cref="ToolFamily"/> values via deterministic policy gates.
/// </summary>
public enum AgentState
{
    /// <summary>Casual conversation — no tools, no context fetching.</summary>
    Chat,

    /// <summary>Factual lookup — web search required.</summary>
    SearchFact,

    /// <summary>News lookup — news-oriented search.</summary>
    SearchNews,

    /// <summary>Deep dive — multi-source search + browser automation.</summary>
    SearchDeepDive,

    /// <summary>Screen observation — screenshot capture + vision.</summary>
    ScreenObserve,

    /// <summary>File system interaction — read/write files.</summary>
    FileTask,

    /// <summary>System command execution — shell / process launch.</summary>
    SystemTask,

    /// <summary>Memory write — store/update/delete user memories.</summary>
    MemoryWrite,

    /// <summary>Memory recall — retrieve stored memories.</summary>
    MemoryRead,

    /// <summary>Browser navigation — open/browse a specific URL.</summary>
    BrowseOnce,

    /// <summary>Deterministic utility — time, math, conversions, etc.</summary>
    UtilityDeterministic,

    /// <summary>
    /// Fallback — the Footman could not confidently classify.
    /// The orchestrator should use the primary model with a conservative tool set.
    /// </summary>
    Fallback
}

/// <summary>
/// Maps <see cref="AgentState"/> to the legacy <see cref="Intents"/> string constants
/// used by the existing policy gate infrastructure.
/// </summary>
public static class AgentStateMapper
{
    /// <summary>
    /// Converts a Footman <see cref="AgentState"/> to the legacy intent string
    /// consumed by <see cref="PolicyGate"/> and <see cref="RouterOutput"/>.
    /// </summary>
    public static string ToIntentString(AgentState state) => state switch
    {
        AgentState.Chat               => Intents.ChatOnly,
        AgentState.SearchFact         => Intents.LookupFact,
        AgentState.SearchNews         => Intents.LookupNews,
        AgentState.SearchDeepDive     => Intents.LookupDeepDive,
        AgentState.ScreenObserve      => Intents.ScreenObserve,
        AgentState.FileTask           => Intents.FileTask,
        AgentState.SystemTask         => Intents.SystemTask,
        AgentState.MemoryWrite        => Intents.MemoryWrite,
        AgentState.MemoryRead         => Intents.MemoryRead,
        AgentState.BrowseOnce         => Intents.BrowseOnce,
        AgentState.UtilityDeterministic => Intents.UtilityDeterministic,
        AgentState.Fallback           => Intents.GeneralTool,
        _                             => Intents.GeneralTool
    };

    /// <summary>
    /// Attempts to parse a raw string (from Footman JSON) into an <see cref="AgentState"/>.
    /// Returns <c>null</c> on unrecognised values so callers can fall back.
    /// </summary>
    public static AgentState? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Accept both PascalCase enum names and snake_case from JSON.
        var normalized = raw.Trim().Replace("_", "");
        if (Enum.TryParse<AgentState>(normalized, ignoreCase: true, out var result))
            return result;

        return null;
    }
}
