namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Coarse tool families used by the deterministic policy gate to decide
/// which tool capabilities are exposed for a given <see cref="AgentState"/>.
/// Each family maps to one or more <see cref="ToolCapability"/> values.
/// </summary>
[Flags]
public enum ToolFamily
{
    None             = 0,
    WebSearch        = 1 << 0,
    BrowserNavigate  = 1 << 1,
    FileSystem       = 1 << 2,
    SystemExecute    = 1 << 3,
    ScreenCapture    = 1 << 4,
    MemoryRead       = 1 << 5,
    MemoryWrite      = 1 << 6,
    DeterministicUtility = 1 << 7,
    Meta             = 1 << 8
}

/// <summary>
/// Deterministic mapping from <see cref="AgentState"/> to the set of
/// <see cref="ToolFamily"/> values that the primary model is allowed to use.
/// This is the hard policy gate — the Footman cannot override it.
/// </summary>
public static class ToolFamilyPolicy
{
    /// <summary>
    /// Returns the allowed tool families for the given agent state.
    /// </summary>
    public static ToolFamily AllowedFamilies(AgentState state) => state switch
    {
        AgentState.Chat =>
            ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.SearchFact =>
            ToolFamily.WebSearch | ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.SearchNews =>
            ToolFamily.WebSearch | ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.SearchDeepDive =>
            ToolFamily.WebSearch | ToolFamily.BrowserNavigate | ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.ScreenObserve =>
            ToolFamily.ScreenCapture | ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.FileTask =>
            ToolFamily.FileSystem | ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.SystemTask =>
            ToolFamily.SystemExecute | ToolFamily.FileSystem | ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.MemoryWrite =>
            ToolFamily.MemoryWrite | ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.MemoryRead =>
            ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.BrowseOnce =>
            ToolFamily.BrowserNavigate | ToolFamily.WebSearch | ToolFamily.MemoryRead | ToolFamily.Meta,

        AgentState.UtilityDeterministic =>
            ToolFamily.DeterministicUtility | ToolFamily.Meta,

        AgentState.Fallback =>
            ToolFamily.MemoryRead | ToolFamily.Meta,

        _ =>
            ToolFamily.MemoryRead | ToolFamily.Meta
    };

    /// <summary>
    /// Expands a <see cref="ToolFamily"/> bitmask into the corresponding
    /// set of <see cref="ToolCapability"/> values for the existing policy gate.
    /// </summary>
    public static IReadOnlyList<ToolCapability> ToCapabilities(ToolFamily families)
    {
        var caps = new HashSet<ToolCapability>();

        if (families.HasFlag(ToolFamily.WebSearch))
            caps.Add(ToolCapability.WebSearch);
        if (families.HasFlag(ToolFamily.BrowserNavigate))
            caps.Add(ToolCapability.BrowserNavigate);
        if (families.HasFlag(ToolFamily.FileSystem))
        {
            caps.Add(ToolCapability.FileRead);
            caps.Add(ToolCapability.FileWrite);
        }
        if (families.HasFlag(ToolFamily.SystemExecute))
            caps.Add(ToolCapability.SystemExecute);
        if (families.HasFlag(ToolFamily.ScreenCapture))
            caps.Add(ToolCapability.ScreenCapture);
        if (families.HasFlag(ToolFamily.MemoryRead))
            caps.Add(ToolCapability.MemoryRead);
        if (families.HasFlag(ToolFamily.MemoryWrite))
            caps.Add(ToolCapability.MemoryWrite);
        if (families.HasFlag(ToolFamily.DeterministicUtility))
            caps.Add(ToolCapability.DeterministicUtility);
        if (families.HasFlag(ToolFamily.Meta))
            caps.Add(ToolCapability.Meta);

        return caps.ToList();
    }
}
