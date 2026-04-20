using Photino.NET;
using Thaddeus.SharedTypes;

namespace Thaddeus.Shell.Windows;

/// <summary>
/// Owns the main workspace Photino window (1200×800, resizable). The window simply
/// loads the runtime URL; all product UI lives in the React workspace.
/// </summary>
public sealed class WorkspaceWindow
{
    private readonly ILogger<WorkspaceWindow> _logger;
    private PhotinoWindow? _window;

    /// <summary>Initialises the window controller.</summary>
    public WorkspaceWindow(ILogger<WorkspaceWindow> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Shows the workspace window pointing at the supplied URL and blocks until the
    /// user closes it. Must be called on the application's main thread.
    ///
    /// <paramref name="onReady"/> fires after the window is constructed but before
    /// the message loop blocks. Phase 2.4 callers use it to spawn the compact
    /// panel as a child window: child windows must be created on the same thread
    /// that owns the message loop, so a Program.Main hook is the natural place
    /// to do that work.
    /// </summary>
    public void ShowBlocking(string url, string version, Action<PhotinoWindow>? onReady = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        _logger.LogInformation("workspace.window.opening url={Url}", url);

        _window = new PhotinoWindow()
            .SetTitle($"Sir Thaddeus — {version}")
            .SetSize(1200, 800)
            .Center()
            .SetResizable(true)
            .SetUseOsDefaultLocation(false)
            .SetUseOsDefaultSize(false)
            .Load(url);

        if (onReady is not null)
        {
            _window.RegisterWindowCreatingHandler((_, _) =>
            {
                try { onReady(_window!); }
                catch (Exception ex) { _logger.LogWarning(ex, "workspace.window.on_ready_failed"); }
            });
        }

        _window.WaitForClose();
        _logger.LogInformation("workspace.window.closed");
    }
}
