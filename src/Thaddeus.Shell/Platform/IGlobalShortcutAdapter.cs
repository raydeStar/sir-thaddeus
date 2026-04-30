namespace Thaddeus.Shell.Platform;

/// <summary>
/// Platform abstraction over global keyboard shortcut registration. Phase 1 ships
/// the stub; real platform-specific implementations land in Phase 2.
/// </summary>
public interface IGlobalShortcutAdapter : IDisposable
{
    /// <summary>True if the current OS environment supports global shortcut registration.</summary>
    bool IsSupported { get; }

    /// <summary>Attempts to register a chord.</summary>
    Task<bool> RegisterAsync(string id, KeyChord chord, CancellationToken ct);

    /// <summary>Cancels a previously-registered chord.</summary>
    Task UnregisterAsync(string id);

    /// <summary>Raised when a registered chord is pressed.</summary>
    event EventHandler<string>? Triggered;

    /// <summary>Raised after a triggered chord is released.</summary>
    event EventHandler<string>? Released;
}

/// <summary>
/// Logical key chord (e.g. <c>Ctrl+Shift+Space</c>). Modifier flags are deliberately
/// platform-agnostic and translated by adapters at registration time.
/// </summary>
/// <param name="Key">The non-modifier key, e.g. "Space", "F8".</param>
/// <param name="Modifiers">Modifier set.</param>
public sealed record KeyChord(string Key, KeyModifiers Modifiers);

/// <summary>Bitwise modifier set.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifiers.</summary>
    None = 0,

    /// <summary>Control / Cmd on macOS.</summary>
    Control = 1 << 0,

    /// <summary>Shift.</summary>
    Shift = 1 << 1,

    /// <summary>Alt / Option.</summary>
    Alt = 1 << 2,

    /// <summary>Super / Windows / Cmd (where distinct from Control).</summary>
    Super = 1 << 3,
}
