namespace SirThaddeus.Agent;

/// <summary>
/// Capability-level abstraction for tool access decisions.
/// Each tool must map to exactly one capability.
/// </summary>
public enum ToolCapability
{
    DeterministicUtility,
    WebSearch,
    BrowserNavigate,
    FileRead,
    FileWrite,
    SystemExecute,
    ScreenCapture,
    MemoryRead,
    MemoryWrite,
    Meta,
    TimeRead
}

