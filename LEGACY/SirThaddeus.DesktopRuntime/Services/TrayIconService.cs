using System.Drawing;
using System.Windows.Forms;
using SirThaddeus.AuditLog;
using WpfApplication = System.Windows.Application;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Minimal system tray integration for the desktop runtime.
/// Uses a WinForms <see cref="NotifyIcon"/> because WPF does not ship a built-in tray icon.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly IAuditLogger _auditLogger;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly RuntimeStateStore _stateStore;
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _openSirThaddeus;
    private readonly Action _pauseServiceJobs;
    private readonly Action _stopAll;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private bool _disposed;

    public TrayIconService(
        IAuditLogger auditLogger,
        RuntimeStateStore stateStore,
        Action openSirThaddeus,
        Action pauseServiceJobs,
        Action stopAll,
        Action openSettings,
        Action exit)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _dispatcher = WpfApplication.Current?.Dispatcher
            ?? throw new InvalidOperationException("WPF dispatcher is not available (tray icon requires UI thread).");

        _openSirThaddeus = openSirThaddeus ?? throw new ArgumentNullException(nameof(openSirThaddeus));
        _pauseServiceJobs = pauseServiceJobs ?? throw new ArgumentNullException(nameof(pauseServiceJobs));
        _stopAll = stopAll ?? throw new ArgumentNullException(nameof(stopAll));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        var openItem = new ToolStripMenuItem("Open Sir Thaddeus");
        openItem.Click += (_, _) => InvokeOnUiThread(_openSirThaddeus);

        var pauseServiceItem = new ToolStripMenuItem("Pause Service Jobs");
        pauseServiceItem.Click += (_, _) => InvokeOnUiThread(_pauseServiceJobs);
        pauseServiceItem.Enabled = false; // Stubbed for V0

        var stopAllItem = new ToolStripMenuItem("STOP ALL");
        stopAllItem.Click += (_, _) => InvokeOnUiThread(_stopAll);

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => InvokeOnUiThread(_openSettings);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => InvokeOnUiThread(_exit);

        var menu = new ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(pauseServiceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(stopAllItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "Sir Thaddeus",
            Icon = BrandIcon.TrayIcon,
            ContextMenuStrip = menu
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                InvokeOnUiThread(_openSirThaddeus);
            }
        };

        _stateStore.StateChanged += OnStateChanged;
        UpdateTrayState(_stateStore.CurrentState);

        _auditLogger.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "TRAY_ICON_READY",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["icon"] = "BrandIcon.TrayIcon",
                ["tooltip"] = _notifyIcon.Text
            }
        });
    }

    private void OnStateChanged(object? sender, RuntimeState state)
    {
        UpdateTrayState(state);
    }

    private void UpdateTrayState(RuntimeState state)
    {
        var label = state.ToDisplayLabel();
        var tooltip = $"Sir Thaddeus - {label}";
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;

        // In the future, this can swap _notifyIcon.Icon to reflect states (e.g. Red for Stopped, Blue for Active).
    }

    private void InvokeOnUiThread(Action action)
    {
        try
        {
            _dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            _auditLogger.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "TRAY_ICON_CALLBACK_ERROR",
                Result = "failed",
                Details = new Dictionary<string, object>
                {
                    ["error"] = ex.Message
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stateStore.StateChanged -= OnStateChanged;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}