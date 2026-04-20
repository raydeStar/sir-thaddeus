namespace Thaddeus.Shell.Platform;

/// <summary>
/// Platform abstraction over a system tray / menu bar / status item. Phase 1 ships
/// only the stub implementation; Phase 7 layers on per-platform behaviour. The
/// shell must work without a tray (Linux fallback per spec §16).
/// </summary>
public interface ITrayAdapter : IAsyncDisposable
{
    /// <summary>True if the current OS environment supports a tray.</summary>
    bool IsSupported { get; }

    /// <summary>Initialises the tray with the supplied menu.</summary>
    Task InitializeAsync(TrayMenu menu, CancellationToken ct);
}

/// <summary>Declarative tray-menu structure populated by the shell at startup.</summary>
/// <param name="Items">Top-level menu items in display order.</param>
public sealed record TrayMenu(IReadOnlyList<TrayMenuItem> Items);

/// <summary>A clickable tray-menu entry.</summary>
/// <param name="Id">Stable id, used in handlers and tests.</param>
/// <param name="Label">Display label.</param>
/// <param name="Invoke">Handler.</param>
public sealed record TrayMenuItem(string Id, string Label, Func<Task> Invoke);
