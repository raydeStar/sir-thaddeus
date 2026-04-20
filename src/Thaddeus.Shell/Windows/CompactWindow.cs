using Photino.NET;

namespace Thaddeus.Shell.Windows;

/// <summary>
/// Abstraction over the native compact-panel window so the
/// <see cref="CompactPanelLauncher"/> state machine can be unit-tested without
/// instantiating Photino. The shell ships <see cref="PhotinoCompactWindowSurface"/>
/// at runtime; tests substitute fakes.
/// </summary>
public interface ICompactWindowSurface
{
    /// <summary>True once <see cref="Open(string)"/> has been called and the window has not been closed.</summary>
    bool IsOpen { get; }

    /// <summary>Creates the compact window pointed at <paramref name="url"/>. Idempotent: a second call is a no-op while open.</summary>
    void Open(string url);

    /// <summary>Brings the compact window back into view (un-minimises). Throws if the surface has not been opened.</summary>
    void Show();

    /// <summary>Minimises the compact window so it is out of the way without tearing down the WebView. Throws if not opened.</summary>
    void Hide();

    /// <summary>Closes the underlying window. Subsequent <see cref="Open"/> calls re-create it.</summary>
    void Close();
}

/// <summary>
/// Production <see cref="ICompactWindowSurface"/> backed by a child
/// <see cref="PhotinoWindow"/>. The window is created chromeless, ~420×220, and
/// always-on-top so it stays handy during quick voice interactions (spec §11.4).
///
/// Photino requires native window creation to happen on the thread running the
/// main message loop, so callers must invoke <see cref="Open"/> from a parent
/// window callback (e.g. <c>WindowCreating</c> on the workspace window) — not
/// from Program.Main directly.
/// </summary>
public sealed class PhotinoCompactWindowSurface : ICompactWindowSurface
{
    private readonly PhotinoWindow _parent;
    private readonly ILogger<PhotinoCompactWindowSurface> _logger;
    private PhotinoWindow? _window;

    /// <summary>
    /// Initialises a new compact-window surface anchored to <paramref name="parent"/>.
    /// </summary>
    public PhotinoCompactWindowSurface(PhotinoWindow parent, ILogger<PhotinoCompactWindowSurface> logger)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsOpen => _window is not null;

    /// <inheritdoc />
    public void Open(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (_window is not null)
        {
            Show();
            return;
        }
        _logger.LogInformation("compact.window.opening url={Url}", url);
        _window = new PhotinoWindow(_parent)
            .SetTitle("Sir Thaddeus — Quick")
            .SetSize(420, 220)
            .Center()
            .SetUseOsDefaultLocation(false)
            .SetUseOsDefaultSize(false)
            .SetResizable(false)
            .SetTopMost(true)
            .Load(url);
        _window.WindowClosing += OnClosing;
        _window.WaitForClose();
    }

    /// <inheritdoc />
    public void Show()
    {
        var window = RequireOpen();
        window.Minimized = false;
    }

    /// <inheritdoc />
    public void Hide()
    {
        var window = RequireOpen();
        window.Minimized = true;
    }

    /// <inheritdoc />
    public void Close()
    {
        if (_window is null) return;
        _logger.LogInformation("compact.window.closing");
        try { _window.Close(); } catch (Exception ex) { _logger.LogWarning(ex, "compact.window.close_failed"); }
        _window = null;
    }

    private PhotinoWindow RequireOpen() =>
        _window ?? throw new InvalidOperationException("Compact window has not been opened yet.");

    private bool OnClosing(object? sender, EventArgs e)
    {
        _logger.LogInformation("compact.window.closed");
        _window = null;
        return false; // allow close
    }
}
