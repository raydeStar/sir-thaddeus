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
    private readonly ToolStripMenuItem _reasoningGuardrailsItem;
    private readonly ToolStripMenuItem _runtimeSafetyItem;
    private readonly ToolStripMenuItem _panicModeItem;
    private readonly Func<bool> _isOverlayVisible;
    private readonly Func<string> _getReasoningGuardrails;
    private readonly Func<string> _getRuntimeSafetySummary;
    private readonly Action _toggleOverlay;
    private readonly Action _cycleReasoningGuardrails;
    private readonly Action _togglePanicMode;
    private readonly Action _exportDiagnostics;
    private readonly Action _showCommandPalette;
    private readonly Action _stopAll;
    private readonly Action _exit;
    private bool _disposed;

    public TrayIconService(
        IAuditLogger auditLogger,
        Func<bool> isOverlayVisible,
        Func<string> getReasoningGuardrails,
        Func<string> getRuntimeSafetySummary,
        Action toggleOverlay,
        Action cycleReasoningGuardrails,
        Action togglePanicMode,
        Action exportDiagnostics,
        Action showCommandPalette,
        Action stopAll,
        Action exit)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _dispatcher = WpfApplication.Current?.Dispatcher
            ?? throw new InvalidOperationException("WPF dispatcher is not available (tray icon requires UI thread).");

        _isOverlayVisible = isOverlayVisible ?? throw new ArgumentNullException(nameof(isOverlayVisible));
        _getReasoningGuardrails = getReasoningGuardrails ?? throw new ArgumentNullException(nameof(getReasoningGuardrails));
        _getRuntimeSafetySummary = getRuntimeSafetySummary ?? throw new ArgumentNullException(nameof(getRuntimeSafetySummary));
        _toggleOverlay = toggleOverlay ?? throw new ArgumentNullException(nameof(toggleOverlay));
        _cycleReasoningGuardrails = cycleReasoningGuardrails ?? throw new ArgumentNullException(nameof(cycleReasoningGuardrails));
        _togglePanicMode = togglePanicMode ?? throw new ArgumentNullException(nameof(togglePanicMode));
        _exportDiagnostics = exportDiagnostics ?? throw new ArgumentNullException(nameof(exportDiagnostics));
        _showCommandPalette = showCommandPalette ?? throw new ArgumentNullException(nameof(showCommandPalette));
        _stopAll = stopAll ?? throw new ArgumentNullException(nameof(stopAll));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        _toggleOverlayItem = new ToolStripMenuItem("Show Overlay");
        _toggleOverlayItem.Click += (_, _) => InvokeOnUiThread(_toggleOverlay);

        var openPaletteItem = new ToolStripMenuItem("Command Palette (Ctrl+Space)");
        openPaletteItem.Click += (_, _) => InvokeOnUiThread(_showCommandPalette);

        _reasoningGuardrailsItem = new ToolStripMenuItem("First Principles: Off");
        _reasoningGuardrailsItem.Click += (_, _) => InvokeOnUiThread(_cycleReasoningGuardrails);

        _runtimeSafetyItem = new ToolStripMenuItem("Runtime: normal")
        {
            Enabled = false
        };

        _panicModeItem = new ToolStripMenuItem("Enable Panic Mode");
        _panicModeItem.Click += (_, _) => InvokeOnUiThread(_togglePanicMode);

        var exportDiagnosticsItem = new ToolStripMenuItem("Export Diagnostics Bundle");
        exportDiagnosticsItem.Click += (_, _) => InvokeOnUiThread(_exportDiagnostics);

        var stopAllItem = new ToolStripMenuItem("STOP ALL");
        stopAllItem.Click += (_, _) => InvokeOnUiThread(_stopAll);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => InvokeOnUiThread(_exit);

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => UpdateMenuText();
        menu.Items.Add(_toggleOverlayItem);
        menu.Items.Add(openPaletteItem);
        menu.Items.Add(_reasoningGuardrailsItem);
        menu.Items.Add(_runtimeSafetyItem);
        menu.Items.Add(_panicModeItem);
        menu.Items.Add(exportDiagnosticsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(stopAllItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            // 63-char max; keep short.
            Text = "Sir Thaddeus",
            Icon = BrandIcon.TrayIcon,
            ContextMenuStrip = menu
        };

        UpdateMenuText();

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
                ["icon"] = "BrandIcon.TrayIcon",
                ["tooltip"] = _notifyIcon.Text
            }
        });
    }

    private void UpdateMenuText()
    {
        _toggleOverlayItem.Text = _isOverlayVisible() ? "Hide Overlay" : "Show Overlay";
        var mode = NormalizeMode(_getReasoningGuardrails());
        _reasoningGuardrailsItem.Text = mode switch
        {
            "always" => "First Principles: Always",
            "auto" => "First Principles: Auto",
            _ => "First Principles: Off"
        };

        var safetySummary = _getRuntimeSafetySummary();
        _runtimeSafetyItem.Text = $"Runtime: {safetySummary}";
        _panicModeItem.Text = safetySummary.Contains("PANIC MODE", StringComparison.OrdinalIgnoreCase)
            ? "Disable Panic Mode"
            : "Enable Panic Mode";
        _notifyIcon.Text = BuildTooltip(safetySummary);
    }

    private static string NormalizeMode(string? mode)
    {
        var normalized = (mode ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "always" => "always",
            "auto" => "auto",
            _ => "off"
        };
    }

    private static string BuildTooltip(string runtimeSummary)
    {
        const string baseText = "Sir Thaddeus";
        if (string.IsNullOrWhiteSpace(runtimeSummary) ||
            runtimeSummary.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return baseText;
        }

        var suffix = runtimeSummary.Length > 40
            ? runtimeSummary[..40] + "…"
            : runtimeSummary;
        var tooltip = $"{baseText} - {suffix}";
        return tooltip.Length > 63 ? tooltip[..63] : tooltip;
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

