using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;

namespace SirThaddeus.DesktopRuntime;

public partial class App
{
    private string GetRuntimeSafetySummary()
    {
        if (_runtimeControls.SafeModeEnabled)
        {
            var reason = string.IsNullOrWhiteSpace(_runtimeSafeModeReason)
                ? "safe mode active"
                : _runtimeSafeModeReason;
            return $"SAFE MODE ({reason})";
        }

        if (_runtimeControls.PanicModeEnabled)
            return "PANIC MODE ON";

        return "normal";
    }

    private void TogglePanicModeFromTray()
    {
        if (_settings is null)
            return;

        var enable = !_settings.RuntimeSafety.PanicMode;
        var updated = _settings with
        {
            RuntimeSafety = _settings.RuntimeSafety with
            {
                PanicMode = enable
            }
        };

        SettingsManager.Save(updated);
        _settings = updated;
        _runtimeControls = RuntimeControlState.FromSettings(updated);
        _runtimeSafeModeReason = updated.RuntimeSafety.SafeModeReason;

        _permissionGate?.UpdateSettings(updated);
        if (_orchestrator is not null)
        {
            _orchestrator.PanicModeEnabled = _runtimeControls.PanicModeEnabled;
            _orchestrator.SafeModeEnabled = _runtimeControls.SafeModeEnabled;
        }

        _commandPaletteViewModel?.UpdateRuntimeSafety(
            _runtimeControls.PanicModeEnabled,
            _runtimeControls.SafeModeEnabled,
            _runtimeSafeModeReason);

        _auditLogger?.Append(new AuditEvent
        {
            Actor = "user",
            Action = "PANIC_MODE_TOGGLED",
            Result = enable ? "enabled" : "disabled",
            Details = new Dictionary<string, object>
            {
                ["source"] = "tray_menu"
            }
        });
    }

    private async void ExportDiagnosticsBundleFromTray()
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                confirm = true,
                reason = "tray_menu_export"
            });

            if (_mcpClient is not null)
            {
                var result = await _mcpClient.CallToolAsync("audit.export_bundle", json);
                if (TryExtractBundlePath(result, out var bundlePath))
                {
                    MessageBox.Show(
                        $"Diagnostics bundle exported:\n{bundlePath}",
                        "Diagnostics exported",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }
        }
        catch
        {
            // Fall through to local export.
        }

        try
        {
            var bundlePath = BuildLocalDiagnosticsBundle();
            MessageBox.Show(
                $"Diagnostics bundle exported:\n{bundlePath}",
                "Diagnostics exported",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _auditLogger?.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "DIAGNOSTICS_EXPORT_FAILED",
                Result = "error",
                Details = new Dictionary<string, object>
                {
                    ["message"] = ex.Message
                }
            });
        }
    }

    private static bool TryExtractBundlePath(string payload, out string bundlePath)
    {
        bundlePath = "";
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("bundle_zip", out var zipEl) &&
                zipEl.ValueKind == JsonValueKind.String)
            {
                bundlePath = zipEl.GetString() ?? "";
                return !string.IsNullOrWhiteSpace(bundlePath);
            }
        }
        catch
        {
            // Ignore malformed payload and fallback to local export.
        }

        return false;
    }

    private static string BuildLocalDiagnosticsBundle()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SirThaddeus");
        var diagnosticsDir = Path.Combine(appDataDir, "diagnostics");
        Directory.CreateDirectory(diagnosticsDir);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var bundleDir = Path.Combine(diagnosticsDir, $"bundle-{stamp}");
        Directory.CreateDirectory(bundleDir);

        CopyRedacted(Path.Combine(appDataDir, "settings.json"), Path.Combine(bundleDir, "settings-redacted.json"));
        CopyRedacted(Path.Combine(appDataDir, "audit.jsonl"), Path.Combine(bundleDir, "audit-redacted.jsonl"));
        CopyRedacted(Path.Combine(appDataDir, "chat-history.json"), Path.Combine(bundleDir, "chat-history-redacted.json"));
        CopyRedacted(Path.Combine(appDataDir, "briefing-history.json"), Path.Combine(bundleDir, "briefing-history-redacted.json"));

        var zipPath = bundleDir + ".zip";
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        ZipFile.CreateFromDirectory(bundleDir, zipPath);
        return zipPath;
    }

    private static void CopyRedacted(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
            return;

        var text = File.ReadAllText(sourcePath);
        text = Regex.Replace(
            text,
            "(?i)(api[_-]?key|token|password|secret)\"?\\s*[:=]\\s*\"?[^\",\\r\\n]+\"?",
            "$1:\"[REDACTED]\"");
        text = Regex.Replace(
            text,
            "(?i)bearer\\s+[a-z0-9\\-\\._~\\+\\/]+=*",
            "bearer [REDACTED]");
        File.WriteAllText(destinationPath, text);
    }
}
