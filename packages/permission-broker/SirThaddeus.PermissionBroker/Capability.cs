namespace SirThaddeus.PermissionBroker;

/// <summary>
/// Capabilities that require explicit permission tokens.
/// Each capability represents a category of privileged operations.
/// </summary>
public enum Capability
{
    /// <summary>
    /// Read content from the screen (screenshots, OCR, window inspection).
    /// </summary>
    ScreenRead,

    /// <summary>
    /// Control browser navigation and interaction.
    /// </summary>
    BrowserControl,

    /// <summary>
    /// Access to microphone for voice input.
    /// </summary>
    Microphone,

    /// <summary>
    /// Execute system commands or scripts.
    /// </summary>
    SystemExecute,

    /// <summary>
    /// Read/write files on the local filesystem.
    /// </summary>
    FileAccess,

    /// <summary>
    /// Access web search and browser navigation tools.
    /// </summary>
    WebAccess,

    /// <summary>
    /// Read from the local memory store (retrieve, list facts).
    /// </summary>
    MemoryRead,

    /// <summary>
    /// Write to the local memory store (store, update, delete facts).
    /// </summary>
    MemoryWrite
}

/// <summary>
/// Extension methods for <see cref="Capability"/>.
/// </summary>
public static class CapabilityExtensions
{
    /// <summary>
    /// Gets a human-readable display name for the capability.
    /// </summary>
    public static string ToDisplayName(this Capability capability) => capability switch
    {
        Capability.ScreenRead     => "Screen Reading",
        Capability.BrowserControl => "Browser Control",
        Capability.Microphone     => "Microphone Access",
        Capability.SystemExecute  => "System Command Execution",
        Capability.FileAccess     => "File System Access",
        Capability.WebAccess      => "Web Access",
        Capability.MemoryRead     => "Memory Read",
        Capability.MemoryWrite    => "Memory Write",
        _                         => capability.ToString()
    };

    /// <summary>
    /// Gets a description of what this capability allows.
    /// </summary>
    public static string ToDescription(this Capability capability) => capability switch
    {
        Capability.ScreenRead     => "Read content visible on your screen",
        Capability.BrowserControl => "Navigate and interact with web pages",
        Capability.Microphone     => "Listen to audio from your microphone",
        Capability.SystemExecute  => "Run commands on your system",
        Capability.FileAccess     => "Read or write files on your computer",
        Capability.WebAccess      => "Search the web and navigate to pages",
        Capability.MemoryRead     => "Retrieve stored memories and facts",
        Capability.MemoryWrite    => "Store, update, or delete memories",
        _                         => "Perform privileged operations"
    };
}
