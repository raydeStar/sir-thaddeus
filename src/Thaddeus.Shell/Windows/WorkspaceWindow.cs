using System.Runtime.InteropServices;
using Photino.NET;
using Thaddeus.SharedTypes;

namespace Thaddeus.Shell.Windows;

/// <summary>
/// Owns the main workspace Photino window (1200×800, resizable). The window simply
/// loads the runtime URL; all product UI lives in the React workspace.
/// </summary>
public sealed class WorkspaceWindow : IWorkspaceWindowSurface
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    private readonly ILogger<WorkspaceWindow> _logger;
    private PhotinoWindow? _window;

    /// <summary>Initialises the window controller.</summary>
    public WorkspaceWindow(ILogger<WorkspaceWindow> logger)
    {
        _logger = logger;
    }

    public bool IsVisible { get; private set; }

    public event WorkspaceWindowClosingHandler? ClosingRequested;

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
            .SetTitle($"Sir Thaddeus — Ready for adventure · v{version}")
            .SetSize(1200, 800)
            .Center()
            .SetResizable(true)
            .SetUseOsDefaultLocation(false)
            .SetUseOsDefaultSize(false)
            .Load(url);
        _window.WindowClosing += OnClosing;
        IsVisible = true;

        if (onReady is not null)
        {
            _window.RegisterWindowCreatingHandler((_, _) =>
            {
                try { onReady(_window!); }
                catch (Exception ex) { _logger.LogWarning(ex, "workspace.window.on_ready_failed"); }
            });
        }

        _window.WaitForClose();
        _window = null;
        IsVisible = false;
        _logger.LogInformation("workspace.window.closed");
    }

    public void Show()
    {
        if (_window is null)
        {
            return;
        }

        if (OperatingSystem.IsWindows() && _window.WindowHandle != IntPtr.Zero)
        {
            ShowWindow(_window.WindowHandle, SwShow);
            ShowWindow(_window.WindowHandle, SwRestore);
        }
        else
        {
            _window.SetMinimized(false);
        }
        IsVisible = true;
        _logger.LogInformation("workspace.window.shown");
    }

    public void Hide()
    {
        if (_window is null)
        {
            return;
        }

        if (OperatingSystem.IsWindows() && _window.WindowHandle != IntPtr.Zero)
        {
            ShowWindow(_window.WindowHandle, SwHide);
        }
        else
        {
            _window.SetMinimized(true);
        }
        IsVisible = false;
        _logger.LogInformation("workspace.window.hidden");
    }

    public void Close()
    {
        if (_window is null)
        {
            return;
        }

        _logger.LogInformation("workspace.window.close_requested");
        // Photino windows are tied to the message-pump thread that called
        // ShowBlocking. Calling Close() from another thread (e.g. the
        // supervisor's Process.Exited handler firing on the thread pool) is a
        // silent no-op on Windows because the WM_CLOSE message never reaches
        // the right thread. Marshal through Photino's Invoke() so the close
        // actually fires.
        try
        {
            _window.Invoke(() =>
            {
                try { _window?.Close(); }
                catch (Exception ex) { _logger.LogWarning(ex, "workspace.window.close_inner_failed"); }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace.window.close_invoke_failed");
            // Best-effort fallback in case Invoke is unavailable on this
            // platform; on the UI thread Close() works directly.
            try { _window.Close(); }
            catch (Exception inner) { _logger.LogWarning(inner, "workspace.window.close_direct_failed"); }
        }
    }

    private bool OnClosing(object? sender, EventArgs e)
    {
        if (ClosingRequested is null)
        {
            return false;
        }

        foreach (var handler in ClosingRequested.GetInvocationList().Cast<WorkspaceWindowClosingHandler>())
        {
            try
            {
                if (handler())
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "workspace.window.close_handler_failed");
            }
        }

        return false;
    }
}
