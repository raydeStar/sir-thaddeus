using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System.Net.Http;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private async void ConnectButton_Click(object? sender, RoutedEventArgs e)
    {
        await EnsureRuntimeConnectedAsync(
            allowStartRuntime: _uiSettings.AutoStartRuntime,
            appendTranscriptOnFailure: true);
    }

    private async void StartRuntimeButton_Click(object? sender, RoutedEventArgs e)
    {
        await EnsureRuntimeConnectedAsync(
            allowStartRuntime: true,
            appendTranscriptOnFailure: true,
            forceRuntimeLaunch: true);
    }

    private void StopRuntimeButton_Click(object? sender, RoutedEventArgs e)
    {
        var wasRunning = _runtimeLauncher.IsManagedRuntimeRunning;
        _runtimeLauncher.StopManagedRuntime();
        UpdateRuntimeLaunchStatusText();

        if (wasRunning)
        {
            _runtimeHttpClient?.Dispose();
            _runtimeHttpClient = null;
            _runtimeApiClient = null;
            _runtimeBaseUri = null;
            SetDisconnectedStatus("Disconnected");
            SettingsRuntimeText.Text = "Runtime: stopped";
            AppendTranscript("[system] Stopped managed runtime.");
        }
    }

    private async Task<bool> EnsureRuntimeConnectedAsync(
        bool allowStartRuntime,
        bool appendTranscriptOnFailure,
        bool forceRuntimeLaunch = false,
        int waitForReadyRetries = 0,
        int waitForReadyDelayMs = 300)
    {
        if (_isConnecting)
        {
            return _runtimeApiClient is not null;
        }

        if (!TryGetRuntimeUri(out var runtimeUri))
        {
            SetDisconnectedStatus("Invalid URL");
            if (appendTranscriptOnFailure)
            {
                AppendTranscript("[system] Runtime URL is invalid.");
            }

            return false;
        }

        _uiSettings = _uiSettings with { RuntimeUrl = runtimeUri.ToString().TrimEnd('/') };
        PersistUiSettings();

        try
        {
            _isConnecting = true;
            SetConnectingState(true);

            if (!forceRuntimeLaunch && await TryConnectAsync(runtimeUri))
            {
                return true;
            }

            if (allowStartRuntime)
            {
                var launch = await _runtimeLauncher.EnsureRunningAsync(runtimeUri, CancellationToken.None);
                UpdateRuntimeLaunchStatusText(launch.Message);

                if ((launch.Status == RuntimeLaunchStatus.Started || launch.Status == RuntimeLaunchStatus.AlreadyRunning) &&
                    await TryConnectWithRetryAsync(runtimeUri, retries: 25, delayMs: 300))
                {
                    return true;
                }
            }
            else if (waitForReadyRetries > 0)
            {
                // Caller asked us to wait for the runtime to come online (e.g. user
                // submitted a prompt while LM Studio is still loading). Poll instead
                // of failing fast.
                if (await TryConnectWithRetryAsync(runtimeUri, retries: waitForReadyRetries, delayMs: waitForReadyDelayMs))
                {
                    return true;
                }
            }

            SetDisconnectedStatus("Disconnected");
            if (appendTranscriptOnFailure)
            {
                AppendTranscript("[system] Unable to connect to runtime. Check URL or start local runtime.");
            }

            return false;
        }
        finally
        {
            _isConnecting = false;
            SetConnectingState(false);
            UpdateActionDrawerSummary();
        }
    }

    private async Task<bool> TryConnectWithRetryAsync(Uri runtimeUri, int retries, int delayMs)
    {
        for (var i = 0; i < retries; i++)
        {
            if (await TryConnectAsync(runtimeUri))
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private async Task<bool> TryConnectAsync(Uri runtimeUri)
    {
        try
        {
            using var probeClient = new HttpClient
            {
                BaseAddress = runtimeUri,
                Timeout = TimeSpan.FromSeconds(2)
            };

            var probeApi = new RuntimeApiClient(probeClient);
            var health = await probeApi.GetHealthAsync(CancellationToken.None);
            if (health is null)
            {
                return false;
            }

            _runtimeHttpClient?.Dispose();
            _runtimeHttpClient = new HttpClient
            {
                BaseAddress = runtimeUri,
                Timeout = TimeSpan.FromSeconds(30)
            };
            _runtimeApiClient = new RuntimeApiClient(_runtimeHttpClient);
            _runtimeBaseUri = runtimeUri;

            var isManaged = _runtimeLauncher.IsManagedRuntimeRunning;
            ConnectionStatusText.Text = "Connected";
            ConnectionStatusText.Foreground =
                (IBrush?)this.FindResource("GreenBrush")
                ?? Brushes.LightGreen;
            SettingsRuntimeText.Text = $"Runtime: {runtimeUri} ({health.Version})";
            RuntimeLaunchStateText.Text = isManaged
                ? "Managed runtime: running"
                : "Managed runtime: external";

            if (ReferenceEquals(SettingsTabControl.SelectedItem, SearchTabItem))
            {
                _ = RefreshSearchStatusAsync();
            }
            else
            {
                _backendSettings.ResetSearchHealthState(
                    "Connected",
                    "Runtime connected. Open Search or click Refresh Search Status to inspect live web-search and MCP health.");
            }

            UpdateActionDrawerSummary();
            UpdateComposerState();

            _ = RefreshProfilesAsync();

            if (_pendingUserPrompt is not null)
            {
                var pending = _pendingUserPrompt;
                _pendingUserPrompt = null;
                Dispatcher.UIThread.Post(async () =>
                {
                    await SubmitPromptAsync(pending, voiceInitiated: false);
                }, DispatcherPriority.Background);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetRuntimeUri(out Uri runtimeUri)
    {
        var baseUrl = RuntimeUrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            runtimeUri = default!;
            return false;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out runtimeUri!))
        {
            return runtimeUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                   runtimeUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        }

        if (Uri.TryCreate("http://" + baseUrl, UriKind.Absolute, out runtimeUri!))
        {
            RuntimeUrlBox.Text = runtimeUri.ToString().TrimEnd('/');
            return true;
        }

        return false;
    }

    private void SetConnectingState(bool connecting)
    {
        StartRuntimeButton.IsEnabled = !connecting;
        StopRuntimeButton.IsEnabled = !connecting;

        if (connecting)
        {
            ConnectionStatusText.Text = "Connecting...";
            ConnectionStatusText.Foreground = Brushes.White;
        }

        UpdateHeaderConnectionControls();
    }

    private void SetDisconnectedStatus(string status)
    {
        ConnectionStatusText.Text = status;
        ConnectionStatusText.Foreground =
            (IBrush?)this.FindResource("RedBrush")
            ?? Brushes.Salmon;

        if (_runtimeBaseUri is not null)
        {
            SettingsRuntimeText.Text = $"Runtime: {_runtimeBaseUri} (offline)";
        }
        else
        {
            SettingsRuntimeText.Text = "Runtime: not connected";
        }

        _backendSettings.ResetSearchHealthState(
            "Disconnected",
            "Connect the runtime to inspect live web-search and MCP health.");
        UpdateRuntimeLaunchStatusText();
        UpdateHeaderConnectionControls();
        UpdateActionDrawerSummary();
        UpdateComposerState();
    }

    private void UpdateRuntimeLaunchStatusText(string? overrideMessage = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideMessage))
        {
            RuntimeLaunchStateText.Text = overrideMessage;
            return;
        }

        RuntimeLaunchStateText.Text = _runtimeLauncher.IsManagedRuntimeRunning
            ? "Managed runtime: running"
            : "Managed runtime: not running";
    }
}