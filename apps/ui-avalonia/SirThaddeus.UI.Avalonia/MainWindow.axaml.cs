using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SirThaddeus.Contracts;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow : Window
{
    private RuntimeApiClient? _runtimeApiClient;
    private HttpClient? _runtimeHttpClient;
    private Uri? _runtimeBaseUri;
    private string? _activeRunId;
    private string? _pendingPermissionRequestId;
    private CancellationTokenSource? _eventStreamCancellation;
    private readonly StringBuilder _transcript = new();
    private readonly RuntimeHostLauncher _runtimeLauncher = new();
    private readonly UiClientSettingsStore _uiSettingsStore = new();
    private UiClientSettings _uiSettings;
    private bool _isConnecting;
    private bool _initialConnectAttempted;
    private bool _trayAvailable;
    private bool _stopAllInProgress;

    private readonly ObservableCollection<ChatSessionItem> _chatHistory = [];
    private ChatSessionItem _currentSession;

    private readonly ToggleButton[] _viewTabs;
    private readonly Control[] _viewPanels;

    public MainWindow()
    {
        InitializeComponent();

        _viewTabs = [ChatTabButton, PermTabButton, AuditTabButton, SettingsTabButton];
        _viewPanels = [ChatView, PermissionsView, AuditView, SettingsView];

        _uiSettings = _uiSettingsStore.Load();
        ApplyUiSettingsToControls();

        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Add(_currentSession);
        ChatHistoryList.ItemsSource = _chatHistory;
        ChatHistoryList.SelectedItem = _currentSession;
        UpdateConversationTitle();

        TranscriptBox.IsVisible = false;
        SetActiveView(ChatTabButton);

        Opened += OnOpened;
    }

    public void ConfigureTrayUi(bool trayAvailable, bool minimizeToTrayEnabled)
    {
        _trayAvailable = trayAvailable;

        MinimizeToTrayCheckBox.IsEnabled = trayAvailable;

        var desired = trayAvailable ? _uiSettings.MinimizeToTray : false;
        if (Application.Current is App app)
        {
            app.MinimizeToTrayEnabled = desired;
        }

        MinimizeToTrayCheckBox.IsChecked = desired;
        TraySupportText.Text = trayAvailable
            ? "Tray is available. You can keep Sir Thaddeus running in the system tray."
            : "Tray is not available on this platform. Closing exits the app.";

        PersistUiSettings();
    }

    protected override void OnClosed(EventArgs e)
    {
        Opened -= OnOpened;

        _eventStreamCancellation?.Cancel();
        _eventStreamCancellation?.Dispose();
        _eventStreamCancellation = null;

        _runtimeHttpClient?.Dispose();
        _runtimeHttpClient = null;
        _runtimeApiClient = null;

        _runtimeLauncher.Dispose();
        PersistUiSettings();

        base.OnClosed(e);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialConnectAttempted)
        {
            return;
        }

        _initialConnectAttempted = true;
        if (_uiSettings.AutoConnectOnLaunch)
        {
            await EnsureRuntimeConnectedAsync(
                allowStartRuntime: _uiSettings.AutoStartRuntime,
                appendTranscriptOnFailure: false);
        }

        UpdateRuntimeLaunchStatusText();
        UpdateActionDrawerSummary();
    }

    private void ApplyUiSettingsToControls()
    {
        RuntimeUrlBox.Text = _uiSettings.RuntimeUrl;
        SendOnEnterCheckBox.IsChecked = _uiSettings.SendOnEnter;
        AutoSwitchPermissionsCheckBox.IsChecked = _uiSettings.AutoSwitchToPermissions;
        AutoConnectCheckBox.IsChecked = _uiSettings.AutoConnectOnLaunch;
        AutoStartRuntimeCheckBox.IsChecked = _uiSettings.AutoStartRuntime;
    }

    private void PersistUiSettings()
    {
        _uiSettingsStore.Save(_uiSettings);
    }

    private void ViewTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked)
        {
            return;
        }

        SetActiveView(clicked);
    }

    private void SetActiveView(ToggleButton selected)
    {
        foreach (var tab in _viewTabs)
        {
            tab.IsChecked = ReferenceEquals(tab, selected);
        }

        for (var i = 0; i < _viewTabs.Length; i++)
        {
            _viewPanels[i].IsVisible = _viewTabs[i].IsChecked == true;
        }

        InputBar.IsVisible = ChatTabButton.IsChecked == true;

        if (AuditTabButton.IsChecked == true)
        {
            _ = RefreshAuditAsync();
        }
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.D1:
                    SetActiveView(ChatTabButton);
                    e.Handled = true;
                    return;
                case Key.D2:
                    SetActiveView(PermTabButton);
                    e.Handled = true;
                    return;
                case Key.D3:
                    SetActiveView(AuditTabButton);
                    e.Handled = true;
                    return;
                case Key.D4:
                    SetActiveView(SettingsTabButton);
                    e.Handled = true;
                    return;
            }
        }

        if (_uiSettings.SendOnEnter &&
            e.Key == Key.Enter &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
            PromptBox.IsFocused)
        {
            e.Handled = true;
            SendButton_Click(sender, e);
        }
    }

    private void ConversationButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleConversationDrawer(!ConversationDrawer.IsVisible);
    }

    private async void ConnectionStatusButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleActionDrawer(!ActionDrawer.IsVisible);
        if (ActionDrawer.IsVisible)
        {
            await RefreshActionDrawerAsync();
        }
    }

    private void ToggleConversationDrawer(bool show)
    {
        ConversationDrawer.IsVisible = show;
        if (show)
        {
            ActionDrawer.IsVisible = false;
        }
    }

    private void ToggleActionDrawer(bool show)
    {
        ActionDrawer.IsVisible = show;
        if (show)
        {
            ConversationDrawer.IsVisible = false;
        }
    }

    private void CloseConversationDrawerButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleConversationDrawer(false);
    }

    private void CloseActionDrawerButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleActionDrawer(false);
    }

    private void OpenActionsDrawerButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleActionDrawer(true);
        _ = RefreshActionDrawerAsync();
    }

    private void ChatHistoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is not ChatSessionItem session || ReferenceEquals(session, _currentSession))
        {
            return;
        }

        _currentSession = session;
        _transcript.Clear();
        _transcript.Append(session.TranscriptText);
        TranscriptBox.Text = _transcript.ToString();
        TranscriptBox.IsVisible = _transcript.Length > 0;
        EmptyHero.IsVisible = _transcript.Length == 0;

        UpdateConversationTitle();
        SetActiveView(ChatTabButton);
        ToggleConversationDrawer(false);
    }

    private void ClearHistoryButton_Click(object? sender, RoutedEventArgs e)
    {
        _chatHistory.Clear();
        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Add(_currentSession);
        ChatHistoryList.SelectedItem = _currentSession;

        _transcript.Clear();
        TranscriptBox.Text = string.Empty;
        TranscriptBox.IsVisible = false;
        EmptyHero.IsVisible = true;

        UpdateConversationTitle();
    }

    private void SendOnEnterCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        _uiSettings = _uiSettings with { SendOnEnter = SendOnEnterCheckBox.IsChecked == true };
        PersistUiSettings();
    }

    private void AutoSwitchPermissionsCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        _uiSettings = _uiSettings with { AutoSwitchToPermissions = AutoSwitchPermissionsCheckBox.IsChecked == true };
        PersistUiSettings();
    }

    private void AutoConnectCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        _uiSettings = _uiSettings with { AutoConnectOnLaunch = AutoConnectCheckBox.IsChecked == true };
        PersistUiSettings();
    }

    private void AutoStartRuntimeCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        _uiSettings = _uiSettings with { AutoStartRuntime = AutoStartRuntimeCheckBox.IsChecked == true };
        PersistUiSettings();
    }

    private void MinimizeToTrayCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (!_trayAvailable)
        {
            MinimizeToTrayCheckBox.IsChecked = false;
            return;
        }

        var enabled = MinimizeToTrayCheckBox.IsChecked == true;
        _uiSettings = _uiSettings with { MinimizeToTray = enabled };
        if (Application.Current is App app)
        {
            app.MinimizeToTrayEnabled = enabled;
        }

        PersistUiSettings();
    }

    private void NewChatButton_Click(object? sender, RoutedEventArgs e)
    {
        StartNewChat();
    }

    private void StartNewChat()
    {
        _eventStreamCancellation?.Cancel();
        _eventStreamCancellation?.Dispose();
        _eventStreamCancellation = null;

        _activeRunId = null;
        _pendingPermissionRequestId = null;

        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Insert(0, _currentSession);
        ChatHistoryList.SelectedItem = _currentSession;

        _transcript.Clear();
        TranscriptBox.Text = string.Empty;
        TranscriptBox.IsVisible = false;
        EmptyHero.IsVisible = true;

        PromptBox.Text = string.Empty;
        PermissionSummaryText.Text = "No pending permission requests.";
        PermissionPayloadBox.Text = string.Empty;
        ApprovePermissionButton.IsEnabled = false;
        DenyPermissionButton.IsEnabled = false;

        UpdateConversationTitle();
        SetActiveView(ChatTabButton);
        ToggleConversationDrawer(false);
    }

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

    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        var connected = await EnsureRuntimeConnectedAsync(
            allowStartRuntime: _uiSettings.AutoStartRuntime,
            appendTranscriptOnFailure: true);
        if (!connected || _runtimeApiClient is null)
        {
            return;
        }

        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        try
        {
            if (_currentSession.Title == "New Chat")
            {
                _currentSession.Title = BuildSessionTitle(prompt);
                UpdateConversationTitle();
            }

            AppendTranscript($"[user] {prompt}");
            var run = await _runtimeApiClient.StartRunAsync(prompt, CancellationToken.None);
            _activeRunId = run.RunId;
            AppendTranscript($"[system] Run started: {run.RunId}");
            StartEventStream(run.RunId);
            PromptBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] {ex.Message}");
        }
    }

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
            var accepted = await _runtimeApiClient.CancelRunAsync(_activeRunId, CancellationToken.None);
            AppendTranscript(accepted
                ? $"[system] STOP accepted for {_activeRunId}"
                : $"[system] STOP rejected for {_activeRunId}");
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] Cancel failed: {ex.Message}");
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

    private async void ActionReconnectButton_Click(object? sender, RoutedEventArgs e)
    {
        await EnsureRuntimeConnectedAsync(
            allowStartRuntime: _uiSettings.AutoStartRuntime,
            appendTranscriptOnFailure: true);
        await RefreshActionDrawerAsync();
    }

    private async void ActionRefreshAuditButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshAuditAsync();
        await RefreshActionDrawerAsync();
    }

    private void ActionOpenAuditTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SetActiveView(AuditTabButton);
    }

    private void ActionOpenSettingsTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SetActiveView(SettingsTabButton);
    }

    private async void ActionStartRuntimeButton_Click(object? sender, RoutedEventArgs e)
    {
        await EnsureRuntimeConnectedAsync(allowStartRuntime: true, appendTranscriptOnFailure: true, forceRuntimeLaunch: true);
        await RefreshActionDrawerAsync();
    }

    private void ActionStopRuntimeButton_Click(object? sender, RoutedEventArgs e)
    {
        StopRuntimeButton_Click(sender, e);
        _ = RefreshActionDrawerAsync();
    }

    private async Task RefreshActionDrawerAsync()
    {
        UpdateActionDrawerSummary();

        if (_runtimeApiClient is null)
        {
            ActionAuditPreviewList.ItemsSource = new[] { "Not connected to runtime." };
            return;
        }

        try
        {
            var entries = await _runtimeApiClient.GetAuditAsync(CancellationToken.None);
            ActionAuditPreviewList.ItemsSource = entries
                .TakeLast(15)
                .Reverse()
                .Select(e => $"{e.TimestampUtc:HH:mm:ss} {e.Category}: {e.Message}")
                .ToArray();
        }
        catch (Exception ex)
        {
            ActionAuditPreviewList.ItemsSource = new[] { "Audit preview failed: " + ex.Message };
        }
    }

    private async void ApprovePermissionButton_Click(object? sender, RoutedEventArgs e)
    {
        await SubmitPermissionDecisionAsync(true);
    }

    private async void DenyPermissionButton_Click(object? sender, RoutedEventArgs e)
    {
        await SubmitPermissionDecisionAsync(false);
    }

    private void AppendTranscript(string line)
    {
        if (EmptyHero.IsVisible)
        {
            EmptyHero.IsVisible = false;
            TranscriptBox.IsVisible = true;
        }

        _transcript.AppendLine(line);
        TranscriptBox.Text = _transcript.ToString();

        _currentSession.AppendLine(line);
        BumpSessionToTop(_currentSession);
        UpdateConversationTitle();
    }

    private void BumpSessionToTop(ChatSessionItem session)
    {
        var index = _chatHistory.IndexOf(session);
        if (index > 0)
        {
            _chatHistory.Move(index, 0);
            ChatHistoryList.SelectedItem = session;
        }
    }

    private string BuildSessionTitle(string prompt)
    {
        var trimmed = prompt.Trim();
        if (trimmed.Length <= 34)
        {
            return trimmed;
        }

        return trimmed[..31].TrimEnd() + "...";
    }

    private void UpdateConversationTitle()
    {
        ConversationTitleText.Text = _currentSession.Title;
    }

    private void StartEventStream(string runId)
    {
        _eventStreamCancellation?.Cancel();
        _eventStreamCancellation?.Dispose();
        _eventStreamCancellation = new CancellationTokenSource();
        _ = Task.Run(() => StreamEventsAsync(runId, _eventStreamCancellation.Token));
    }

    private async Task StreamEventsAsync(string runId, CancellationToken cancellationToken)
    {
        if (_runtimeApiClient is null)
        {
            return;
        }

        try
        {
            await foreach (var envelope in _runtimeApiClient.StreamRunEventsAsync(runId, cancellationToken))
            {
                await Dispatcher.UIThread.InvokeAsync(() => HandleEvent(envelope));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => AppendTranscript($"[error] Event stream failed: {ex.Message}"));
        }
    }

    private void HandleEvent(RuntimeEventEnvelope envelope)
    {
        switch (envelope.EventType)
        {
            case RuntimeEventTypes.TokenDelta:
                var token = ReadPayload<TokenDeltaPayload>(envelope.Payload);
                if (token is not null)
                {
                    AppendTranscript($"[assistant] {token.Delta}");
                }
                break;
            case RuntimeEventTypes.RunCompleted:
                AppendTranscript("[system] Run completed.");
                _activeRunId = null;
                break;
            case RuntimeEventTypes.RunFailed:
                var failure = ReadPayload<RunFailedPayload>(envelope.Payload);
                AppendTranscript($"[system] Run failed: {failure?.Error ?? "unknown"}");
                _activeRunId = null;
                break;
            case RuntimeEventTypes.ToolRequested:
                var request = ReadPayload<ToolRequestedPayload>(envelope.Payload);
                if (request is not null)
                {
                    _pendingPermissionRequestId = request.RequestId;
                    PermissionSummaryText.Text = $"Permission requested for {request.ToolName}";
                    PermissionPayloadBox.Text = request.ArgumentsJson;
                    ApprovePermissionButton.IsEnabled = true;
                    DenyPermissionButton.IsEnabled = true;
                    AppendTranscript($"[system] Permission requested: {request.ToolName}");

                    if (_uiSettings.AutoSwitchToPermissions)
                    {
                        SetActiveView(PermTabButton);
                    }
                }
                break;
            case RuntimeEventTypes.ToolApproved:
            case RuntimeEventTypes.ToolDenied:
                var decision = ReadPayload<ToolDecisionPayload>(envelope.Payload);
                PermissionSummaryText.Text = "No pending permission requests.";
                PermissionPayloadBox.Text = string.Empty;
                _pendingPermissionRequestId = null;
                ApprovePermissionButton.IsEnabled = false;
                DenyPermissionButton.IsEnabled = false;
                if (decision is not null)
                {
                    AppendTranscript($"[system] Permission {(decision.Approved ? "approved" : "denied")} for {decision.ToolName}");
                }
                break;
            default:
                break;
        }

        UpdateActionDrawerSummary();
    }

    private async Task SubmitPermissionDecisionAsync(bool approved)
    {
        if (_runtimeApiClient is null || string.IsNullOrWhiteSpace(_pendingPermissionRequestId))
        {
            return;
        }

        try
        {
            var applied = await _runtimeApiClient.SubmitPermissionDecisionAsync(
                _pendingPermissionRequestId,
                approved,
                CancellationToken.None);

            AppendTranscript(applied
                ? $"[system] Permission decision submitted ({(approved ? "approve" : "deny")})."
                : "[system] Permission decision rejected by runtime.");

            SetActiveView(ChatTabButton);
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] Failed to submit permission decision: {ex.Message}");
        }
    }

    private async Task<bool> EnsureRuntimeConnectedAsync(
        bool allowStartRuntime,
        bool appendTranscriptOnFailure,
        bool forceRuntimeLaunch = false)
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

            UpdateActionDrawerSummary();
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
        ConnectButton.IsEnabled = !connecting;
        StartRuntimeButton.IsEnabled = !connecting;
        StopRuntimeButton.IsEnabled = !connecting;

        if (connecting)
        {
            ConnectionStatusText.Text = "Connecting...";
            ConnectionStatusText.Foreground = Brushes.White;
        }
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

        UpdateRuntimeLaunchStatusText();
        UpdateActionDrawerSummary();
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

    private void UpdateActionDrawerSummary()
    {
        var statusBrush = ConnectionStatusText.Foreground
            ?? (IBrush?)this.FindResource("Overlay0Brush")
            ?? Brushes.Gray;

        ActionConnectionStateText.Text = ConnectionStatusText.Text;
        ActionConnectionStateText.Foreground = statusBrush;
        ActionConnectionDot.Background = statusBrush;
        ActionRuntimeStateText.Text = SettingsRuntimeText.Text + " | " + RuntimeLaunchStateText.Text;
    }

    private static T? ReadPayload<T>(object payload)
    {
        if (payload is T typed)
        {
            return typed;
        }

        if (payload is JsonElement jsonElement)
        {
            return jsonElement.Deserialize<T>();
        }

        return default;
    }

    private static string ToAuditLine(AuditEntryDto dto)
    {
        return $"{dto.TimestampUtc:O} [{dto.Category}] {dto.Message}";
    }

    private sealed class ChatSessionItem : INotifyPropertyChanged
    {
        private string _title;
        private DateTimeOffset _updatedAtUtc;
        private readonly StringBuilder _transcript = new();

        public ChatSessionItem(string title)
        {
            _title = title;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }

        public string Title
        {
            get => _title;
            set
            {
                if (_title == value)
                {
                    return;
                }

                _title = value;
                OnPropertyChanged(nameof(Title));
            }
        }

        public string UpdatedLabel => _updatedAtUtc.LocalDateTime.ToString("g");

        public string Preview
        {
            get
            {
                var text = _transcript.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return "No messages yet.";
                }

                if (text.Length <= 96)
                {
                    return text;
                }

                return text[..93] + "...";
            }
        }

        public string TranscriptText => _transcript.ToString();

        public event PropertyChangedEventHandler? PropertyChanged;

        public void AppendLine(string line)
        {
            _transcript.AppendLine(line);
            _updatedAtUtc = DateTimeOffset.UtcNow;
            OnPropertyChanged(nameof(UpdatedLabel));
            OnPropertyChanged(nameof(Preview));
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}





