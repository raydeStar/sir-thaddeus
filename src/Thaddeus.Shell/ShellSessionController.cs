using Thaddeus.Shell.Platform;
using Thaddeus.Shell.Windows;

namespace Thaddeus.Shell;

/// <summary>
/// Coordinates tray, workspace visibility, and shell shutdown behavior.
/// The controller stays free of native APIs so shell tests can cover the behavior.
/// </summary>
public sealed class ShellSessionController
{
    internal const string OpenWorkspaceMenuId = "open-workspace";
    internal const string StopAllMenuId = "stop-all";
    internal const string ExitMenuId = "exit";

    private readonly IWorkspaceWindowSurface _workspace;
    private readonly ITrayAdapter _tray;
    private readonly Func<Task> _stopAllAsync;
    private readonly Action? _closeCompactWindow;
    private readonly ILogger<ShellSessionController> _logger;
    private bool _trayInitialized;
    private bool _exitRequested;

    public ShellSessionController(
        IWorkspaceWindowSurface workspace,
        ITrayAdapter tray,
        Func<Task> stopAllAsync,
        ILogger<ShellSessionController> logger,
        Action? closeCompactWindow = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _stopAllAsync = stopAllAsync ?? throw new ArgumentNullException(nameof(stopAllAsync));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _closeCompactWindow = closeCompactWindow;
    }

    /// <summary>
    /// Initializes tray integration when the platform supports it.
    /// The shell still works without a tray; in that case the main window closes normally.
    /// </summary>
    public async Task InitializeAsync(bool startMinimized, CancellationToken ct)
    {
        if (!_tray.IsSupported)
        {
            _logger.LogInformation("shell.tray.unsupported");
            return;
        }

        try
        {
            await _tray.InitializeAsync(BuildTrayMenu(), ct).ConfigureAwait(false);
            _trayInitialized = true;
            _logger.LogInformation("shell.tray.ready");

            if (startMinimized)
            {
                _logger.LogInformation("shell.workspace.start_minimized");
                _workspace.Hide();
            }
        }
        catch (Exception ex)
        {
            _trayInitialized = false;
            _logger.LogWarning(ex, "shell.tray.init_failed");
        }
    }

    /// <summary>
    /// Intercepts the native close request when a live tray exists and converts it
    /// into a hide-to-tray action. Returning <c>false</c> allows the process to exit.
    /// </summary>
    public bool HandleWorkspaceClosing()
    {
        if (_exitRequested || !_trayInitialized)
        {
            return false;
        }

        _logger.LogInformation("shell.workspace.close_to_tray");
        _workspace.Hide();
        return true;
    }

    public Task ExitAsync()
    {
        if (_exitRequested)
        {
            return Task.CompletedTask;
        }

        _exitRequested = true;
        try
        {
            _closeCompactWindow?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "shell.compact.close_failed");
        }

        _logger.LogInformation("shell.exit.requested");
        _workspace.Close();
        return Task.CompletedTask;
    }

    private TrayMenu BuildTrayMenu() => new(
        [
            new TrayMenuItem(OpenWorkspaceMenuId, "At your service, sir", OpenWorkspaceAsync),
            new TrayMenuItem(StopAllMenuId, "Stand down", StopAllAsync),
            new TrayMenuItem(ExitMenuId, "Dismiss", ExitAsync),
        ]);

    private Task OpenWorkspaceAsync()
    {
        _logger.LogInformation("shell.workspace.restore_from_tray");
        _workspace.Show();
        return Task.CompletedTask;
    }

    private async Task StopAllAsync()
    {
        try
        {
            _logger.LogInformation("shell.stop_all.requested_from_tray");
            await _stopAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "shell.stop_all.from_tray_failed");
        }
    }
}