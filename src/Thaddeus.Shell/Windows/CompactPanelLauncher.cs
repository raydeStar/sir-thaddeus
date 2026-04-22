namespace Thaddeus.Shell.Windows;

/// <summary>
/// State machine that owns the compact-panel lifecycle for the shell. The
/// launcher gates open/show/hide transitions on the underlying
/// <see cref="ICompactWindowSurface"/> and ensures repeated triggers (e.g.
/// double-tapping the global shortcut) collapse to the right behaviour:
///
/// <list type="bullet">
///   <item><description><see cref="Show"/>/<see cref="Toggle"/> opens the window the first time and un-minimises it on subsequent calls.</description></item>
///   <item><description><see cref="Hide"/> minimises an already-open window. It is a no-op when the window is closed.</description></item>
///   <item><description><see cref="Close"/> tears the window down so the next <see cref="Show"/> rebuilds it (used at shutdown).</description></item>
/// </list>
///
/// The launcher does <b>not</b> own the runtime URL — callers supply it on
/// each <see cref="Show"/> so different sessions can repoint at the right
/// runtime endpoint.
/// </summary>
public sealed class CompactPanelLauncher
{
    private readonly ICompactWindowSurface _surface;
    private readonly ILogger<CompactPanelLauncher> _logger;
    private bool _visible;

    /// <summary>Initialises the launcher with the window surface to drive.</summary>
    public CompactPanelLauncher(ICompactWindowSurface surface, ILogger<CompactPanelLauncher> logger)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _logger = logger;
    }

    /// <summary>True when the launcher believes the panel is currently visible.</summary>
    public bool IsVisible => _visible && _surface.IsOpen;

    /// <summary>Open or restore the compact panel pointed at <paramref name="url"/>.</summary>
    public void Show(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (!_surface.IsOpen)
        {
            _logger.LogInformation("compact.launcher.opening");
            _surface.Open(url);
        }
        else
        {
            _logger.LogInformation("compact.launcher.restoring");
            _surface.Show();
        }
        _visible = true;
    }

    /// <summary>Minimise the compact panel out of the way. No-op when not open.</summary>
    public void Hide()
    {
        if (!_surface.IsOpen)
        {
            _visible = false;
            return;
        }
        _logger.LogInformation("compact.launcher.hiding");
        _surface.Hide();
        _visible = false;
    }

    /// <summary>Toggle the compact panel between visible and hidden states.</summary>
    public void Toggle(string url)
    {
        if (IsVisible) Hide();
        else Show(url);
    }

    /// <summary>Tear the compact window down completely. Used at shutdown.</summary>
    public void Close()
    {
        if (!_surface.IsOpen)
        {
            _visible = false;
            return;
        }
        _logger.LogInformation("compact.launcher.closing");
        _surface.Close();
        _visible = false;
    }
}
