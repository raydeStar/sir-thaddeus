using Avalonia.Interactivity;
using SirThaddeus.AuditLog;
using SirThaddeus.Contracts;
using System.Diagnostics;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private async void RefreshSearchStatusButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshSearchStatusAsync();
    }

    private async Task RefreshSearchStatusAsync()
    {
        if (_runtimeApiClient is null)
        {
            _backendSettings.ResetSearchHealthState(
                "Disconnected",
                "Connect the runtime to inspect live web-search and MCP health.");
            return;
        }

        try
        {
            var snapshot = await _runtimeApiClient.GetSearchStatusAsync(CancellationToken.None);
            _backendSettings.ApplySearchStatus(snapshot);
        }
        catch (Exception ex)
        {
            _backendSettings.ResetSearchHealthState(
                "Unavailable",
                "Search status refresh failed: " + ex.Message);
        }
    }

    private async void RefreshAuditButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshAuditAsync();
    }

    private async Task RefreshAuditAsync()
    {
        if (_runtimeApiClient is null)
        {
            return;
        }

        try
        {
            var entries = await _runtimeApiClient.GetAuditAsync(CancellationToken.None);
            AuditList.ItemsSource = entries.Select(ToAuditLine).ToArray();
        }
        catch (Exception ex)
        {
            AuditList.ItemsSource = new[] { "Audit load failed: " + ex.Message };
        }
    }

    private static string ToAuditLine(AuditEntryDto dto)
    {
        return $"{dto.TimestampUtc:O} [{dto.Category}] {dto.Message}";
    }

    private void OpenAuditLogFile_Click(object? sender, RoutedEventArgs e)
    {
        var path = JsonLineAuditLogger.GetDefaultPath();
        if (!System.IO.File.Exists(path))
        {
            AppendTranscript("[system] Audit log file not found: " + path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Failed to open audit log: " + ex.Message);
        }
    }

    private void OpenAuditLogFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = JsonLineAuditLogger.GetDefaultPath();
        var folder = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder))
        {
            AppendTranscript("[system] Audit log folder not found.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Failed to open audit folder: " + ex.Message);
        }
    }
}