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
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleOverlayItem;
    private readonly Func<bool> _isOverlayVisible;
    private readonly Action _toggleOverlay;
    private readonly Action _showCommandPalette;
    private readonly Action _stopAll;
    private readonly Action _exit;
    private bool _disposed;

    public TrayIconService(
        IAuditLogger auditLogger,
        Func<bool> isOverlayVisible,
        Action toggleOverlay,
        Action showCommandPalette,
        Action stopAll,
        Action exit)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _dispatcher = WpfApplication.Current?.Dispatcher
            ?? throw new InvalidOperationException("WPF dispatcher is not available (tray icon requires UI thread).");

        _isOverlayVisible = isOverlayVisible ?? throw new ArgumentNullException(nameof(isOverlayVisible));
        _toggleOverlay = toggleOverlay ?? throw new ArgumentNullException(nameof(toggleOverlay));
        _showCommandPalette = showCommandPalette ?? throw new ArgumentNullException(nameof(showCommandPalette));
        _stopAll = stopAll ?? throw new ArgumentNullException(nameof(stopAll));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        _toggleOverlayItem = new ToolStripMenuItem("Show Overlay");
        _toggleOverlayItem.Click += (_, _) => InvokeOnUiThread(_toggleOverlay);

        var openPaletteItem = new ToolStripMenuItem("Command Palette (Ctrl+Space)");
        openPaletteItem.Click += (_, _) => InvokeOnUiThread(_showCommandPalette);

        var stopAllItem = new ToolStripMenuItem("STOP ALL");
        stopAllItem.Click += (_, _) => InvokeOnUiThread(_stopAll);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => InvokeOnUiThread(_exit);

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => UpdateMenuText();
        menu.Items.Add(_toggleOverlayItem);
        menu.Items.Add(openPaletteItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(stopAllItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            // 63-char max; keep short.
            Text = "Assistant Runtime",
            // Placeholder icon until a real one is supplied.
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                InvokeOnUiThread(_toggleOverlay);
            }
        };

        _auditLogger.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "TRAY_ICON_READY",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["icon"] = "SystemIcons.Application",
                ["tooltip"] = _notifyIcon.Text
            }
        });
    }

    private void UpdateMenuText()
    {
        _toggleOverlayItem.Text = _isOverlayVisible() ? "Hide Overlay" : "Show Overlay";
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
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
            // Best-effort cleanup; tray icon disposal shouldn't crash shutdown.
        }
    }
}

