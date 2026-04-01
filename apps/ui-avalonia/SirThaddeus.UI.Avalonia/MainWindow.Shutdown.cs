using Avalonia;
using Avalonia.Interactivity;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private async void StopAllButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_stopAllInProgress)
        {
            return;
        }

        _stopAllInProgress = true;
        StopAllButton.IsEnabled = false;
        try
        {
            AppendTranscript("[system] STOP ALL requested. Tearing down backend services and exiting.");

            if (_runtimeApiClient is not null && !string.IsNullOrWhiteSpace(_activeRunId))
            {
                try
                {
                    await _runtimeApiClient.CancelRunAsync(_activeRunId, CancellationToken.None);
                }
                catch
                {
                    // Continue with hard shutdown.
                }
            }

            _eventStreamCancellation?.Cancel();
            _eventStreamCancellation?.Dispose();
            _eventStreamCancellation = null;
            _activeRunId = null;

            _runtimeHttpClient?.Dispose();
            _runtimeHttpClient = null;
            _runtimeApiClient = null;
            _runtimeBaseUri = null;

            _runtimeLauncher.StopManagedRuntime();
            KillKnownBackendProcesses();
        }
        finally
        {
            if (Application.Current is App app)
            {
                app.RequestShutdown();
            }
            else
            {
                Close();
            }

            ScheduleHardExitFallback();
        }
    }

    private static void ScheduleHardExitFallback()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Environment.Exit(0);
        });
    }

    private static void KillKnownBackendProcesses()
    {
        var names = new[]
        {
            "SirThaddeus.HeadlessRuntime",
            "SirThaddeus.McpServer",
            "SirThaddeus.VoiceHost",
            "voice-backend"
        };

        foreach (var name in names)
        {
            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best effort kill only.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Ignore process enumeration failures.
            }
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_runtimeApiClient is null || string.IsNullOrWhiteSpace(_activeRunId))
        {
            AppendTranscript("[system] No active run to cancel.");
            return;
        }

        try
        {
            var activeRunId = _activeRunId;
            var accepted = await _runtimeApiClient.CancelRunAsync(activeRunId, CancellationToken.None);
            AppendTranscript(accepted
                ? $"[system] STOP accepted for {activeRunId}"
                : $"[system] STOP rejected for {activeRunId}");

            if (accepted)
            {
                _activeRunId = null;
            }

            UpdateComposerState();
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] Cancel failed: {ex.Message}");
            UpdateComposerState();
        }
    }
}