using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.UI.Avalonia.ViewModels;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private RuntimeApiClient? _runtimeApiClient;
    private HttpClient? _runtimeHttpClient;
    private Uri? _runtimeBaseUri;
    private string? _activeRunId;
    private string? _pendingPermissionRequestId;
    private CancellationTokenSource? _eventStreamCancellation;
    private readonly StringBuilder _transcript = new();
    private readonly RuntimeHostLauncher _runtimeLauncher = new();
    private readonly UiClientSettingsStore _uiSettingsStore = new();
    private readonly SettingsViewModel _backendSettings = new();
    private UiClientSettings _uiSettings;
    private bool _isConnecting;
    private bool _initialConnectAttempted;
    private bool _trayAvailable;
    private bool _stopAllInProgress;
    private AttachedDocumentContext? _attachedDocument;
    private string? _lastUserPrompt;
    private string? _lastAssistantMessage;
    private IReadOnlyList<string> _lastAssistantSources = Array.Empty<string>();
    private readonly Dictionary<string, StringBuilder> _assistantBuffersByRunId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _toolCallsInCurrentRun = new();
    private static readonly Regex MarkdownBoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex MarkdownUnderscoreBoldRegex = new(@"__(.+?)__", RegexOptions.Compiled);
    private readonly LocalTextToSpeechPlaybackService _ttsPlaybackService = new();
    private readonly IMicrophoneCaptureService _microphoneCaptureService = new NAudioMicrophoneCaptureService();
    private readonly VoiceHostLauncher _voiceHostLauncher = new();
    private readonly LocalAsrHttpTranscriptionService _transcriptionService;
    private CancellationTokenSource? _voiceHostLifecycleCancellation;
    private readonly SemaphoreSlim _pttGate = new(1, 1);
    private bool _pttCaptureActive;
    private bool _pttHotkeyDown;
    private int _pttSessionCounter;

    private readonly ObservableCollection<MemoryFactRowViewModel> _memoryFacts = [];
    private readonly ObservableCollection<MemoryEventRowViewModel> _memoryEvents = [];
    private readonly ObservableCollection<MemoryChunkRowViewModel> _memoryChunks = [];
    private readonly ObservableCollection<MemoryNuggetRowViewModel> _memoryNuggets = [];
    private readonly ObservableCollection<ProfileListItemViewModel> _profileItems = [];
    private readonly ObservableCollection<PersonalityListItemViewModel> _personalityItems = [];

    private readonly ObservableCollection<ChatSessionItem> _chatHistory = [];
    private ChatSessionItem _currentSession;

    private readonly ToggleButton[] _viewTabs;
    private readonly Control[] _viewPanels;

    public MainWindow()
    {
        InitializeComponent();

        _viewTabs = [ChatTabButton, BriefingTabButton, SettingsTabButton];
        _viewPanels = [ChatView, BriefingView, SettingsView];

        _uiSettings = _uiSettingsStore.Load();
        ApplyUiSettingsToControls();

        SettingsHeaderBar.DataContext = _backendSettings;
        SettingsTabControl.DataContext = _backendSettings;

        LlmsScrollViewer.DataContext = _backendSettings;
        AudioScrollViewer.DataContext = _backendSettings;
        PermissionsTabItem.DataContext = _backendSettings;
        ConstraintsPanel.DataContext = _backendSettings;
        _backendSettings.PropertyChanged += BackendSettings_PropertyChanged;
        _transcriptionService = new LocalAsrHttpTranscriptionService(() => _backendSettings.VoiceHostBaseUrl);
        ApplyAudioPreferences();
        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Add(_currentSession);
        ChatHistoryList.ItemsSource = _chatHistory;
        ChatMessagesList.ItemsSource = _currentSession.Messages;
        PromptBox.AddHandler(KeyDownEvent, PromptBox_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        LmStudioPresetBtn.Click += LlmPreset_Click;
        OllamaPresetBtn.Click += LlmPreset_Click;
        OpenAiPresetBtn.Click += LlmPreset_Click;
        ChatHistoryList.SelectedItem = _currentSession;
        MemoryFactsList.ItemsSource = _memoryFacts;
        MemoryEventsList.ItemsSource = _memoryEvents;
        MemoryChunksList.ItemsSource = _memoryChunks;
        MemoryNuggetsList.ItemsSource = _memoryNuggets;
        ProfilesList.ItemsSource = _profileItems;
        PersonalitiesList.ItemsSource = _personalityItems;
        InitializeBriefingUi();
        InitializePushToTalkUi();
        UpdateAttachmentUi();
        UpdateConversationTitle();

        if (!OperatingSystem.IsWindows())
        {
            PttHoldButton.IsEnabled = false;
            ReadAloudButton.IsEnabled = false;
            SetPushToTalkPlatformUnavailable();
        }

        SyncLastMessageCacheFromCurrentSession();
        UpdateComposerState();
        UpdateChatActionState();
        UpdateHeaderConnectionControls();
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
    private void LlmPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            _backendSettings.LlmBaseUrl = url;
            _backendSettings.LlmModel = string.Empty;
        }
    }

    private void BackendSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.SelectedInputDevice), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.SelectedOutputDevice), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.InputGain), StringComparison.Ordinal))
        {
            ApplyAudioPreferences();
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostEnabled), StringComparison.Ordinal))
        {
            BeginVoiceHostLifecycleTransition(_backendSettings.VoiceHostEnabled);
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostBaseUrl), StringComparison.Ordinal) &&
            _backendSettings.VoiceHostEnabled)
        {
            BeginVoiceHostLifecycleTransition(enabled: true, restartManagedProcess: true);
        }

        if (!ReferenceEquals(SettingsTabControl.SelectedItem, AudioTabItem))
        {
            if (string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostEnabled), StringComparison.Ordinal) &&
                !_backendSettings.VoiceHostEnabled)
            {
                _backendSettings.StopVoiceHostHealthPolling();
            }

            return;
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostEnabled), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostBaseUrl), StringComparison.Ordinal))
        {
            if (_backendSettings.VoiceHostEnabled)
            {
                _backendSettings.StartVoiceHostHealthPolling();
                _ = _backendSettings.RefreshVoiceHostHealthAsync();
            }
            else
            {
                _backendSettings.StopVoiceHostHealthPolling();
            }
        }
    }
    protected override void OnClosed(EventArgs e)
    {
        Opened -= OnOpened;
        _backendSettings.PropertyChanged -= BackendSettings_PropertyChanged;
        _backendSettings.StopVoiceHostHealthPolling();
        _voiceHostLifecycleCancellation?.Cancel();
        _voiceHostLifecycleCancellation?.Dispose();
        _voiceHostLifecycleCancellation = null;
        _voiceHostLauncher.Dispose();

        _eventStreamCancellation?.Cancel();
        _eventStreamCancellation?.Dispose();
        _eventStreamCancellation = null;

        _runtimeHttpClient?.Dispose();
        _runtimeHttpClient = null;
        _runtimeApiClient = null;

        _runtimeLauncher.Dispose();
        DisposePushToTalkUi();
        _transcriptionService.Dispose();
        _microphoneCaptureService.Dispose();
        _pttGate.Dispose();
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
        TryStartGlobalPushToTalkHotkey();
        if (_uiSettings.AutoConnectOnLaunch && !AppStartupOptions.Current.SmokeTestMode)
        {
            await EnsureRuntimeConnectedAsync(
                allowStartRuntime: _uiSettings.AutoStartRuntime,
                appendTranscriptOnFailure: false);
        }

        UpdateRuntimeLaunchStatusText();
        UpdateHeaderConnectionControls();
        UpdateActionDrawerSummary();
        UpdateComposerState();
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

    private async void SaveSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        var snapshot = _backendSettings.BuildPersistableSnapshot();

        try
        {
            if (_runtimeApiClient is not null)
            {
                var persisted = await _runtimeApiClient.SaveSettingsAsync(snapshot, CancellationToken.None);
                _backendSettings.ApplySavedSnapshot(persisted, "Settings saved and applied to the connected runtime.");
                await RefreshSearchStatusAsync();
                AppendTranscript("[system] Settings saved and applied to the connected runtime.");
                return;
            }

            SettingsManager.Save(snapshot);
            var localPersisted = SettingsManager.Load();
            _backendSettings.ApplySavedSnapshot(localPersisted, "Settings saved locally. Connect or restart the runtime to apply them.");
            _backendSettings.ResetSearchHealthState(
                "Not connected",
                "Settings saved locally. Connect the runtime to inspect live web-search and MCP health.");
            AppendTranscript("[system] Settings saved locally.");
        }
        catch (Exception ex)
        {
            try
            {
                SettingsManager.Save(snapshot);
                var localPersisted = SettingsManager.Load();
                _backendSettings.ApplySavedSnapshot(localPersisted, "Settings saved locally. Runtime sync failed; reconnect to apply them.");
                _backendSettings.ResetSearchHealthState(
                    "Unavailable",
                    "Settings saved locally, but runtime sync failed. Reconnect to inspect live web-search and MCP health.");
                AppendTranscript("[error] Runtime settings sync failed: " + ex.Message);
                AppendTranscript("[system] Settings saved locally.");
            }
            catch (Exception saveEx)
            {
                _backendSettings.SetStatus("Settings save failed: " + saveEx.Message);
                AppendTranscript("[error] Settings save failed: " + saveEx.Message);
            }
        }
    }

    private void ReloadSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        _backendSettings.Reload();
        AppendTranscript("[system] Settings reloaded from disk.");
    }

    private async void RefreshPrimaryModelsButton_Click(object? sender, RoutedEventArgs e)
    {
        await _backendSettings.RefreshPrimaryModelsAsync();
    }

    private async void RefreshGatekeeperModelsButton_Click(object? sender, RoutedEventArgs e)
    {
        await _backendSettings.RefreshGatekeeperModelsAsync();
    }

    private async void RefreshVoiceHostHealthButton_Click(object? sender, RoutedEventArgs e)
    {
        await _backendSettings.RefreshVoiceHostHealthAsync();
    }

    private void RefreshAudioDevicesButton_Click(object? sender, RoutedEventArgs e)
    {
        _backendSettings.RefreshAudioDevices();
        ApplyAudioPreferences();
    }

    private void RefreshTtsVoicesButton_Click(object? sender, RoutedEventArgs e)
    {
        _backendSettings.RefreshVoiceCatalogs("TTS voices refreshed.");
    }

    private void BeginVoiceHostLifecycleTransition(bool enabled, bool restartManagedProcess = false)
    {
        _voiceHostLifecycleCancellation?.Cancel();
        _voiceHostLifecycleCancellation?.Dispose();
        _voiceHostLifecycleCancellation = null;

        if (!enabled)
        {
            _voiceHostLauncher.StopManagedVoiceHost();
            return;
        }

        if (restartManagedProcess)
        {
            _voiceHostLauncher.StopManagedVoiceHost();
        }

        var cts = new CancellationTokenSource();
        _voiceHostLifecycleCancellation = cts;
        _ = StartManagedVoiceHostAsync(cts.Token);
    }

    private async Task StartManagedVoiceHostAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = _backendSettings.BuildPersistableSnapshot();
            var baseUrl = snapshot.Voice.GetVoiceHostBaseUrl();
            _backendSettings.SetVoiceHostStatus("Starting...", $"Starting VoiceHost at {baseUrl}...");

            var result = await _voiceHostLauncher.EnsureRunningAsync(snapshot.Voice, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (result.Status is VoiceHostLaunchStatus.Started or VoiceHostLaunchStatus.AlreadyRunning)
            {
                _backendSettings.SetVoiceHostStatus("Checking...", result.Message);
                await _backendSettings.RefreshVoiceHostHealthAsync(cancellationToken);
                return;
            }

            _backendSettings.SetVoiceHostStatus("Failed", result.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Toggle changed while startup was in flight.
        }
        catch (Exception ex)
        {
            _backendSettings.SetVoiceHostStatus("Error", ex.Message);
        }
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

        InputBar.IsVisible = ChatTabButton.IsChecked == true || BriefingTabButton.IsChecked == true;
    }

    private void SettingsTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabControl)
        {
            return;
        }

        if (ReferenceEquals(tabControl.SelectedItem, LlmsTabItem))
        {
            _backendSettings.StopVoiceHostHealthPolling();
            _ = _backendSettings.OnLlmsTabActivatedAsync();
        }
        else if (ReferenceEquals(tabControl.SelectedItem, AudioTabItem))
        {
            _backendSettings.RefreshVoiceCatalogs();
            ApplyAudioPreferences();
            _backendSettings.StartVoiceHostHealthPolling();
            _ = _backendSettings.RefreshVoiceHostHealthAsync();
        }
        else if (ReferenceEquals(tabControl.SelectedItem, SearchTabItem))
        {
            _backendSettings.StopVoiceHostHealthPolling();
            _ = RefreshSearchStatusAsync();
        }
        else
        {
            _backendSettings.StopVoiceHostHealthPolling();

            if (ReferenceEquals(tabControl.SelectedItem, AuditTabItem))
            {
                _ = RefreshAuditAsync();
            }
            else if (ReferenceEquals(tabControl.SelectedItem, MemoryTabItem))
            {
                _ = RefreshMemoryAsync();
            }
            else if (ReferenceEquals(tabControl.SelectedItem, ProfilesTabItem))
            {
                _ = RefreshProfilesAsync();
            }
        }
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        // Escape hides the window (like WPF Close)
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Hide();
            return;
        }

        if (OperatingSystem.IsWindows() &&
            ShouldUseWindowScopedPttHotkey() &&
            e.Key == Key.Escape &&
            e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            e.Handled = true;
            _ = RequestVoiceCancelAsync("window cancel hotkey");
            return;
        }

        if (ShouldUseWindowScopedPttHotkey() &&
            e.Key == Key.M &&
            e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            e.Handled = true;
            if (!_pttHotkeyDown)
            {
                _pttHotkeyDown = true;
                _ = BeginPushToTalkAsync("hotkey");
            }

            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.D1:
                    SetActiveView(ChatTabButton);
                    e.Handled = true;
                    return;
                case Key.D2:
                    SetActiveView(BriefingTabButton);
                    e.Handled = true;
                    return;
                case Key.D3:
                    SetActiveView(SettingsTabButton);
                    SettingsTabControl.SelectedItem = PermissionsTabItem;
                    e.Handled = true;
                    return;
                case Key.D4:
                    SetActiveView(SettingsTabButton);
                    SettingsTabControl.SelectedItem = AuditTabItem;
                    e.Handled = true;
                    return;
                case Key.D5:
                    SetActiveView(SettingsTabButton);
                    SettingsTabControl.SelectedItem = MemoryTabItem;
                    e.Handled = true;
                    return;
                case Key.D6:
                    SetActiveView(SettingsTabButton);
                    SettingsTabControl.SelectedItem = ProfilesTabItem;
                    e.Handled = true;
                    return;
                case Key.D7:
                    SetActiveView(SettingsTabButton);
                    e.Handled = true;
                    return;
            }
        }

    }

    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (ShouldUseWindowScopedPttHotkey() && e.Key == Key.M && _pttHotkeyDown)
        {
            _pttHotkeyDown = false;
            e.Handled = true;
            _ = EndPushToTalkAsync("hotkey");
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

    private void ApplyAudioPreferences()
    {
        _microphoneCaptureService.DeviceNumber = _backendSettings.SelectedInputDevice?.DeviceNumber ?? -1;
        _microphoneCaptureService.InputGain = _backendSettings.InputGain;
        _ttsPlaybackService.OutputDeviceNumber = _backendSettings.SelectedOutputDevice?.DeviceNumber ?? -1;
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
        ChatMessagesList.ItemsSource = _currentSession.Messages;
        EmptyHero.IsVisible = _currentSession.Messages.Count == 0;
        SyncLastMessageCacheFromCurrentSession();
        UpdateChatActionState();
        UpdateComposerState();

        UpdateConversationTitle();
        LoadBriefingForSession(session);
        SetActiveView(ChatTabButton);
        ToggleConversationDrawer(false);
    }

    private void ClearHistoryButton_Click(object? sender, RoutedEventArgs e)
    {
        _chatHistory.Clear();
        _briefingBySession.Clear();
        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Add(_currentSession);
        ChatHistoryList.SelectedItem = _currentSession;

        ChatMessagesList.ItemsSource = _currentSession.Messages;
        EmptyHero.IsVisible = true;
        SyncLastMessageCacheFromCurrentSession();
        UpdateChatActionState();
        UpdateComposerState();

        UpdateConversationTitle();
        LoadBriefingForSession(_currentSession);
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
        _assistantBuffersByRunId.Clear();
        _lastUserPrompt = null;
        _lastAssistantMessage = null;
        _lastAssistantSources = Array.Empty<string>();

        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Insert(0, _currentSession);
        ChatHistoryList.SelectedItem = _currentSession;

        ChatMessagesList.ItemsSource = _currentSession.Messages;
        EmptyHero.IsVisible = true;

        PromptBox.Text = string.Empty;
        ResetPermissionRequestUi();

        _attachedDocument = null;
        UpdateAttachmentUi();
        SyncLastMessageCacheFromCurrentSession();
        UpdateChatActionState();
        UpdateComposerState();

        UpdateConversationTitle();
        LoadBriefingForSession(_currentSession);
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
            UpdateComposerState();
            return;
        }

        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            UpdateComposerState();
            return;
        }

        var runtimePrompt = prompt;
        if (_attachedDocument is not null)
        {
            runtimePrompt = _attachedDocument.BuildContextBlock(prompt) + "\n" + prompt;
            AppendTranscript($"[system] Attached context injected: {_attachedDocument.FileName}");
            _attachedDocument = null;
            UpdateAttachmentUi();
        }

        try
        {
            if (_currentSession.Title == "New Chat")
            {
                _currentSession.Title = BuildSessionTitle(prompt);
                UpdateConversationTitle();
            }

            _lastUserPrompt = prompt;
            AppendTranscript($"[user] {prompt}");
            var run = await _runtimeApiClient.StartRunAsync(runtimePrompt, CancellationToken.None);
            _activeRunId = run.RunId;
            _assistantBuffersByRunId[run.RunId] = new StringBuilder();
            _currentSession.AddPendingAssistantMessage();
            UpdateComposerState();
            StartEventStream(run.RunId);
            PromptBox.Text = string.Empty;
            UpdateComposerState();
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] {ex.Message}");
            UpdateComposerState();
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

    private void PttHoldButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!PttHoldButton.IsEnabled)
        {
            return;
        }

        e.Pointer.Capture(PttHoldButton);
        e.Handled = true;
        _ = BeginPushToTalkAsync("button");
    }

    private void PttHoldButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured == PttHoldButton)
        {
            e.Pointer.Capture(null);
        }

        e.Handled = true;
        _ = EndPushToTalkAsync("button");
    }

    private void PttHoldButton_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _ = EndPushToTalkAsync("capture_lost");
    }

    private async Task BeginPushToTalkAsync(string source)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Interrupt TTS if currently speaking (WPF parity)
        if (_readAloudActive)
        {
            _readAloudCancellation?.Cancel();
            _readAloudActive = false;
        }

        await _pttGate.WaitAsync();
        try
        {
            if (_pttCaptureActive)
            {
                return;
            }

            if (_pttTranscriptionActive)
            {
                SetPushToTalkBusyTranscribing();
                return;
            }

            await _microphoneCaptureService.StartCaptureAsync(CancellationToken.None);
            _pttCaptureActive = true;
            MarkPushToTalkCaptureStarted(source);
            AppendTranscript($"[system] PTT listening ({source}).");
        }
        catch (Exception ex)
        {
            _pttCaptureActive = false;
            MarkPushToTalkFailure("PTT start failed.", ex.Message);
            AppendTranscript("[error] PTT start failed: " + ex.Message);
        }
        finally
        {
            _pttGate.Release();
        }
    }

    private async Task EndPushToTalkAsync(string source)
    {
        byte[]? wavBytes;
        CancellationTokenSource? transcriptionCancellation = null;

        await _pttGate.WaitAsync();
        try
        {
            if (!_pttCaptureActive)
            {
                return;
            }

            _pttCaptureActive = false;
            _pttTranscriptionActive = true;
            transcriptionCancellation = new CancellationTokenSource();
            _pttTranscriptionCancellation = transcriptionCancellation;
            MarkPushToTalkTranscribing(source);
            wavBytes = await _microphoneCaptureService.StopCaptureAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _pttTranscriptionActive = false;
            if (ReferenceEquals(_pttTranscriptionCancellation, transcriptionCancellation))
            {
                _pttTranscriptionCancellation = null;
            }

            transcriptionCancellation?.Dispose();
            MarkPushToTalkFailure("PTT stop failed.", ex.Message);
            AppendTranscript("[error] PTT stop failed: " + ex.Message);
            return;
        }
        finally
        {
            _pttGate.Release();
        }

        if (wavBytes is null || wavBytes.Length == 0)
        {
            await ClearPushToTalkTranscriptionAsync(transcriptionCancellation);
            MarkPushToTalkNoAudio();
            return;
        }

        try
        {
            var sessionId = $"ui-ptt-{Interlocked.Increment(ref _pttSessionCounter)}";
            var transcript = (await _transcriptionService.TranscribeAsync(
                wavBytes,
                sessionId,
                transcriptionCancellation?.Token ?? CancellationToken.None)).Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                MarkPushToTalkNoSpeech();
                return;
            }

            var existing = PromptBox.Text;
            PromptBox.Text = string.IsNullOrWhiteSpace(existing)
                ? transcript
                : existing.TrimEnd() + " " + transcript;
            PromptBox.CaretIndex = PromptBox.Text.Length;
            MarkPushToTalkTranscriptInserted(transcript);
            AppendTranscript($"[voice] {transcript}");
        }
        catch (OperationCanceledException) when (transcriptionCancellation?.IsCancellationRequested == true)
        {
            MarkPushToTalkCanceled(
                headline: "Transcription canceled.",
                detail: $"The local ASR request for {DescribeCaptureSource(_pttLastCaptureSource)} was canceled before the composer changed.");
        }
        catch (Exception ex)
        {
            MarkPushToTalkFailure("Transcription failed.", ex.Message);
            AppendTranscript("[error] PTT transcription failed: " + ex.Message);
        }
        finally
        {
            await ClearPushToTalkTranscriptionAsync(transcriptionCancellation);
        }
    }

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

    private void ActionOpenAuditTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SetActiveView(SettingsTabButton);
        SettingsTabControl.SelectedItem = AuditTabItem;
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

    // ---------------------------------------------------------------
    // Permission Request UI helpers
    // ---------------------------------------------------------------

    private void ShowPermissionRequest(ToolRequestedPayload request)
    {
        var (category, description, warning) = ClassifyTool(request.ToolName);

        PermToolNameText.Text = request.ToolName ?? "(unknown tool)";
        PermCategoryText.Text = category;
        PermDescriptionText.Text = description;
        PermDetailsText.Text = FormatPermissionDetails(request.ToolName, request.Reason, request.ArgumentsJson);
        PermWarningText.Text = warning;

        PermissionPayloadBox.Text = request.ArgumentsJson;

        SetPermissionButtonsEnabled(true);

        PermissionRequestCard.IsVisible = true;
        PermissionIdleCard.IsVisible = false;
    }

    private void ResetPermissionRequestUi()
    {
        PermissionRequestCard.IsVisible = false;
        PermissionIdleCard.IsVisible = true;
        PermissionSummaryText.Text = "No pending permission requests.";
        PermissionPayloadBox.Text = string.Empty;
        SetPermissionButtonsEnabled(false);
    }

    private void SetPermissionButtonsEnabled(bool enabled)
    {
        ApprovePermissionButton.IsEnabled = enabled;
        DenyPermissionButton.IsEnabled = enabled;
        AllowSessionButton.IsEnabled = enabled;
        AllowAlwaysButton.IsEnabled = enabled;
    }

    private static string FormatPermissionDetails(string? toolName, string? reason, string? argsJson)
    {
        // Prefer the reason/purpose field, fall back to args JSON
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var cleaned = reason;
            if (toolName is not null)
            {
                var prefixFull = $"Use tool '{toolName}'.";
                var prefixArgs = $"Use '{toolName}': ";
                if (cleaned.Equals(prefixFull, StringComparison.Ordinal))
                    return string.IsNullOrWhiteSpace(argsJson) ? "(no additional details)" : argsJson;
                if (cleaned.StartsWith(prefixArgs, StringComparison.Ordinal))
                    cleaned = cleaned[prefixArgs.Length..];
            }

            return cleaned;
        }

        return string.IsNullOrWhiteSpace(argsJson) ? "(no additional details)" : argsJson;
    }

    private static (string Category, string Description, string Warning) ClassifyTool(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return ("Unknown", "Perform an action", "Sir Thaddeus is requesting access to a tool on your behalf.");

        var lower = toolName.ToLowerInvariant();

        // Memory
        if (lower.Contains("memory_retrieve") || lower.Contains("memory_list"))
            return ("Memory Read", "Retrieve stored memories and facts",
                "This tool will read from your local memory database.");
        if (lower.Contains("memory_store") || lower.Contains("memory_update") || lower.Contains("memory_delete"))
            return ("Memory Write", "Store, update, or delete memories",
                "This tool will store or modify data in your local memory database.");

        // Screen
        if (lower.Contains("screen") || lower.Contains("screenshot") || lower.Contains("active_window"))
            return ("Screen Reading", "Read content visible on your screen",
                "This tool will capture what is currently visible on your screen.");

        // File system
        if (lower.Contains("file_read") || lower.Contains("file_write") || lower.Contains("file_list") || lower.Contains("file_"))
            return ("File System Access", "Read or write files on your computer",
                "This tool can read or write files on your computer. Review the path before allowing.");

        // System execute
        if (lower.Contains("system_execute") || lower.Contains("execute") || lower.Contains("shell") || lower.Contains("powershell"))
            return ("System Command Execution", "Run commands on your system",
                "This tool can run commands on your system. Review the details carefully before allowing.");

        // Web
        if (lower.Contains("web_search") || lower.Contains("browser") || lower.Contains("navigate") ||
            lower.Contains("weather") || lower.Contains("places_lookup") || lower.Contains("feed_fetch") ||
            lower.Contains("status_check") || lower.Contains("holidays"))
            return ("Web Access", "Search the web and navigate to pages",
                "This tool will make an outbound internet request on your behalf.");

        return ("Agent Tool", "Perform a privileged operation",
            "Sir Thaddeus is requesting access to a tool on your behalf. Choose how to proceed.");
    }

    private void AppendTranscript(string line)
    {
        if (EmptyHero.IsVisible)
        {
            EmptyHero.IsVisible = false;
        }

        if (line.StartsWith("[user] "))
        {
            _currentSession.AddMessage("user", line[7..]);
        }
        else if (line.StartsWith("[assistant] "))
        {
            _currentSession.AppendToLastAssistantMessage(line[12..]);
        }
        else if (line.StartsWith("[voice] "))
        {
            _currentSession.AddMessage("user", line[8..]);
        }
        else if (line.StartsWith("[system] "))
        {
            _currentSession.AddMessage("system", line[9..]);
        }
        else if (line.StartsWith("[error] "))
        {
            _currentSession.AddMessage("system", line);
        }
        else
        {
            _currentSession.AddMessage("system", line);
        }

        BumpSessionToTop(_currentSession);
        SyncLastMessageCacheFromCurrentSession();
        UpdateChatActionState();
        UpdateComposerState();
        UpdateConversationTitle();
        ScrollChatToBottom();
    }

    private void ScrollChatToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ChatScroller.Offset = new Vector(ChatScroller.Offset.X, double.MaxValue);
        }, DispatcherPriority.Background);
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
        ConversationTitleText.Text = "Conversation";
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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _currentSession.ClearPendingAssistantMessage();
                _assistantBuffersByRunId.Remove(runId);
                _activeRunId = null;
                AppendTranscript($"[error] Event stream failed: {ex.Message}");
                UpdateComposerState();
            });
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
                    if (string.IsNullOrEmpty(token.Delta))
                    {
                        break;
                    }

                    if (!_assistantBuffersByRunId.TryGetValue(envelope.RunId, out var buffer))
                    {
                        buffer = new StringBuilder();
                        _assistantBuffersByRunId[envelope.RunId] = buffer;
                    }

                    buffer.Append(token.Delta);
                    AppendTranscript($"[assistant] {token.Delta}");
                }
                break;
            case RuntimeEventTypes.RunCompleted:
                var completed = ReadPayload<RunCompletedPayload>(envelope.Payload);
                if (_assistantBuffersByRunId.TryGetValue(envelope.RunId, out var completedBuffer))
                {
                    var streamedText = completedBuffer.ToString();
                    if (string.IsNullOrWhiteSpace(streamedText) &&
                        !string.IsNullOrWhiteSpace(completed?.FinalText))
                    {
                        _lastAssistantMessage = completed.FinalText;
                        AppendTranscript($"[assistant] {completed.FinalText}");
                    }
                    else
                    {
                        _lastAssistantMessage = streamedText;
                    }

                    _assistantBuffersByRunId.Remove(envelope.RunId);
                }
                else if (!string.IsNullOrWhiteSpace(completed?.FinalText))
                {
                    _lastAssistantMessage = completed.FinalText;
                    AppendTranscript($"[assistant] {completed.FinalText}");
                }

                _lastAssistantSources = BuildAssistantSourceList(_lastAssistantMessage ?? string.Empty, completed?.Briefing);
                if (completed?.Briefing is not null)
                {
                    DisplayBriefing(completed.Briefing, recordHistory: true, activateTab: true);
                }

                // Extract thought/reasoning content and update the last assistant message.
                if (!string.IsNullOrWhiteSpace(_lastAssistantMessage))
                {
                    var parts = ParseAssistantDisplayParts(_lastAssistantMessage);
                    var lastMsg = _currentSession.Messages.LastOrDefault(m => m.Role == "assistant");
                    if (lastMsg is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(parts.ThinkingText))
                        {
                            lastMsg.ThoughtContent = parts.ThinkingText;
                            lastMsg.Content = StripMarkdownFormatting(parts.DisplayText);
                        }
                        else
                        {
                            // Always strip markdown bold/italic and clean LLM output markers.
                            lastMsg.Content = StripMarkdownFormatting(parts.DisplayText);
                        }

                        // Stash the user prompt so the context menu "Retry" can resubmit it.
                        if (!string.IsNullOrWhiteSpace(_lastUserPrompt))
                        {
                            lastMsg.RetryPrompt = _lastUserPrompt;
                        }
                    }
                }

                // Emit consolidated tool summary as an inline footer on the assistant message.
                if (_toolCallsInCurrentRun.Count > 0)
                {
                    var toolNames = string.Join(", ", _toolCallsInCurrentRun.Distinct());
                    var iterations = completed?.ToolLoopIterations ?? 0;
                    var summary = $"\u21B3 tools called: {_toolCallsInCurrentRun.Count} ({toolNames}) \u00B7 {iterations} round-trip(s)";

                    // Attach to the last assistant message as a subtle footer.
                    var assistantMsg = _currentSession.Messages.LastOrDefault(m => m.Role == "assistant");
                    if (assistantMsg is not null)
                    {
                        assistantMsg.ToolSummary = summary;
                    }

                    _toolCallsInCurrentRun.Clear();
                }

                // Safety: clear any orphaned pending "Thinking..." message.
                _currentSession.ClearPendingAssistantMessage();

                _activeRunId = null;
                UpdateComposerState();
                break;
            case RuntimeEventTypes.RunFailed:
                _currentSession.ClearPendingAssistantMessage();
                _assistantBuffersByRunId.Remove(envelope.RunId);
                _toolCallsInCurrentRun.Clear();
                var failure = ReadPayload<RunFailedPayload>(envelope.Payload);
                AppendTranscript($"[system] Run failed: {failure?.Error ?? "unknown"}");
                _activeRunId = null;
                UpdateComposerState();
                break;
            case RuntimeEventTypes.ToolRequested:
                var request = ReadPayload<ToolRequestedPayload>(envelope.Payload);
                if (request is not null)
                {
                    _pendingPermissionRequestId = request.RequestId;
                    _toolCallsInCurrentRun.Add(request.ToolName);
                    ShowPermissionRequest(request);
                    // Don't clutter chat with individual permission messages.
                    // A consolidated tool activity summary is emitted at RunCompleted.

                    if (_uiSettings.AutoSwitchToPermissions)
                    {
                        SetActiveView(SettingsTabButton);
                        SettingsTabControl.SelectedItem = PermissionsTabItem;
                    }
                }
                break;
            case RuntimeEventTypes.ToolApproved:
            case RuntimeEventTypes.ToolDenied:
                var decision = ReadPayload<ToolDecisionPayload>(envelope.Payload);
                _pendingPermissionRequestId = null;
                ResetPermissionRequestUi();
                // Suppressed: individual approval/denial messages no longer clutter chat.
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

            // Suppressed: don't clutter chat with individual decision messages.
            // The consolidated tool summary is added to the assistant message at RunCompleted.
            if (!applied)
            {
                AppendTranscript("[system] Permission decision rejected by runtime.");
            }

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

            UpdateHeaderConnectionControls();
            UpdateActionDrawerSummary();
            UpdateComposerState();
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
            return jsonElement.Deserialize<T>(PayloadJsonOptions);
        }

        return default;
    }

    private static string ToAuditLine(AuditEntryDto dto)
    {
        return $"{dto.TimestampUtc:O} [{dto.Category}] {dto.Message}";
    }

    private async void RefreshMemoryButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshMemoryAsync();
    }

    private async Task RefreshMemoryAsync()
    {
        if (_runtimeApiClient is null)
        {
            MemoryStatusText.Text = "Memory: runtime not connected";
            _memoryFacts.Clear();
            _memoryEvents.Clear();
            _memoryChunks.Clear();
            _memoryNuggets.Clear();
            return;
        }

        try
        {
            var response = await _runtimeApiClient.GetMemoryAsync(MemoryFilterBox.Text, 40, CancellationToken.None);
            _memoryFacts.Clear();
            _memoryEvents.Clear();
            _memoryChunks.Clear();
            _memoryNuggets.Clear();

            foreach (var fact in response.Facts)
            {
                _memoryFacts.Add(new MemoryFactRowViewModel(fact));
            }

            foreach (var evt in response.Events)
            {
                _memoryEvents.Add(new MemoryEventRowViewModel(evt));
            }

            foreach (var chunk in response.Chunks)
            {
                _memoryChunks.Add(new MemoryChunkRowViewModel(chunk));
            }

            foreach (var nugget in response.Nuggets)
            {
                _memoryNuggets.Add(new MemoryNuggetRowViewModel(nugget));
            }

            MemoryStatusText.Text = $"Memory loaded. Facts={response.TotalFacts}, Events={response.TotalEvents}, Chunks={response.TotalChunks}, Nuggets={response.TotalNuggets}";
        }
        catch (Exception ex)
        {
            MemoryStatusText.Text = "Memory load failed: " + ex.Message;
        }
    }

    private async void MemoryFactsList_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is MemoryFactRowViewModel row)
        {
            if (_runtimeApiClient is null) return;
            try
            {
                await _runtimeApiClient.SaveMemoryFactAsync(row.MemoryId, row.ToSaveRequest(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                MemoryStatusText.Text = $"Failed to save fact: {ex.Message}";
            }
        }
    }

    private async void MemoryEventsList_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is MemoryEventRowViewModel row)
        {
            if (_runtimeApiClient is null) return;
            try
            {
                await _runtimeApiClient.SaveMemoryEventAsync(row.EventId, row.ToSaveRequest(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                MemoryStatusText.Text = $"Failed to save event: {ex.Message}";
            }
        }
    }

    private async void MemoryChunksList_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is MemoryChunkRowViewModel row)
        {
            if (_runtimeApiClient is null) return;
            try
            {
                await _runtimeApiClient.SaveMemoryChunkAsync(row.ChunkId, row.ToSaveRequest(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                MemoryStatusText.Text = $"Failed to save chunk: {ex.Message}";
            }
        }
    }

    private async void MemoryNuggetsList_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is MemoryNuggetRowViewModel row)
        {
            if (_runtimeApiClient is null) return;
            try
            {
                await _runtimeApiClient.SaveMemoryNuggetAsync(row.NuggetId, row.ToSaveRequest(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                MemoryStatusText.Text = $"Failed to save nugget: {ex.Message}";
            }
        }
    }

    private async void RefreshProfilesButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshProfilesAsync();
    }

    private async Task RefreshProfilesAsync()
    {
        if (_runtimeApiClient is null)
        {
            ProfilesStatusText.Text = "Profiles: runtime not connected";
            _profileItems.Clear();
            _personalityItems.Clear();
            EditProfileButton.IsEnabled = false;
            DeleteProfileButton.IsEnabled = false;
            SetActiveProfileButton.IsEnabled = false;
            EditPersonalityButton.IsEnabled = false;
            DeletePersonalityButton.IsEnabled = false;
            SetActivePersonalityButton.IsEnabled = false;
            return;
        }

        try
        {
            var response = await _runtimeApiClient.GetProfilesAsync(CancellationToken.None);

            _profileItems.Clear();
            foreach (var profile in response.Profiles)
            {
                _profileItems.Add(new ProfileListItemViewModel(profile));
            }

            _personalityItems.Clear();
            foreach (var personality in response.Personalities)
            {
                _personalityItems.Add(new PersonalityListItemViewModel(personality));
            }

            ProfilesStatusText.Text = $"Profiles loaded. Active profile: {response.ActiveProfileId ?? "(none)"} | Active personality: {response.ActivePersonalityId}";
            SelectProfile(response.ActiveProfileId ?? _profileItems.FirstOrDefault()?.ProfileId);
            SelectPersonality(response.ActivePersonalityId ?? _personalityItems.FirstOrDefault()?.Id);
            _backendSettings.ApplyActiveIdentity(response.ActiveProfileId, response.ActivePersonalityId);
        }
        catch (Exception ex)
        {
            ProfilesStatusText.Text = "Profiles load failed: " + ex.Message;
        }
    }

    private void ProfilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is ProfileListItemViewModel selected)
        {
            EditProfileButton.IsEnabled = true;
            DeleteProfileButton.IsEnabled = true;
            SetActiveProfileButton.IsEnabled = !selected.IsActive;
        }
        else
        {
            EditProfileButton.IsEnabled = false;
            DeleteProfileButton.IsEnabled = false;
            SetActiveProfileButton.IsEnabled = false;
        }
    }

    private void PersonalitiesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PersonalitiesList.SelectedItem is PersonalityListItemViewModel selected)
        {
            EditPersonalityButton.IsEnabled = true;
            DeletePersonalityButton.IsEnabled = true;
            SetActivePersonalityButton.IsEnabled = !selected.IsActive;
        }
        else
        {
            EditPersonalityButton.IsEnabled = false;
            DeletePersonalityButton.IsEnabled = false;
            SetActivePersonalityButton.IsEnabled = false;
        }
    }

    private async void SetActiveProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_runtimeApiClient is null || ProfilesList.SelectedItem is not ProfileListItemViewModel selected)
        {
            return;
        }

        try
        {
            var result = await _runtimeApiClient.SetActiveProfileAsync(selected.ProfileId, CancellationToken.None);
            AppendTranscript($"[system] {result.Message}");
            await RefreshProfilesAsync();
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Active profile update failed: " + ex.Message);
        }
    }

    private async void SetActivePersonalityButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_runtimeApiClient is null || PersonalitiesList.SelectedItem is not PersonalityListItemViewModel selected)
        {
            return;
        }

        try
        {
            var result = await _runtimeApiClient.SetActivePersonalityAsync(selected.Id, CancellationToken.None);
            AppendTranscript($"[system] {result.Message}");
            await RefreshProfilesAsync();
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Active personality update failed: " + ex.Message);
        }
    }
    private async void CopyLastAssistantButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastAssistantMessage))
        {
            AppendTranscript("[system] Nothing to copy yet.");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            AppendTranscript("[error] Clipboard is unavailable on this platform.");
            return;
        }

        await clipboard.SetTextAsync(_lastAssistantMessage);
        AppendTranscript("[system] Copied last assistant message.");
    }

    private void RetryLastPromptButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastUserPrompt))
        {
            AppendTranscript("[system] Nothing to retry yet.");
            return;
        }

        PromptBox.Text = _lastUserPrompt;
        PromptBox.CaretIndex = _lastUserPrompt.Length;
        SendButton_Click(sender, e);
    }

    private async void ReadAloudButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readAloudActive)
        {
            await RequestVoiceCancelAsync("read aloud button");
            return;
        }

        if (string.IsNullOrWhiteSpace(_lastAssistantMessage))
        {
            AppendTranscript("[system] Nothing to read aloud yet.");
            return;
        }

        using var readAloudCancellation = new CancellationTokenSource();
        _readAloudCancellation = readAloudCancellation;
        _readAloudActive = true;
        MarkReadAloudStarted(_lastAssistantMessage.Length);

        try
        {
            await _ttsPlaybackService.SpeakAsync(_lastAssistantMessage, readAloudCancellation.Token);
            MarkReadAloudCompleted(_lastAssistantMessage.Length);
            AppendTranscript("[system] Read aloud complete.");
        }
        catch (OperationCanceledException) when (readAloudCancellation.IsCancellationRequested)
        {
            MarkPushToTalkCanceled(
                headline: "Read aloud canceled.",
                detail: "Local Windows speech playback was stopped before completion.");
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Read aloud failed: " + ex.Message);
            MarkPushToTalkFailure("Read aloud failed.", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_readAloudCancellation, readAloudCancellation))
            {
                _readAloudCancellation = null;
            }

            _readAloudActive = false;
            ReadAloudButton.Content = "Read Aloud";
        }
    }
private void ShowSourcesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastAssistantSources.Count == 0 && !string.IsNullOrWhiteSpace(_lastAssistantMessage))
        {
            _lastAssistantSources = ExtractUrls(_lastAssistantMessage);
        }

        var sources = _lastAssistantSources;
        if (sources.Count == 0)
        {
            AppendTranscript("[system] No source URLs detected in the last assistant response.");
            return;
        }

        AppendTranscript("[system] Sources from last assistant response:");
        foreach (var url in sources)
        {
            AppendTranscript("[source] " + url);
        }
    }

    // ── Per-message context menu handlers ────────────────────────────

    private async void CopyMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        if (menuItem.Parent is not ContextMenu ctx) return;
        var msg = (ctx.DataContext ?? (ctx.PlacementTarget as Control)?.DataContext) as ChatMessageItem;
        if (msg is null || string.IsNullOrWhiteSpace(msg.Content)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            AppendTranscript("[error] Clipboard is unavailable on this platform.");
            return;
        }

        await clipboard.SetTextAsync(msg.Content);
        AppendTranscript("[system] Copied message to clipboard.");
    }

    private void RetryMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        if (menuItem.Parent is not ContextMenu ctx) return;
        var msg = (ctx.DataContext ?? (ctx.PlacementTarget as Control)?.DataContext) as ChatMessageItem;
        if (msg is null) return;

        var prompt = !string.IsNullOrWhiteSpace(msg.RetryPrompt) ? msg.RetryPrompt : _lastUserPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AppendTranscript("[system] Nothing to retry.");
            return;
        }

        PromptBox.Text = prompt;
        PromptBox.CaretIndex = prompt.Length;
        SendButton_Click(sender, e);
    }

    private async void ReadAloudMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (sender is not MenuItem menuItem) return;
        if (menuItem.Parent is not ContextMenu ctx) return;
        var msg = (ctx.DataContext ?? (ctx.PlacementTarget as Control)?.DataContext) as ChatMessageItem;
        if (msg is null || string.IsNullOrWhiteSpace(msg.Content)) return;

        if (_readAloudActive)
        {
            await RequestVoiceCancelAsync("read aloud message context");
            return;
        }

        using var readAloudCancellation = new CancellationTokenSource();
        _readAloudCancellation = readAloudCancellation;
        _readAloudActive = true;
        MarkReadAloudStarted(msg.Content.Length);

        try
        {
            await _ttsPlaybackService.SpeakAsync(msg.Content, readAloudCancellation.Token);
            MarkReadAloudCompleted(msg.Content.Length);
            AppendTranscript("[system] Read aloud complete.");
        }
        catch (OperationCanceledException) when (readAloudCancellation.IsCancellationRequested)
        {
            MarkPushToTalkCanceled("Read aloud canceled.", "Local speech playback was stopped.");
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Read aloud failed: " + ex.Message);
            MarkPushToTalkFailure("Read aloud failed.", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_readAloudCancellation, readAloudCancellation))
                _readAloudCancellation = null;
            _readAloudActive = false;
            ReadAloudButton.Content = "Read Aloud";
        }
    }

    // ── Audit log file / folder handlers ─────────────────────────────

    private void OpenAuditLogFile_Click(object? sender, RoutedEventArgs e)
    {
        var path = SirThaddeus.AuditLog.JsonLineAuditLogger.GetDefaultPath();
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
        var path = SirThaddeus.AuditLog.JsonLineAuditLogger.GetDefaultPath();
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

    private async void AttachFileButton_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            AppendTranscript("[error] File picker is unavailable on this platform.");
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach a document",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported Documents")
                {
                    Patterns = ["*.txt", "*.csv", "*.md", "*.html", "*.htm", "*.json", "*.log"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        try
        {
            var content = await ReadAttachmentTextAsync(file, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(content))
            {
                AppendTranscript("[system] Selected file is empty.");
                return;
            }

            if (content.Length > 200_000)
            {
                content = content[..200_000];
                AppendTranscript("[system] Attachment was trimmed to 200,000 characters.");
            }

            _attachedDocument = new AttachedDocumentContext(file.Name, content);
            UpdateAttachmentUi();
            AppendTranscript($"[system] Attached file ready: {file.Name}");
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Attachment failed: " + ex.Message);
        }
    }

    private void RemoveAttachmentButton_Click(object? sender, RoutedEventArgs e)
    {
        _attachedDocument = null;
        UpdateAttachmentUi();
    }

    private void UpdateAttachmentUi()
    {
        if (_attachedDocument is null)
        {
            AttachmentChip.IsVisible = false;
            AttachmentNameText.Text = string.Empty;
            AttachmentMetaText.Text = string.Empty;
            return;
        }

        AttachmentChip.IsVisible = true;
        AttachmentNameText.Text = _attachedDocument.FileName;
        AttachmentMetaText.Text = _attachedDocument.IsSmall
            ? $"{_attachedDocument.RawContent.Length:N0} chars (inline)"
            : $"{_attachedDocument.RawContent.Length:N0} chars (context excerpts)";
    }

    private static async Task<string> ReadAttachmentTextAsync(IStorageFile file, CancellationToken cancellationToken)
    {
        if (file.TryGetLocalPath() is { Length: > 0 } path && File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static IReadOnlyList<string> ExtractUrls(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var urls = new List<string>();
        var tokens = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var trimmed = token.Trim(',', '.', ';', ')', ']', '}', '>');
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!urls.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(trimmed);
            }
        }

        return urls;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength].TrimEnd() + "...";
    }
    private void PromptBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateComposerState();
    }

    private void PromptBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!ShouldSendPromptOnKeyDown(e))
        {
            return;
        }

        e.Handled = true;
        SendButton_Click(sender, new RoutedEventArgs());
    }

    private bool ShouldSendPromptOnKeyDown(KeyEventArgs e)
    {
        if (!_uiSettings.SendOnEnter)
        {
            return false;
        }

        if (e.Key is not (Key.Enter or Key.Return))
        {
            return false;
        }

        return !e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
               !e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
               !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
    }

    private void UpdateComposerState()
    {
        var hasPrompt = !string.IsNullOrWhiteSpace(PromptBox.Text);
        var runActive = !string.IsNullOrWhiteSpace(_activeRunId);
        SendButton.IsEnabled = hasPrompt && !runActive;
        SendButton.IsVisible = !runActive;
        StopButton.IsEnabled = runActive;
        StopButton.IsVisible = runActive;
    }

    private void UpdateChatActionState()
    {
        var hasMessages = _currentSession.Messages.Count > 0;
        ChatActionBar.IsVisible = hasMessages;

        var hasAssistantMessage = !string.IsNullOrWhiteSpace(_lastAssistantMessage);
        CopyLastAssistantButton.IsEnabled = hasAssistantMessage;
        RetryLastPromptButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastUserPrompt);
        SourcesButton.IsEnabled = hasAssistantMessage;
        ReadAloudButton.IsEnabled = OperatingSystem.IsWindows() && hasAssistantMessage;
    }

    private void SyncLastMessageCacheFromCurrentSession()
    {
        _lastUserPrompt = _currentSession.Messages.LastOrDefault(m => m.Role == "user")?.Content;
        _lastAssistantMessage = _currentSession.Messages.LastOrDefault(m => m.Role == "assistant" && !m.IsPending)?.Content;
        _lastAssistantSources = string.IsNullOrWhiteSpace(_lastAssistantMessage)
            ? Array.Empty<string>()
            : ExtractUrls(_lastAssistantMessage);
    }

    private void UpdateHeaderConnectionControls()
    {
        ConnectButton.IsVisible = _runtimeApiClient is null;
        ConnectButton.IsEnabled = !_isConnecting;
    }
    private sealed class MemoryFactRowViewModel
    {
        public string MemoryId { get; init; }
        public string? ProfileId { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public string Object { get; set; }
        public double Confidence { get; set; }
        public string? SourceRef { get; set; }

        public MemoryFactRowViewModel(MemoryFactItemDto dto)
        {
            MemoryId = dto.MemoryId;
            ProfileId = dto.ProfileId;
            Subject = dto.Subject;
            Predicate = dto.Predicate;
            Object = dto.Object;
            Confidence = dto.Confidence;
            SourceRef = dto.SourceRef;
        }

        public SaveMemoryFactRequest ToSaveRequest() =>
            new SaveMemoryFactRequest(ProfileId, Subject, Predicate, Object, Confidence, SourceRef);
    }

    private sealed class MemoryEventRowViewModel
    {
        public string EventId { get; init; }
        public string? ProfileId { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string? Summary { get; set; }
        public DateTimeOffset? WhenUtc { get; set; }
        public double Confidence { get; set; }
        public string? SourceRef { get; set; }

        public MemoryEventRowViewModel(MemoryEventItemDto dto)
        {
            EventId = dto.EventId;
            ProfileId = dto.ProfileId;
            Type = dto.Type;
            Title = dto.Title;
            Summary = dto.Summary;
            WhenUtc = dto.WhenUtc;
            Confidence = dto.Confidence;
            SourceRef = dto.SourceRef;
        }

        public SaveMemoryEventRequest ToSaveRequest() =>
            new SaveMemoryEventRequest(ProfileId, Type, Title, Summary, WhenUtc, Confidence, SourceRef);
    }

    private sealed class MemoryChunkRowViewModel
    {
        public string ChunkId { get; init; }
        public string SourceType { get; set; }
        public string? SourceRef { get; set; }
        public string Text { get; set; }
        public DateTimeOffset? WhenUtc { get; set; }

        public MemoryChunkRowViewModel(MemoryChunkItemDto dto)
        {
            ChunkId = dto.ChunkId;
            SourceType = dto.SourceType;
            SourceRef = dto.SourceRef;
            Text = dto.Text;
            WhenUtc = dto.WhenUtc;
        }

        public SaveMemoryChunkRequest ToSaveRequest() =>
            new SaveMemoryChunkRequest(SourceType, Text, WhenUtc, SourceRef);
    }

    private sealed class MemoryNuggetRowViewModel
    {
        public string NuggetId { get; init; }
        public string Text { get; set; }
        public string? Tags { get; set; }
        public double Weight { get; set; }
        public int PinLevel { get; set; }

        public MemoryNuggetRowViewModel(MemoryNuggetItemDto dto)
        {
            NuggetId = dto.NuggetId;
            Text = dto.Text;
            Tags = dto.Tags;
            Weight = dto.Weight;
            PinLevel = dto.PinLevel;
        }

        public SaveMemoryNuggetRequest ToSaveRequest() =>
            new SaveMemoryNuggetRequest(Text, Tags, Weight, PinLevel);
    }

    private sealed class ProfileListItemViewModel
    {
        public ProfileListItemViewModel(ProfileListItemDto dto)
        {
            ProfileId = dto.ProfileId;
            IsActive = dto.IsActive;

            var displayName = dto.PreferredName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = dto.DisplayName;
            }

            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? dto.ProfileId
                : displayName;

            Meta = $"Id: {dto.ProfileId} | Kind: {dto.Kind} | Active: {(dto.IsActive ? "yes" : "no")}";
        }

        public string ProfileId { get; }

        public string DisplayName { get; }

        public string Meta { get; }

        public bool IsActive { get; }
    }

    private sealed class PersonalityListItemViewModel
    {
        public PersonalityListItemViewModel(PersonalityListItemDto dto)
        {
            Id = dto.Id;
            IsActive = dto.IsActive;
            DisplayName = string.IsNullOrWhiteSpace(dto.Alias)
                ? dto.DisplayName
                : $"{dto.Alias} ({dto.DisplayName})";
            Meta = $"Id: {dto.Id} | Active: {(dto.IsActive ? "yes" : "no")}";
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Meta { get; }

        public bool IsActive { get; }
    }

    // ---------------------------------------------------------------
    // Thought / Reasoning Extraction (ported from WPF)
    // ---------------------------------------------------------------

    private static readonly Regex TaggedThinkingRegex = new(
        @"<(?<tag>think|thinking|reasoning)>(?<body>[\s\S]*?)</\k<tag>>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumberedReasoningLeadRegex = new(
        @"^\d+[\.\)]\s*(analy(?:ze|sis)?|reason(?:ing)?|think(?:ing)?|thought|consult|plan|approach|breakdown)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumberedLineRegex = new(
        @"^\d+[\.\)]\s+",
        RegexOptions.Compiled);

    private sealed record AssistantDisplayParts(string DisplayText, string ThinkingText);

    private static AssistantDisplayParts ParseAssistantDisplayParts(string text)
    {
        var cleaned = CleanLlmOutput(text);
        if (string.IsNullOrWhiteSpace(cleaned))
            return new AssistantDisplayParts(cleaned, "");

        if (TryExtractTaggedThinking(cleaned, out var taggedDisplay, out var taggedThinking))
            return new AssistantDisplayParts(taggedDisplay, taggedThinking);

        if (TryExtractStructuredThinkingPreamble(cleaned, out var structuredDisplay, out var structuredThinking))
            return new AssistantDisplayParts(structuredDisplay, structuredThinking);

        return new AssistantDisplayParts(cleaned, "");
    }

    private static string CleanLlmOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var lines = text.Split('\n');
        var cleaned = lines
            .Where(line =>
            {
                var trimmed = line.Trim();
                if (IsLikelyInternalMarkerLine(trimmed))
                    return false;

                if (trimmed.StartsWith('[') && trimmed.EndsWith(']') &&
                    (trimmed.Contains("END OF", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("INSTRUCTIONS", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("REFERENCE DATA", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("ASSISTANT RESPONSE", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("[Action:", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("[action:", StringComparison.OrdinalIgnoreCase)))
                    return false;

                return true;
            });

        return string.Join('\n', cleaned).Trim();
    }

    private static bool IsLikelyInternalMarkerLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        if (!line.StartsWith('[') || !line.EndsWith(']'))
            return false;

        var marker = line[1..^1].Trim();
        if (marker.StartsWith("/", StringComparison.Ordinal))
            marker = marker[1..].Trim();

        if (string.IsNullOrWhiteSpace(marker))
            return false;

        if (marker.Contains("TOOL", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("INSTRUCTION", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("PROFILE", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("MEMORY", StringComparison.OrdinalIgnoreCase))
            return true;

        var normalized = marker.Replace("/", "", StringComparison.Ordinal).Trim();
        if (normalized.Length == 0)
            return false;

        return normalized.All(c =>
            char.IsUpper(c) || char.IsDigit(c) || c == '_' || c == '-' || c == ' ');
    }

    private static bool TryExtractTaggedThinking(
        string text, out string displayText, out string thinkingText)
    {
        displayText = text;
        thinkingText = "";

        var match = TaggedThinkingRegex.Match(text);
        if (!match.Success)
            return false;

        var thought = match.Groups["body"].Value.Trim();
        var visible = text.Remove(match.Index, match.Length).Trim();

        if (string.IsNullOrWhiteSpace(visible) || string.IsNullOrWhiteSpace(thought))
            return false;

        displayText = visible;
        thinkingText = thought;
        return true;
    }

    private static bool TryExtractStructuredThinkingPreamble(
        string text, out string displayText, out string thinkingText)
    {
        displayText = text;
        thinkingText = "";

        var normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var start = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (start < 0)
            return false;

        var lead = lines[start].Trim();
        if (!LooksLikeThinkingLead(lead))
            return false;

        var sawReasoningLine = false;
        var splitIndex = -1;

        for (var i = start; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (sawReasoningLine)
                    continue;
                continue;
            }

            if (IsReasoningLine(trimmed))
            {
                sawReasoningLine = true;
                continue;
            }

            if (sawReasoningLine)
            {
                splitIndex = i;
                break;
            }

            return false;
        }

        if (!sawReasoningLine || splitIndex <= start || splitIndex >= lines.Length)
            return false;

        var thought = string.Join('\n', lines[start..splitIndex]).Trim();
        var visible = string.Join('\n', lines[splitIndex..]).Trim();
        if (string.IsNullOrWhiteSpace(thought) || string.IsNullOrWhiteSpace(visible))
            return false;

        displayText = visible;
        thinkingText = thought;
        return true;
    }

    /// <summary>
    /// Strips common Markdown formatting (bold, italic, inline code) from LLM output
    /// so the plain TextBox displays clean, readable text.
    /// </summary>
    private static string StripMarkdownFormatting(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // **bold** and __bold__
        text = MarkdownBoldRegex.Replace(text, "$1");
        text = MarkdownUnderscoreBoldRegex.Replace(text, "$1");

        // *italic* and _italic_ (single markers, only if not inside a word)
        text = Regex.Replace(text, @"(?<!\w)\*([^*]+?)\*(?!\w)", "$1");
        text = Regex.Replace(text, @"(?<!\w)_([^_]+?)_(?!\w)", "$1");

        // `inline code` — just remove the backticks
        text = Regex.Replace(text, @"`([^`]+?)`", "$1");

        // ### Headings — strip leading hashes
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);

        return text;
    }

    private static bool LooksLikeThinkingLead(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var lower = line.ToLowerInvariant();
        return lower.StartsWith("thought for ", StringComparison.Ordinal) ||
               lower.StartsWith("analysis:", StringComparison.Ordinal) ||
               lower.StartsWith("reasoning:", StringComparison.Ordinal) ||
               lower.StartsWith("thinking:", StringComparison.Ordinal) ||
               lower.StartsWith("let me think", StringComparison.Ordinal) ||
               NumberedReasoningLeadRegex.IsMatch(line);
    }

    private static bool IsReasoningLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        var trimmed = line.Trim();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            trimmed.StartsWith("* ", StringComparison.Ordinal) ||
            trimmed.StartsWith("\u2022 ", StringComparison.Ordinal) ||
            NumberedLineRegex.IsMatch(trimmed))
            return true;

        if (trimmed.EndsWith(':') && trimmed.Length <= 120)
            return true;

        var lower = trimmed.ToLowerInvariant();
        return lower.Contains("analyze", StringComparison.Ordinal) ||
               lower.Contains("analysis", StringComparison.Ordinal) ||
               lower.Contains("reasoning", StringComparison.Ordinal) ||
               lower.Contains("consult memory", StringComparison.Ordinal) ||
               lower.Contains("step-by-step", StringComparison.Ordinal);
    }


}
