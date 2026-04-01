using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using FluentIcons.Avalonia;
using FluentIcons.Common;
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
    private bool _submitInProgress;
    private AttachedDocumentContext? _attachedDocument;
    private string? _lastUserPrompt;
    private string? _pendingUserPrompt;
    private string? _lastAssistantMessage;
    private IReadOnlyList<string> _lastAssistantSources = Array.Empty<string>();
    private readonly Dictionary<string, StringBuilder> _assistantBuffersByRunId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _toolCallsInCurrentRun = new();
    private readonly ObservableCollection<WorkflowChecklistItemViewModel> _workflowChecklistItems = [];
    private string? _lastWorkflowNarration;
    private string? _lastWorkflowChecklistStamp;
    private string? _workflowConfidenceBand;
    private int _workflowRetryCount;
    private CancellationTokenSource? _progressDrawerAutoCollapseCancellation;
    private bool _voiceInitiatedRun;
    private static readonly TimeSpan MarkdownRegexTimeout = TimeSpan.FromMilliseconds(75);
    private static readonly Regex MarkdownBoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex MarkdownUnderscoreBoldRegex = new(@"__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex MarkdownItalicAsteriskRegex = new(@"(?<!\w)\*([^*\r\n]+)\*(?!\w)", RegexOptions.Compiled, MarkdownRegexTimeout);
    private static readonly Regex MarkdownItalicUnderscoreRegex = new(@"(?<!\w)_([^_\r\n]+)_(?!\w)", RegexOptions.Compiled, MarkdownRegexTimeout);
    private static readonly Regex MarkdownInlineCodeRegex = new(@"`([^`\r\n]+)`", RegexOptions.Compiled, MarkdownRegexTimeout);
    private static readonly Regex MarkdownHeadingRegex = new(@"^#{1,6}\s+", RegexOptions.Compiled | RegexOptions.Multiline, MarkdownRegexTimeout);
    private readonly LocalTextToSpeechPlaybackService _ttsPlaybackService;
    private readonly IMicrophoneCaptureService _microphoneCaptureService = new NAudioMicrophoneCaptureService();
    private readonly VoiceHostLauncher _voiceHostLauncher = new();
    private readonly LocalAsrHttpTranscriptionService _transcriptionService;
    private CancellationTokenSource? _voiceHostLifecycleCancellation;
    private readonly SemaphoreSlim _pttGate = new(1, 1);
    private bool _pttCaptureActive;
    private bool _pttHotkeyDown;
    private bool _pttInterruptTapArmed;
    private int _pttSessionCounter;

    private readonly ObservableCollection<MemoryFactRowViewModel> _memoryFacts = [];
    private readonly ObservableCollection<MemoryEventRowViewModel> _memoryEvents = [];
    private readonly ObservableCollection<MemoryChunkRowViewModel> _memoryChunks = [];
    private readonly ObservableCollection<MemoryNuggetRowViewModel> _memoryNuggets = [];
    private readonly ObservableCollection<ProfileListItemViewModel> _profileItems = [];
    private readonly ObservableCollection<PersonalityListItemViewModel> _personalityItems = [];

    private readonly ObservableCollection<ChatSessionItem> _chatHistory = [];
    private readonly ObservableCollection<SuggestionChipItem> _suggestionChips = [];
    private readonly ObservableCollection<RuntimeStatusItem> _runtimeStatusItems = [];
    private readonly ObservableCollection<RecentActivityItem> _recentActivityItems = [];
    private readonly Dictionary<string, PendingPermissionAuditContext> _pendingPermissionAudit = new(StringComparer.OrdinalIgnoreCase);
    private readonly ViewModels.ActivityDrawerViewModel _activityDrawerVm = new();
    private ChatSessionItem _currentSession;
    private string _voiceStatusLabel = "Ready";
    private string? _lastRuntimeActivityStamp;
    private string? _lastActionDrawerAuditSignature;
    private TabItem? _lastValidSettingsTabItem;

    private TextBox PromptBox => ChatComposer.PromptBox;
    private Button SendButton => ChatComposer.SendButton;
    private Button StopButton => ChatComposer.StopButton;
    private Button PttHoldButton => ChatComposer.PttHoldButton;
    private Button AttachFileButton => ChatComposer.AttachFileButton;
    private Button RemoveAttachmentButton => ChatComposer.RemoveAttachmentButton;
    private Border AttachmentChip => ChatComposer.AttachmentChip;
    private TextBlock AttachmentNameText => ChatComposer.AttachmentNameText;
    private TextBlock AttachmentMetaText => ChatComposer.AttachmentMetaText;

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
        SettingsTabControl.SelectedItem ??= GeneralTabItem;
        _lastValidSettingsTabItem = GeneralTabItem;

        LlmsScrollViewer.DataContext = _backendSettings;
        AudioScrollViewer.DataContext = _backendSettings;
        PermissionsTabItem.DataContext = _backendSettings;
        ConstraintsPanel.DataContext = _backendSettings;
        _backendSettings.PropertyChanged += BackendSettings_PropertyChanged;
        _ttsPlaybackService = new LocalTextToSpeechPlaybackService(
            () => _backendSettings.VoiceHostBaseUrl,
            () => _backendSettings.GetVoiceSettingsSnapshot());
        _transcriptionService = new LocalAsrHttpTranscriptionService(() => _backendSettings.VoiceHostBaseUrl);
        ApplyAudioPreferences();
        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Add(_currentSession);
        ChatHistoryList.ItemsSource = _chatHistory;
        ChatMessagesList.ItemsSource = _currentSession.Messages;
        SuggestionChipsPanel.ItemsSource = _suggestionChips;
        RuntimeStatusStrip.ItemsSource = _runtimeStatusItems;
        CategoryList.ItemsSource = _activityDrawerVm.Categories;
        ConnectionList.ItemsSource = _activityDrawerVm.Connections;
        InitializeSuggestionChips();
        WorkflowChecklistList.ItemsSource = _workflowChecklistItems;

        AttachFileButton.Click += AttachFileButton_Click;
        RemoveAttachmentButton.Click += RemoveAttachmentButton_Click;
        SendButton.Click += SendButton_Click;
        StopButton.Click += StopButton_Click;
        PromptBox.TextChanged += PromptBox_TextChanged;
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
        UpdateLandingEmptyStateVisibility();
        UpdateConversationTitle();

        if (!OperatingSystem.IsWindows())
        {
            PttHoldButton.IsEnabled = false;
            SetPushToTalkPlatformUnavailable();
        }

        SyncLastMessageCacheFromCurrentSession();
        UpdateComposerState();
        UpdateChatActionState();
        UpdateHeaderConnectionControls();
        UpdateRuntimeStatusStrip();
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

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.PttChord), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.ShutupChord), StringComparison.Ordinal))
        {
            TryStartGlobalPushToTalkHotkey();
            SetPushToTalkReadyState();
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

        CancelProgressDrawerAutoCollapse();

        _runtimeHttpClient?.Dispose();
        _runtimeHttpClient = null;
        _runtimeApiClient = null;

        _runtimeLauncher.Dispose();
        DisposePushToTalkUi();
        _ttsPlaybackService.Dispose();
        _transcriptionService.Dispose();
        _microphoneCaptureService.Dispose();
        _pttGate.Dispose();

        if (_backendSettings.IsDirty)
        {
            try
            {
                SettingsManager.Save(_backendSettings.BuildPersistableSnapshot());
            }
            catch
            {
                // Best effort only on shutdown.
            }
        }

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
        BeginVoiceHostLifecycleTransition(_backendSettings.VoiceHostEnabled);

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

    private async Task SaveBackendSettingsAsync(
        string connectedStatus,
        string localStatus,
        string localHealthStatus,
        string syncFailureStatus,
        bool appendTranscript)
    {
        var snapshot = _backendSettings.BuildPersistableSnapshot();
        AppSettings localPersisted;

        try
        {
            SettingsManager.Save(snapshot);
            localPersisted = SettingsManager.Load();

            if (_runtimeApiClient is null)
            {
                _backendSettings.ApplySavedSnapshot(localPersisted, localStatus);
                _backendSettings.ResetSearchHealthState(
                    "Not connected",
                    localHealthStatus);
                if (appendTranscript)
                {
                    AppendTranscript("[system] " + localStatus);
                }

                return;
            }

            try
            {
                var persisted = await _runtimeApiClient.SaveSettingsAsync(localPersisted, CancellationToken.None);
                SettingsManager.Save(persisted);
                var syncedPersisted = SettingsManager.Load();
                _backendSettings.ApplySavedSnapshot(syncedPersisted, connectedStatus);
                await RefreshSearchStatusAsync();
                if (appendTranscript)
                {
                    AppendTranscript("[system] " + connectedStatus);
                }
            }
            catch (Exception ex)
            {
                _backendSettings.ApplySavedSnapshot(localPersisted, syncFailureStatus);
                _backendSettings.ResetSearchHealthState("Unavailable", syncFailureStatus);
                if (appendTranscript)
                {
                    AppendTranscript("[error] Runtime settings sync failed: " + ex.Message);
                    AppendTranscript("[system] " + syncFailureStatus);
                }
            }
        }
        catch (Exception ex)
        {
            _backendSettings.SetStatus("Settings save failed: " + ex.Message);
            if (appendTranscript)
            {
                AppendTranscript("[error] Settings save failed: " + ex.Message);
            }
        }
    }

    private async void SaveSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        await SaveBackendSettingsAsync(
            connectedStatus: "Settings saved and applied to the connected runtime.",
            localStatus: "Settings saved locally. Connect or restart the runtime to apply them.",
            localHealthStatus: "Settings saved locally. Connect the runtime to inspect live web-search and MCP health.",
            syncFailureStatus: "Settings saved locally. Runtime sync failed; reconnect to apply them.",
            appendTranscript: true);
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

    private async void AddAllowedFileRootButton_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            _backendSettings.SetStatus("Folder picker is unavailable on this platform.");
            AppendTranscript("[error] Folder picker is unavailable on this platform.");
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an allowed folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.TryGetLocalPath() is not { Length: > 0 } path)
            return;

        var previousCount = _backendSettings.AllowedFileRoots.Count;
        _backendSettings.AddAllowedFileRoot(path);
        if (_backendSettings.AllowedFileRoots.Count == previousCount)
            return;

        await SaveBackendSettingsAsync(
            connectedStatus: "File access settings saved and applied to the connected runtime.",
            localStatus: "File access settings saved locally.",
            localHealthStatus: "File access settings saved locally. Connect the runtime to inspect live web-search and MCP health.",
            syncFailureStatus: "File access settings saved locally. Runtime sync failed; reconnect to apply them.",
            appendTranscript: false);
    }

    private async void RemoveAllowedFileRootButton_Click(object? sender, RoutedEventArgs e)
    {
        if (AllowedFileRootsList.SelectedItem is string path)
        {
            var previousCount = _backendSettings.AllowedFileRoots.Count;
            _backendSettings.RemoveAllowedFileRoot(path);
            if (_backendSettings.AllowedFileRoots.Count == previousCount)
                return;

            await SaveBackendSettingsAsync(
                connectedStatus: "File access settings saved and applied to the connected runtime.",
                localStatus: "File access settings saved locally.",
                localHealthStatus: "File access settings saved locally. Connect the runtime to inspect live web-search and MCP health.",
                syncFailureStatus: "File access settings saved locally. Runtime sync failed; reconnect to apply them.",
                appendTranscript: false);
        }
    }

    private async void DisableAllFileAccessCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (!_backendSettings.IsDirty)
            return;

        await SaveBackendSettingsAsync(
            connectedStatus: "File access settings saved and applied to the connected runtime.",
            localStatus: "File access settings saved locally.",
            localHealthStatus: "File access settings saved locally. Connect the runtime to inspect live web-search and MCP health.",
            syncFailureStatus: "File access settings saved locally. Runtime sync failed; reconnect to apply them.",
            appendTranscript: false);
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
        UpdateLandingEmptyStateVisibility();
    }

    private void SettingsTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabControl)
        {
            return;
        }

        if (tabControl.SelectedItem is not TabItem selectedTab ||
            !selectedTab.IsEnabled ||
            selectedTab.Classes.Contains("navGroupHeader"))
        {
            var fallbackTab = _lastValidSettingsTabItem ?? GeneralTabItem;
            if (!ReferenceEquals(tabControl.SelectedItem, fallbackTab))
            {
                tabControl.SelectedItem = fallbackTab;
            }

            return;
        }

        _lastValidSettingsTabItem = selectedTab;

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
            IsConfiguredHotkeyDown(e, _backendSettings.ShutupChord))
        {
            e.Handled = true;
            _ = RequestVoiceCancelAsync("window cancel hotkey");
            return;
        }

        if (ShouldUseWindowScopedPttHotkey() &&
            IsConfiguredHotkeyDown(e, _backendSettings.PttChord))
        {
            e.Handled = true;

            if (IsVoiceResponseActive())
            {
                _ = RequestVoiceCancelAsync("window ptt interrupt hotkey");
                return;
            }

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
        if (ShouldUseWindowScopedPttHotkey() &&
            _pttHotkeyDown &&
            IsConfiguredHotkeyTriggerKey(e.Key, _backendSettings.PttChord))
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
            ProgressDrawer.IsVisible = false;
        }
    }

    private void ToggleActionDrawer(bool show)
    {
        ActionDrawer.IsVisible = show;
        if (show)
        {
            ConversationDrawer.IsVisible = false;
            ProgressDrawer.IsVisible = false;
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
        UpdateLandingEmptyStateVisibility();
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
        UpdateLandingEmptyStateVisibility();
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
        _pendingUserPrompt = null;
        _lastAssistantMessage = null;
        _lastAssistantSources = Array.Empty<string>();

        // Clear runtime session-level permission grants so "Allow for session"
        // doesn't carry over to a brand-new conversation.
        _ = _runtimeApiClient?.ClearSessionAsync(CancellationToken.None);

        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Insert(0, _currentSession);
        ChatHistoryList.SelectedItem = _currentSession;

        // Reset the trust-ledger drawer for the new session.
        _activityDrawerVm.Clear();
        SessionSummaryText.Text = _activityDrawerVm.SessionSummaryText;
        SessionTimeRangeText.Text = "";

        ChatMessagesList.ItemsSource = _currentSession.Messages;
        UpdateLandingEmptyStateVisibility();

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
        if (_submitInProgress)
        {
            return;
        }

        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            UpdateComposerState();
            return;
        }

        _submitInProgress = true;
        UpdateComposerState();
        try
        {
            // Don't clear PromptBox yet Ã¢â‚¬â€œ SubmitPromptAsync will clear it on
            // success, or leave the text visible as a pending prompt when offline.
            await SubmitPromptAsync(prompt, voiceInitiated: false);
        }
        finally
        {
            _submitInProgress = false;
            UpdateComposerState();
        }
    }

    /// <summary>
    /// Core submission logic used by both the Send button and PTT auto-submit.
    /// </summary>
    private async Task SubmitPromptAsync(string prompt, bool voiceInitiated)
    {
        var connected = await EnsureRuntimeConnectedAsync(
            allowStartRuntime: _uiSettings.AutoStartRuntime,
            appendTranscriptOnFailure: true);
        if (!connected || _runtimeApiClient is null)
        {
            // Store the prompt so it can be auto-sent when the runtime connects.
            _pendingUserPrompt = prompt;
            UpdateComposerState();
            return;
        }

        // Connection confirmed Ã¢â‚¬â€œ clear any pending prompt and the input box.
        _pendingUserPrompt = null;
        PromptBox.Text = string.Empty;

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
            _voiceInitiatedRun = voiceInitiated;
            ResetWorkflowProgressUi();

            // Snapshot prior conversation turns so the runtime can seed the
            // orchestrator's sliding-window history for multi-turn context.
            var priorMessages = _currentSession.Messages
                .Where(m => !m.IsPending
                    && (m.Role is "user" or "assistant")
                    && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => new ChatHistoryMessage(m.Role, m.Content))
                .ToList();

            AppendTranscript($"[user] {prompt}");
            var run = await _runtimeApiClient.StartRunAsync(
                runtimePrompt,
                CancellationToken.None,
                _currentSession.ConversationId,
                priorMessages.Count > 0 ? priorMessages : null);
            _activeRunId = run.RunId;

            if (voiceInitiated)
            {
                SetVoiceChatStatus("Responding...");
            }

            _assistantBuffersByRunId[run.RunId] = new StringBuilder();
            _currentSession.AddPendingAssistantMessage();
            UpdateComposerState();
            StartEventStream(run.RunId);
            UpdateComposerState();
        }
        catch (Exception ex)
        {
            _voiceInitiatedRun = false;
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

        if (IsVoiceResponseActive())
        {
            _pttInterruptTapArmed = true;
            e.Handled = true;
            _ = RequestVoiceCancelAsync("button tap interrupt");
            return;
        }

        e.Pointer.Capture(PttHoldButton);
        e.Handled = true;
        _ = BeginPushToTalkAsync("button");
    }

    private void PttHoldButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pttInterruptTapArmed)
        {
            _pttInterruptTapArmed = false;
            e.Handled = true;
            return;
        }

        if (e.Pointer.Captured == PttHoldButton)
        {
            e.Pointer.Capture(null);
        }

        e.Handled = true;
        _ = EndPushToTalkAsync("button");
    }

    private void PttHoldButton_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_pttInterruptTapArmed)
        {
            _pttInterruptTapArmed = false;
            return;
        }

        _ = EndPushToTalkAsync("capture_lost");
    }

    private async Task BeginPushToTalkAsync(string source)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Pressing PTT while a response is active acts as a shutup interrupt.
        if (IsVoiceResponseActive())
        {
            await RequestVoiceCancelAsync($"{source} interrupt");
            return;
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
            // Button state CSS class already shows listening Ã¢â‚¬â€ no chat card needed.
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

            // Auto-submit the transcribed text (WPF parity).
            var fullPrompt = PromptBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(fullPrompt))
            {
                PromptBox.Text = string.Empty;
                // Await the transcription cleanup before submitting.
                await ClearPushToTalkTranscriptionAsync(transcriptionCancellation);
                transcriptionCancellation = null; // Prevent double-dispose in finally.
                await SubmitPromptAsync(fullPrompt, voiceInitiated: true);
                return;
            }
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
        OpenFullAuditFromActionDrawer();
    }

    private void OpenFullAuditFromActionDrawer()
    {
        SetActiveView(SettingsTabButton);
        SettingsTabControl.SelectedItem = AuditTabItem;
        ToggleActionDrawer(false);
    }

    private async Task RefreshActionDrawerAsync()
    {
        UpdateActionDrawerSummary();

        if (_runtimeApiClient is null)
            return;

        try
        {
            var summary = await _runtimeApiClient.GetActivitySummaryAsync(
                _currentSession.ConversationId, CancellationToken.None);

            if (summary is not null)
            {
                _activityDrawerVm.UpdateFromResponse(summary);
                SessionSummaryText.Text = _activityDrawerVm.SessionSummaryText;
                SessionTimeRangeText.Text = _activityDrawerVm.SessionTimeRange;
            }
        }
        catch
        {
            // Keep existing drawer state when runtime is unavailable.
        }
    }

    private void CategoryExpandButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string categoryKey)
        {
            foreach (var cat in _activityDrawerVm.Categories)
            {
                if (string.Equals(cat.CategoryKey, categoryKey, StringComparison.OrdinalIgnoreCase))
                {
                    cat.IsExpanded = !cat.IsExpanded;
                    break;
                }
            }
        }
    }

    private async void ApprovePermissionButton_Click(object? sender, RoutedEventArgs e)
    {
        await SubmitPermissionDecisionAsync(approved: true, rememberForSession: false, persistAsAlways: false);
    }

    private async void DenyPermissionButton_Click(object? sender, RoutedEventArgs e)
    {
        await SubmitPermissionDecisionAsync(approved: false, rememberForSession: false, persistAsAlways: false);
    }

    private async void AllowSessionButton_Click(object? sender, RoutedEventArgs e)
    {
        await SubmitPermissionDecisionAsync(approved: true, rememberForSession: true, persistAsAlways: false);
    }

    private async void AllowAlwaysButton_Click(object? sender, RoutedEventArgs e)
    {
        await SubmitPermissionDecisionAsync(approved: true, rememberForSession: false, persistAsAlways: true);
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
        var wasEmpty = _currentSession.Messages.Count == 0;

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
        else if (line.StartsWith("[status] "))
        {
            _currentSession.AddMessage("status", line[9..]);
        }
        else if (line.StartsWith("[error] "))
        {
            _currentSession.AddMessage("system", line);
        }
        else
        {
            _currentSession.AddMessage("system", line);
        }

        if (wasEmpty && _currentSession.Messages.Count > 0)
        {
            UpdateLandingEmptyStateVisibility();
        }

        BumpSessionToTop(_currentSession);
        SyncLastMessageCacheFromCurrentSession();
        UpdateChatActionState();
        UpdateComposerState();
        UpdateConversationTitle();
        ScrollChatToBottom();
    }

    private static readonly string[] PttStateClasses = ["pttListening", "pttProcessing", "pttSpeaking", "pttResponding"];

    private void SetVoiceChatStatus(string state, string? _detail = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(state) ? "Hold" : state.Trim();

        // Choose icon + CSS class based on state
        Symbol iconSymbol;
        string? cssClass;
        string brushKey;
        switch (trimmed)
        {
            case "Listening...":
                _voiceStatusLabel = "Listening";
                iconSymbol = Symbol.Mic;
                cssClass = "pttListening";
                brushKey = "AccentPrimary";
                break;
            case "Processing...":
            case "Transcribing...":
                _voiceStatusLabel = "Working";
                iconSymbol = Symbol.Scan;
                cssClass = "pttProcessing";
                brushKey = "TextSecondary";
                break;
            case "Speaking":
                _voiceStatusLabel = "Speaking";
                iconSymbol = Symbol.SpeakerSettings;
                cssClass = "pttSpeaking";
                brushKey = "TextSecondary";
                break;
            case "Responding...":
                _voiceStatusLabel = "Responding";
                iconSymbol = Symbol.Send;
                cssClass = "pttResponding";
                brushKey = "AccentPrimary";
                break;
            default: // "Hold" and fallback
                _voiceStatusLabel = PttHoldButton.IsEnabled ? "Ready" : "Unavailable";
                iconSymbol = Symbol.Mic;
                cssClass = null;
                brushKey = PttHoldButton.IsEnabled ? "TextSecondary" : "TextTertiary";
                break;
        }

        PttHoldButton.Content = new SymbolIcon
        {
            Symbol = iconSymbol,
            FontSize = 20,
            Foreground = ResolveThemeBrush(brushKey, Brushes.LightGray)
        };
        ToolTip.SetTip(PttHoldButton, string.Equals(trimmed, "Hold", StringComparison.Ordinal)
            ? "Hold to talk"
            : trimmed);

        // Toggle CSS classes instead of setting local Background/Foreground values
        foreach (var cls in PttStateClasses)
        {
            PttHoldButton.Classes.Set(cls, cls == cssClass);
        }

        UpdateRuntimeStatusStrip();
    }

    private bool IsVoiceResponseActive()
    {
        return _readAloudActive || !string.IsNullOrWhiteSpace(_activeRunId);
    }

    private static bool IsConfiguredHotkeyDown(KeyEventArgs e, string chord)
    {
        if (!TryParseUiChord(chord, out var triggerKey, out var modifiers))
        {
            return false;
        }

        return e.Key == triggerKey && ModifiersMatch(e.KeyModifiers, modifiers);
    }

    private static bool IsConfiguredHotkeyTriggerKey(Key key, string chord)
    {
        return TryParseUiChord(chord, out var triggerKey, out _) && key == triggerKey;
    }

    private static bool ModifiersMatch(KeyModifiers actual, KeyModifiers expected)
    {
        var flags = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta;
        return (actual & flags) == (expected & flags);
    }

    private static bool TryParseUiChord(string? chord, out Key triggerKey, out KeyModifiers modifiers)
    {
        triggerKey = Key.None;
        modifiers = KeyModifiers.None;

        if (string.IsNullOrWhiteSpace(chord))
        {
            return false;
        }

        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var token = parts[i];
            if (token.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Control;
            }
            else if (token.Equals("alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Alt;
            }
            else if (token.Equals("shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Shift;
            }
            else if (token.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("meta", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Meta;
            }
        }

        return TryParseUiKey(parts[^1], out triggerKey);
    }

    private static bool TryParseUiKey(string token, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var normalized = token.Trim();

        if ((normalized.StartsWith('F') || normalized.StartsWith('f')) &&
            int.TryParse(normalized[1..], out var fn) &&
            fn is >= 1 and <= 24)
        {
            key = (Key)((int)Key.F1 + (fn - 1));
            return true;
        }

        if (normalized.Length == 1)
        {
            var ch = char.ToUpperInvariant(normalized[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                key = Enum.Parse<Key>(ch.ToString(), ignoreCase: true);
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                key = (Key)((int)Key.D0 + (ch - '0'));
                return true;
            }
        }

        if (normalized.Equals("escape", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("esc", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Escape;
            return true;
        }

        if (normalized.Equals("space", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Space;
            return true;
        }

        if (normalized.Equals("enter", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Enter;
            return true;
        }

        if (normalized.Equals("tab", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Tab;
            return true;
        }

        if (normalized.Equals("backspace", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Back;
            return true;
        }

        return false;
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
        ConversationTitleText.Text = string.Empty;
        ConversationTitleText.IsVisible = false;
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
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await Dispatcher.UIThread.InvokeAsync(() => HandleEvent(envelope));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

                var assistantSourceCards = completed?.SuppressSourceCardsUi == true
                    ? Array.Empty<ChatSourceCardItem>()
                    : CreateAssistantSourceCards(completed?.SourceCards);
                _lastAssistantSources = assistantSourceCards.Count > 0
                    ? assistantSourceCards.Select(card => card.Url).ToArray()
                    : BuildAssistantSourceList(_lastAssistantMessage ?? string.Empty, completed?.Briefing);
                if (completed?.Briefing is not null)
                {
                    DisplayBriefing(completed.Briefing, recordHistory: true, activateTab: true);
                }

                if (!string.IsNullOrWhiteSpace(completed?.ConfidenceBand) ||
                    !string.IsNullOrWhiteSpace(completed?.CompletionReason))
                {
                    var confidenceText = string.IsNullOrWhiteSpace(completed?.ConfidenceBand)
                        ? "n/a"
                        : completed!.ConfidenceBand;
                    var reasonText = FormatCompletionReasonForDisplay(completed?.CompletionReason);
                    _workflowConfidenceBand = confidenceText;
                    UpdateWorkflowToolStrip();
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
                            lastMsg.Content = parts.DisplayText;
                        }
                        else
                        {
                            lastMsg.Content = parts.DisplayText;
                        }

                        // Stash the user prompt so the context menu "Retry" can resubmit it.
                        if (!string.IsNullOrWhiteSpace(_lastUserPrompt))
                        {
                            lastMsg.RetryPrompt = _lastUserPrompt;
                        }

                        if (!string.IsNullOrWhiteSpace(completed?.PlanSummary))
                        {
                            lastMsg.PlanContent = completed!.PlanSummary;
                        }

                        lastMsg.SetSourceCards(assistantSourceCards);
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

                    UpdateWorkflowToolStrip();

                    _toolCallsInCurrentRun.Clear();
                }

                // Safety: clear any orphaned pending "Thinking..." message.
                _currentSession.ClearPendingAssistantMessage();
                ScrollChatToBottom();

                // Auto-TTS: speak the response if this run was voice-initiated.
                var shouldAutoSpeak = _voiceInitiatedRun;
                _voiceInitiatedRun = false;
                _activeRunId = null;
                UpdateComposerState();

                if (ActionDrawer.IsVisible)
                {
                    _ = RefreshActionDrawerAsync();
                }

                if (_workflowChecklistItems.Count == 0)
                {
                    _ = AutoCollapseProgressDrawerAsync();
                }

                if (shouldAutoSpeak && !string.IsNullOrWhiteSpace(_lastAssistantMessage))
                {
                    _ = AutoSpeakResponseAsync(_lastAssistantMessage);
                }
                else if (shouldAutoSpeak)
                {
                    SetVoiceChatStatus("Hold");
                }
                break;
            case RuntimeEventTypes.RunFailed:
                _currentSession.ClearPendingAssistantMessage();
                _assistantBuffersByRunId.Remove(envelope.RunId);
                _toolCallsInCurrentRun.Clear();
                UpdateWorkflowToolStrip();
                _voiceInitiatedRun = false;
                SetVoiceChatStatus("Hold");
                var failure = ReadPayload<RunFailedPayload>(envelope.Payload);
                AppendTranscript($"[system] Run failed: {failure?.Error ?? "unknown"}");
                _activeRunId = null;
                HideProgressDrawer();
                UpdateComposerState();
                if (ActionDrawer.IsVisible)
                {
                    _ = RefreshActionDrawerAsync();
                }
                break;
            case RuntimeEventTypes.ToolRequested:
                var request = ReadPayload<ToolRequestedPayload>(envelope.Payload);
                if (request is not null)
                {
                    _pendingPermissionRequestId = request.RequestId;
                    _pendingPermissionAudit[request.RequestId] = new PendingPermissionAuditContext(
                        request.ToolName,
                        SummarizeToolRequest(request.ToolName, request.Reason, request.ArgumentsJson),
                        request.ArgumentsJson,
                        envelope.TimestampUtc.LocalDateTime.ToString("g"));
                    _toolCallsInCurrentRun.Add(request.ToolName);
                    UpdateWorkflowToolStrip();
                    ShowPermissionRequest(request);
                    AddRecentActivity(
                        GetToolActivityIcon(request.ToolName),
                        $"{FormatToolDisplayName(request.ToolName)} requested",
                        SummarizeToolRequest(request.ToolName, request.Reason, request.ArgumentsJson),
                        "Awaiting approval",
                        "Explicit approval required",
                        BuildToolRequestAuditPreview(request),
                        request.ToolName);
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
                if (decision is not null)
                {
                    _pendingPermissionAudit.TryGetValue(decision.RequestId, out var pendingAudit);
                    AddRecentActivity(
                        GetToolActivityIcon(decision.ToolName),
                        $"{FormatToolDisplayName(decision.ToolName)} {(decision.Approved ? "approved" : "denied")}",
                        pendingAudit?.Purpose ?? $"{FormatToolDisplayName(decision.ToolName)} permission request resolved.",
                        decision.Approved ? "Authorized" : "Denied",
                        pendingAudit?.DecisionSummary ?? (decision.Approved ? "Explicit approval recorded" : "Denied by operator"),
                        BuildToolDecisionAuditPreview(decision, pendingAudit),
                        decision.ToolName);
                    _pendingPermissionAudit.Remove(decision.RequestId);
                }
                // Suppressed: individual approval/denial messages no longer clutter chat.
                break;
            case RuntimeEventTypes.NarrationUpdated:
                var narration = ReadPayload<NarrationUpdatedPayload>(envelope.Payload);
                if (!string.IsNullOrWhiteSpace(narration?.Message) &&
                    !string.Equals(_lastWorkflowNarration, narration.Message, StringComparison.Ordinal))
                {
                    _lastWorkflowNarration = narration.Message;
                    WorkflowNarrationText.Text = narration.Message;
                    ShowProgressDrawer();
                }
                break;
            case RuntimeEventTypes.ChecklistUpdated:
                var checklist = ReadPayload<ChecklistUpdatedPayload>(envelope.Payload);
                if (checklist is not null)
                {
                    var stamp = string.Join("|", checklist.Items.Select(i => $"{i.Order}:{i.State}"));
                    if (!string.Equals(_lastWorkflowChecklistStamp, stamp, StringComparison.Ordinal))
                    {
                        _lastWorkflowChecklistStamp = stamp;
                        _workflowChecklistItems.Clear();
                        foreach (var item in checklist.Items.OrderBy(i => i.Order))
                        {
                            var stateIcon = item.State switch
                            {
                                "Completed"  => "\u2713",  // Ã¢Å“â€œ
                                "InProgress" => "\u25CF",  // Ã¢â€”Â
                                "Failed"     => "\u2717",  // Ã¢Å“â€”
                                "Blocked"    => "\u2014",  // Ã¢â‚¬â€
                                "Skipped"    => "\u203A",  // Ã¢â‚¬Âº
                                _            => "\u25CB"   // Ã¢â€”â€¹
                            };
                            var titleText = (item.Title ?? "").Trim();
                            var noteText = (item.StatusNote ?? "").Trim();
                            var label = string.IsNullOrWhiteSpace(noteText)
                                ? $"{stateIcon} {titleText}"
                                : $"{stateIcon} {titleText} \u2014 {noteText}";
                            _workflowChecklistItems.Add(new WorkflowChecklistItemViewModel
                            {
                                Id = item.Id,
                                Order = item.Order,
                                State = item.State,
                                Label = label,
                                StateIcon = stateIcon,
                                Title = titleText,
                                StatusNote = noteText
                            });
                        }

                        if (_workflowChecklistItems.Count > 0)
                        {
                            ShowProgressDrawer();

                            if (string.Equals(checklist.CurrentPhase, "Done", StringComparison.OrdinalIgnoreCase))
                            {
                                _ = AutoCollapseProgressDrawerAsync();
                            }
                        }
                    }
                }
                break;
            case RuntimeEventTypes.ProgressEvent:
                var progressEvent = ReadPayload<ProgressEventPayload>(envelope.Payload);
                if (progressEvent?.UserVisible == true &&
                    !string.IsNullOrWhiteSpace(progressEvent.Message))
                {
                    if (string.Equals(progressEvent.EventType, "retry.started", StringComparison.OrdinalIgnoreCase))
                    {
                        _workflowRetryCount++;
                        WorkflowNarrationText.Text = "Retrying with alternate verification strategy\u2026";
                        ShowProgressDrawer();
                        UpdateWorkflowToolStrip();
                    }
                    else if (string.Equals(progressEvent.EventType, "retry.skipped", StringComparison.OrdinalIgnoreCase))
                    {
                        // Surface live confidence band from metadata before run.completed.
                        var band = progressEvent.Metadata?.TryGetValue("confidenceBand", out var b) == true ? b : null;
                        if (!string.IsNullOrWhiteSpace(band))
                        {
                            _workflowConfidenceBand = band;
                        }

                        var reason = progressEvent.Metadata?.TryGetValue("reason", out var r) == true ? r : null;
                        var skipLabel = reason switch
                        {
                            "confidence_not_retry" => "Confidence is sufficient Ã¢â‚¬â€ no retry needed.",
                            "retry_budget_exhausted" => "Retry budget exhausted Ã¢â‚¬â€ finalizing with current evidence.",
                            "tool_budget_exhausted" => "Tool budget exhausted Ã¢â‚¬â€ finalizing with current evidence.",
                            "time_budget_exhausted" => "Time budget exhausted Ã¢â‚¬â€ finalizing with current evidence.",
                            _ => "Retry skipped Ã¢â‚¬â€ finalizing with current evidence."
                        };

                        WorkflowNarrationText.Text = skipLabel;
                        ShowProgressDrawer();
                        UpdateWorkflowToolStrip();
                    }
                    else if (string.Equals(progressEvent.EventType, "task.started", StringComparison.OrdinalIgnoreCase))
                    {
                        var complexity = progressEvent.Metadata?.TryGetValue("complexity", out var c) == true ? c : null;
                        if (!string.IsNullOrWhiteSpace(complexity))
                        {
                            var label = complexity switch
                            {
                                "Trivial" => "Simple request Ã¢â‚¬â€ answering directly.",
                                "SimpleLookup" => "Gathering informationÃ¢â‚¬Â¦",
                                "MultiStepResearch" => "Multi-step research Ã¢â‚¬â€ building checklistÃ¢â‚¬Â¦",
                                _ => "Processing requestÃ¢â‚¬Â¦"
                            };
                            WorkflowNarrationText.Text = label;
                            ShowProgressDrawer();
                        }
                    }
                }
                break;
            default:
                break;
        }

        UpdateActionDrawerSummary();
    }

    private void ResetWorkflowProgressUi()
    {
        CancelProgressDrawerAutoCollapse();
        _workflowChecklistItems.Clear();
        _lastWorkflowNarration = null;
        _lastWorkflowChecklistStamp = null;
        _workflowConfidenceBand = null;
        _workflowRetryCount = 0;
        WorkflowNarrationText.Text = string.Empty;
        WorkflowToolStripText.Text = string.Empty;
        HideProgressDrawer();
    }

    private void ShowProgressDrawer()
    {
        CancelProgressDrawerAutoCollapse();
        ProgressDrawer.IsVisible = true;
        // Close competing drawers.
        ActionDrawer.IsVisible = false;
        ConversationDrawer.IsVisible = false;
    }

    private void HideProgressDrawer()
    {
        CancelProgressDrawerAutoCollapse();
        ProgressDrawer.IsVisible = false;
    }

    private async Task AutoCollapseProgressDrawerAsync()
    {
        if (!ProgressDrawer.IsVisible)
            return;

        CancelProgressDrawerAutoCollapse();
        var cancellation = new CancellationTokenSource();
        _progressDrawerAutoCollapseCancellation = cancellation;

        try
        {
            await Task.Delay(2500, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(_progressDrawerAutoCollapseCancellation, cancellation) && ProgressDrawer.IsVisible)
            {
                ProgressDrawer.IsVisible = false;
            }
        });

        if (ReferenceEquals(_progressDrawerAutoCollapseCancellation, cancellation))
        {
            _progressDrawerAutoCollapseCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelProgressDrawerAutoCollapse()
    {
        var cancellation = _progressDrawerAutoCollapseCancellation;
        _progressDrawerAutoCollapseCancellation = null;
        if (cancellation is null)
            return;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void CloseProgressDrawerButton_Click(object? sender, RoutedEventArgs e)
    {
        HideProgressDrawer();
    }

    private void UpdateWorkflowToolStrip()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (_toolCallsInCurrentRun.Count > 0)
            parts.Add($"{_toolCallsInCurrentRun.Count} tool{(_toolCallsInCurrentRun.Count == 1 ? string.Empty : "s")}");
        if (_workflowRetryCount > 0)
            parts.Add($"{_workflowRetryCount} retr{(_workflowRetryCount == 1 ? "y" : "ies")}");
        if (!string.IsNullOrWhiteSpace(_workflowConfidenceBand) &&
            !string.Equals(_workflowConfidenceBand, "n/a", StringComparison.OrdinalIgnoreCase))
            parts.Add($"Confidence {_workflowConfidenceBand}");
        WorkflowToolStripText.Text = parts.Count > 0 ? string.Join(" | ", parts) : string.Empty;
    }

    private static string FormatCompletionReasonForDisplay(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return string.Empty;
        return reason switch
        {
            "SuccessHighConfidence"   => "High confidence",
            "SuccessMediumConfidence" => "Medium confidence",
            "Timeout"                => "Timed out",
            "ToolBudgetExhausted"    => "Tool budget reached",
            "RetryBudgetExhausted"   => "Retry budget reached",
            "BlockedByPolicy"        => "Blocked by policy",
            "Cancelled"              => "Cancelled",
            "Failed"                 => "Failed",
            _                        => reason
        };
    }

    private async Task SubmitPermissionDecisionAsync(bool approved, bool rememberForSession = false, bool persistAsAlways = false)
    {
        if (_runtimeApiClient is null || string.IsNullOrWhiteSpace(_pendingPermissionRequestId))
        {
            return;
        }

        var requestId = _pendingPermissionRequestId;
        if (_pendingPermissionAudit.TryGetValue(requestId, out var auditContext))
        {
            auditContext.DecisionSummary = DescribePermissionDecision(approved, rememberForSession, persistAsAlways);
        }

        try
        {
            var applied = await _runtimeApiClient.SubmitPermissionDecisionAsync(
                requestId,
                approved,
                rememberForSession,
                persistAsAlways,
                CancellationToken.None);

            // Suppressed: don't clutter chat with individual decision messages.
            // The consolidated tool summary is added to the assistant message at RunCompleted.
            if (!applied)
            {
                if (_pendingPermissionAudit.TryGetValue(requestId, out var pendingContext))
                {
                    pendingContext.DecisionSummary = null;
                }

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

            UpdateActionDrawerSummary();
            UpdateComposerState();

            // Load active profile so the user's preferred name shows in chat headers.
            _ = RefreshProfilesAsync();

            // If the user pressed Send while offline, auto-submit the pending prompt.
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

    private void UpdateActionDrawerSummary()
    {
        var statusBrush = ConnectionStatusText.Foreground
            ?? (IBrush?)this.FindResource("Overlay0Brush")
            ?? Brushes.Gray;

        ActionConnectionStateText.Text = ConnectionStatusText.Text;
        ActionConnectionStateText.Foreground = statusBrush;
        ActionConnectionDot.Background = statusBrush;

        var isConnected = string.Equals(ConnectionStatusText.Text, "Connected", StringComparison.OrdinalIgnoreCase);
        var runtimeScope = _runtimeBaseUri?.IsLoopback == true ? "Local runtime" : "Remote runtime";
        var version = ExtractRuntimeVersion(SettingsRuntimeText.Text);
        var summaryParts = new System.Collections.Generic.List<string>
        {
            isConnected ? runtimeScope + " ready" : runtimeScope + " unavailable"
        };

        if (!string.IsNullOrWhiteSpace(version))
        {
            summaryParts.Add(version);
        }

        ActionRuntimeSummaryText.Text = string.Join(" | ", summaryParts);
        ActionRuntimeStateText.Text = SimplifyRuntimeLaunchState(RuntimeLaunchStateText.Text, isConnected);
        ActionRuntimeEndpointText.Text = BuildRuntimeEndpointDetail();
    }

    private static string? ExtractRuntimeVersion(string? runtimeText)
    {
        if (string.IsNullOrWhiteSpace(runtimeText))
        {
            return null;
        }

        var openIndex = runtimeText.LastIndexOf('(');
        var closeIndex = runtimeText.LastIndexOf(')');
        if (openIndex >= 0 && closeIndex > openIndex)
        {
            return runtimeText[(openIndex + 1)..closeIndex].Trim();
        }

        return null;
    }

    private static string SimplifyRuntimeLaunchState(string? launchStateText, bool isConnected)
    {
        if (string.IsNullOrWhiteSpace(launchStateText))
        {
            return isConnected ? "Ready for requests" : "Waiting for runtime";
        }

        return launchStateText
            .Replace("Managed runtime: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("runtime", "service", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildRuntimeEndpointDetail()
    {
        var endpoint = _runtimeBaseUri?.ToString().TrimEnd('/')
            ?? RuntimeUrlBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "No endpoint configured";
        }

        return endpoint;
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

            // Resolve the active profile's preferred/display name for chat headers.
            var activeProfile = response.Profiles.FirstOrDefault(p => p.IsActive)
                                ?? response.Profiles.FirstOrDefault();
            if (activeProfile is not null)
            {
                var name = activeProfile.PreferredName;
                if (string.IsNullOrWhiteSpace(name)) name = activeProfile.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) ChatMessageItem.UserDisplayName = name;
            }
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

    private async void ActionCopyEndpointButton_Click(object? sender, RoutedEventArgs e)
    {
        var endpoint = BuildRuntimeEndpointDetail();
        if (string.IsNullOrWhiteSpace(endpoint) || string.Equals(endpoint, "No endpoint configured", StringComparison.Ordinal))
        {
            AppendTranscript("[system] No runtime endpoint to copy.");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            AppendTranscript("[error] Clipboard is unavailable on this platform.");
            return;
        }

        await clipboard.SetTextAsync(endpoint);
        AppendTranscript("[system] Copied runtime endpoint.");
    }

    private void ActionRuntimeDetailsButton_Click(object? sender, RoutedEventArgs e)
    {
        var isExpanded = !ActionRuntimeDetailsPanel.IsVisible;
        ActionRuntimeDetailsPanel.IsVisible = isExpanded;
        ActionRuntimeDetailsChevron.Symbol = isExpanded ? Symbol.ChevronUp : Symbol.ChevronDown;
    }

    private void ActionRawPayloadButton_Click(object? sender, RoutedEventArgs e)
    {
        // Legacy handler — raw payload panel removed in trust-ledger redesign.
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
                detail: "Speech playback was stopped before completion.");
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
        }
    }

    /// <summary>
    /// Automatically speaks the assistant response after a voice-initiated run completes.
    /// Fire-and-forget from RunCompleted Ã¢â‚¬â€ mirrors the WPF VoiceSessionOrchestrator auto-TTS.
    /// </summary>
    private async Task AutoSpeakResponseAsync(string text)
    {
        if (_readAloudActive || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _readAloudCancellation = cts;
        _readAloudActive = true;
        MarkReadAloudStarted(text.Length);

        try
        {
            await _ttsPlaybackService.SpeakAsync(text, cts.Token);
            MarkReadAloudCompleted(text.Length);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            MarkPushToTalkCanceled(
                headline: "Auto-read canceled.",
                detail: "Voice response playback was interrupted via VoiceHost.");
        }
        catch (Exception ex)
        {
            MarkPushToTalkFailure("Auto-read failed.", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_readAloudCancellation, cts))
            {
                _readAloudCancellation = null;
            }

            _readAloudActive = false;
        }
    }

private void ShowSourcesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastAssistantSources.Count == 0)
        {
            var lastAssistant = _currentSession.Messages.LastOrDefault(m => m.Role == "assistant" && !m.IsPending);
            if (lastAssistant is not null && lastAssistant.SourceCards.Count > 0)
            {
                _lastAssistantSources = lastAssistant.SourceCards.Select(card => card.Url).ToArray();
            }
        }

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

    // Ã¢â€â‚¬Ã¢â€â‚¬ Per-message context/flyout menu handlers Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬

    /// <summary>Resolves the ChatMessageItem from a MenuItem in either a ContextMenu or MenuFlyout.</summary>
    private static ChatMessageItem? ResolveMessageFromMenuItem(object? sender)
    {
        if (sender is not MenuItem menuItem) return null;

        // Try the MenuItem's own DataContext first (flyout inherits from DataTemplate item).
        if (menuItem.DataContext is ChatMessageItem fromDc) return fromDc;

        // Fall back to ContextMenu resolution (right-click menu).
        if (menuItem.Parent is ContextMenu ctx)
        {
            return (ctx.DataContext ?? (ctx.PlacementTarget as Control)?.DataContext) as ChatMessageItem;
        }

        return null;
    }

    private async void CopyMessage_Click(object? sender, RoutedEventArgs e)
    {
        var msg = ResolveMessageFromMenuItem(sender);
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
        var msg = ResolveMessageFromMenuItem(sender);
        if (msg is null) return;

        var prompt = msg.IsUser ? msg.Content
            : !string.IsNullOrWhiteSpace(msg.RetryPrompt) ? msg.RetryPrompt
            : _lastUserPrompt;
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
        var msg = ResolveMessageFromMenuItem(sender);
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
            MarkPushToTalkCanceled("Read aloud canceled.", "Speech playback was stopped.");
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
        }
    }

    private void AssistantSourceCardOpenButton_Click(object? sender, RoutedEventArgs e)
    {
        var url = (sender as Button)?.Tag as string;
        OpenExternalUrl(url);
    }

    // Ã¢â€â‚¬Ã¢â€â‚¬ Audit log file / folder handlers Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬

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
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            {
                urls.Add(trimmed);
            }
        }

        return urls;
    }

    private static IReadOnlyList<ChatSourceCardItem> CreateAssistantSourceCards(
        IReadOnlyList<AssistantSourceCardPayload>? sourceCards)
    {
        if (sourceCards is null || sourceCards.Count == 0)
        {
            return [];
        }

        var cards = new List<ChatSourceCardItem>(sourceCards.Count);
        foreach (var card in sourceCards)
        {
            if (string.IsNullOrWhiteSpace(card.Url))
            {
                continue;
            }

            var item = new ChatSourceCardItem
            {
                Title = string.IsNullOrWhiteSpace(card.Title) ? card.Url : card.Title.Trim(),
                Url = card.Url,
                Domain = NormalizeSourceCardDomain(card.Domain, card.Url),
                Excerpt = Truncate(card.Excerpt?.Trim() ?? string.Empty, 220),
                PublishedLabel = FormatSourceCardPublishedLabel(card.PublishedAt),
                ThumbnailUrl = card.Thumbnail?.Trim() ?? string.Empty,
                FaviconBase64 = card.Favicon?.Trim() ?? string.Empty
            };

            item.BeginLoadImages();
            cards.Add(item);
        }

        return cards;
    }

    private static string NormalizeSourceCardDomain(string? domain, string? url)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            return domain.Trim();
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : string.Empty;
    }

    private static string FormatSourceCardPublishedLabel(string? publishedAt)
    {
        if (string.IsNullOrWhiteSpace(publishedAt))
        {
            return string.Empty;
        }

        return DateTimeOffset.TryParse(publishedAt, out var parsed)
            ? parsed.LocalDateTime.ToString("MMM d, h:mm tt")
            : string.Empty;
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

    private void SuggestionChipButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_submitInProgress || sender is not Button { DataContext: SuggestionChipItem chip })
        {
            return;
        }

        if (chip.ActionKind == SuggestionActionKind.OpenAuditTrail)
        {
            SetActiveView(SettingsTabButton);
            SettingsTabControl.SelectedItem = AuditTabItem;
            _ = RefreshAuditAsync();
            AddRecentActivity(Symbol.History, "Audit trail opened", "Switched to the audit tab for inspection.", "Opened", "Audit: read-only records");
            return;
        }

        PromptBox.Text = chip.PromptText;
        PromptBox.CaretIndex = PromptBox.Text.Length;
        PromptBox.Focus();
        AddRecentActivity(chip.IconSymbol, chip.Label, "Command prepared in the composer.", "Prepared", "Awaiting explicit approval for external actions", chip.PromptText);
        UpdateComposerState();
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
        var stopAllActive = runActive || IsVoiceResponseActive() || _voiceStatusLabel is "Listening" or "Responding" or "Working";
        SendButton.IsEnabled = hasPrompt && !runActive && !_submitInProgress;
        SendButton.IsVisible = !runActive;
        SendButton.Opacity = hasPrompt ? 1.0 : 0.92;
        SendButton.Background = hasPrompt
            ? (IBrush?)this.FindResource("AccentPrimary") ?? Brushes.DodgerBlue
            : (IBrush?)this.FindResource("BackgroundTertiary") ?? Brushes.DimGray;
        SendButton.Foreground = hasPrompt
            ? Brushes.White
            : (IBrush?)this.FindResource("TextSecondary") ?? Brushes.Gray;
        StopButton.IsEnabled = runActive;
        StopButton.IsVisible = runActive;
        StopAllButton.Opacity = stopAllActive ? 1.0 : 0.38;
        UpdateRuntimeStatusStrip();
    }

    private void UpdateChatActionState()
    {
        // Actions are now per-message via triple-dot flyouts.
    }

    private void SyncLastMessageCacheFromCurrentSession()
    {
        _lastUserPrompt = _currentSession.Messages.LastOrDefault(m => m.Role == "user")?.Content;
        var lastAssistant = _currentSession.Messages.LastOrDefault(m => m.Role == "assistant" && !m.IsPending);
        _lastAssistantMessage = lastAssistant?.Content;
        _lastAssistantSources = lastAssistant is not null && lastAssistant.SourceCards.Count > 0
            ? lastAssistant.SourceCards.Select(card => card.Url).ToArray()
            : string.IsNullOrWhiteSpace(_lastAssistantMessage)
                ? Array.Empty<string>()
                : ExtractUrls(_lastAssistantMessage);
    }

    private void UpdateHeaderConnectionControls()
    {
        ConnectButton.IsVisible = false;
        ConnectButton.IsEnabled = !_isConnecting;

        var connected = _runtimeApiClient is not null;
        ConnectionStatusDot.Fill = connected
            ? (IBrush?)this.FindResource("Success") ?? Brushes.LightGreen
            : (IBrush?)this.FindResource("TextTertiary") ?? Brushes.Gray;

        ToolTip.SetTip(ConnectionStatusButton, connected ? "Connected" : "Disconnected");
        UpdateRuntimeStatusStrip();
    }

    private void UpdateLandingEmptyStateVisibility()
    {
        var showEmptyState = _currentSession.Messages.Count == 0;
        var activeConversation = !showEmptyState && ChatTabButton.IsChecked == true;
        ChatScroller.VerticalScrollBarVisibility = showEmptyState ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        HomeCommandStage.IsVisible = showEmptyState;
        RuntimeStatusStrip.IsVisible = showEmptyState;
        EmptyHero.IsVisible = showEmptyState;
        SuggestionChipsPanel.IsVisible = showEmptyState;
        ChatMessagesList.IsVisible = !showEmptyState;
        ChatSurfaceLayout.MaxWidth = showEmptyState ? 860 : 1120;
        ChatSurfaceLayout.Margin = showEmptyState ? new Thickness(24, 0, 24, 0) : new Thickness(32, 18, 32, 0);
        HomeCommandStage.Margin = showEmptyState ? new Thickness(0, -24, 0, 40) : new Thickness(0);
        ChatMessagesList.Margin = showEmptyState ? new Thickness(0, 20, 0, 36) : new Thickness(0, 24, 0, 30);
        InputBarLayout.MaxWidth = showEmptyState ? 760 : 1120;
        InputBar.Padding = showEmptyState ? new Thickness(24, 8, 24, 20) : new Thickness(28, 12, 28, 16);
        ConnectionStatusText.IsVisible = activeConversation;
        ConversationTitleText.IsVisible = false;
        ChatComposer.SetLayoutMode(activeConversation: !showEmptyState);
    }

    private void InitializeSuggestionChips()
    {
        _suggestionChips.Clear();
        _suggestionChips.Add(new SuggestionChipItem(Symbol.Screenshot, "Summarize this screen", "Summarize the current screen and call out what matters.", SuggestionActionKind.FillPrompt));
        _suggestionChips.Add(new SuggestionChipItem(Symbol.SearchInfo, "Inspect current page", "Inspect the current page and tell me what stands out.", SuggestionActionKind.FillPrompt));
        _suggestionChips.Add(new SuggestionChipItem(Symbol.FolderOpen, "Review file or folder", "Review this file or folder and call out the important findings.", SuggestionActionKind.FillPrompt));
    }

    private void UpdateRuntimeStatusStrip()
    {
        if (RuntimeStatusStrip is null)
        {
            return;
        }

        var runtimeValue = _isConnecting
            ? "Starting"
            : !string.IsNullOrWhiteSpace(_activeRunId)
                ? "Busy"
                : _runtimeApiClient is not null
                    ? "Ready"
                    : "Offline";

        var runtimeBrush = runtimeValue switch
        {
            "Ready" => ResolveThemeBrush("Success", Brushes.LightGreen),
            "Busy" => ResolveThemeBrush("AccentPrimary", Brushes.DodgerBlue),
            "Starting" => ResolveThemeBrush("AccentPrimary", Brushes.DodgerBlue),
            _ => ResolveThemeBrush("TextTertiary", Brushes.Gray)
        };

        var modelConnected = _runtimeApiClient is not null;

        _runtimeStatusItems.Clear();
        _runtimeStatusItems.Add(new RuntimeStatusItem(Symbol.WindowShield, "Runtime", runtimeValue, runtimeBrush));
        _runtimeStatusItems.Add(new RuntimeStatusItem(Symbol.Shield, "Permissions", "Explicit approval", ResolveThemeBrush("TextSecondary", Brushes.LightGray)));
        _runtimeStatusItems.Add(new RuntimeStatusItem(Symbol.History, "Audit", "Active", ResolveThemeBrush("TextSecondary", Brushes.LightGray)));

        var runtimeStamp = $"{runtimeValue}|{(modelConnected ? "connected" : "offline")}|{_voiceStatusLabel}";
        if (!string.Equals(_lastRuntimeActivityStamp, runtimeStamp, StringComparison.Ordinal))
        {
            _lastRuntimeActivityStamp = runtimeStamp;
        }
    }

    private void InitializeRecentActivity()
    {
        _recentActivityItems.Clear();
        var runtimeTitle = _runtimeApiClient is not null ? "Runtime connected" : "Runtime awaiting connection";
        var runtimeDetail = _runtimeApiClient is not null
            ? "Local runtime is available for inspect, review, and command tasks."
            : "Start or connect a local runtime to enable inspection and action flows.";

        AddRecentActivity(Symbol.WindowShield, runtimeTitle, runtimeDetail, _runtimeApiClient is not null ? "Ready" : "Waiting", "Runtime connection scope");
        AddRecentActivity(Symbol.Shield, "Approval policy ready", "File, shell, and external actions require explicit confirmation.", "Enforced", "Explicit approval required");
        AddRecentActivity(Symbol.History, "Audit trail available", "Permissions, file reads, and runtime events remain inspectable.", "Inspectable", "Audit: read-only records");
    }

    private void AddRecentActivity(
        Symbol iconSymbol,
        string actionName,
        string purpose,
        string resultStatus = "Recorded",
        string approvalScope = "Not applicable",
        string? rawPayloadPreview = null,
        string? toolLabel = null)
    {
        _recentActivityItems.Insert(0, new RecentActivityItem(
            iconSymbol,
            actionName,
            toolLabel ?? actionName,
            purpose,
            DateTime.Now.ToString("g"),
            resultStatus,
            approvalScope,
            rawPayloadPreview ?? "No raw payload captured for this action.",
            ResolveThemeBrush("TextSecondary", Brushes.LightGray)));

        while (_recentActivityItems.Count > 3)
        {
            _recentActivityItems.RemoveAt(_recentActivityItems.Count - 1);
        }
    }

    private async Task SyncRecentActivityFromAuditAsync()
    {
        if (_runtimeApiClient is null)
        {
            return;
        }

        try
        {
            var entries = await _runtimeApiClient.GetAuditAsync(CancellationToken.None);
            var auditItems = entries
                .Select(TryCreateRecentActivityFromAudit)
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderByDescending(item => item.TimestampUtc)
                .Take(3)
                .ToList();

            if (auditItems.Count == 0)
            {
                return;
            }

            var signature = string.Join("|", auditItems.Select(item => item.Signature));
            if (string.Equals(_lastActionDrawerAuditSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastActionDrawerAuditSignature = signature;
            _recentActivityItems.Clear();
            foreach (var auditItem in auditItems)
            {
                _recentActivityItems.Add(auditItem.Activity);
            }
        }
        catch
        {
            // Keep the existing drawer state when audit retrieval is unavailable.
        }
    }

    private AuditActivitySnapshot? TryCreateRecentActivityFromAudit(AuditEntryDto entry)
    {
        if (!string.Equals(entry.Category, "MCP_TOOL_CALL_END", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!TryParseJsonDocument(entry.MetadataJson, out var metadataDocument))
        {
            return null;
        }

        using (metadataDocument)
        {
            var metadata = metadataDocument.RootElement;
            var sessionId = ReadJsonPropertyAsString(metadata, "session_id");
            if (!string.Equals(sessionId, _currentSession.ConversationId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var toolName = ReadJsonPropertyAsString(metadata, "tool_name_canonical")
                ?? ReadJsonPropertyAsString(metadata, "tool_name_requested")
                ?? "tool";
            var inputSummary = ReadJsonPropertyAsString(metadata, "input_summary");
            var outputSummary = ReadJsonPropertyAsString(metadata, "output_summary");
            var permission = ReadJsonPropertyAsString(metadata, "permission");
            var requestId = ReadJsonPropertyAsString(metadata, "request_id") ?? entry.Id;
            var durationMs = ReadJsonPropertyAsString(metadata, "duration_ms");
            var result = ExtractAuditResult(entry.Message);
            var approvalScope = FormatAuditPermission(permission);
            var activity = new RecentActivityItem(
                GetToolActivityIcon(toolName),
                $"{FormatToolDisplayName(toolName)} {DescribeAuditResult(result)}",
                toolName,
                SummarizeAuditInput(toolName, inputSummary),
                entry.TimestampUtc.LocalDateTime.ToString("g"),
                FormatAuditStatus(result),
                approvalScope,
                BuildAuditPayloadPreview(entry, toolName, requestId, result, approvalScope, inputSummary, outputSummary, durationMs),
                ResolveThemeBrush("TextSecondary", Brushes.LightGray));

            return new AuditActivitySnapshot(
                activity,
                entry.TimestampUtc,
                $"{requestId}|{entry.TimestampUtc.ToUnixTimeMilliseconds()}");
        }
    }

    private static string BuildToolRequestAuditPreview(ToolRequestedPayload request)
    {
        var details = FormatPermissionDetails(request.ToolName, request.Reason, request.ArgumentsJson);
        return $"Tool: {request.ToolName}\nPermission request: {request.RequestId}\nStatus: awaiting operator approval\nPurpose: {details}\nArguments:\n{PrettyPrintJsonIfPossible(request.ArgumentsJson)}";
    }

    private static string BuildToolDecisionAuditPreview(ToolDecisionPayload decision, PendingPermissionAuditContext? context)
    {
        var builder = new StringBuilder();
        builder.Append("Tool: ").Append(decision.ToolName).AppendLine();
        builder.Append("Permission request: ").Append(decision.RequestId).AppendLine();
        builder.Append("Decision: ").AppendLine(decision.Approved ? "approved" : "denied");

        if (!string.IsNullOrWhiteSpace(context?.DecisionSummary))
        {
            builder.Append("Authorization mode: ").AppendLine(context.DecisionSummary);
        }

        if (!string.IsNullOrWhiteSpace(context?.Purpose))
        {
            builder.Append("Purpose: ").AppendLine(context.Purpose);
        }

        if (!string.IsNullOrWhiteSpace(context?.ArgumentsJson))
        {
            builder.Append("Arguments:").AppendLine();
            builder.Append(PrettyPrintJsonIfPossible(context.ArgumentsJson));
        }

        return builder.ToString().TrimEnd();
    }

    private static string DescribePermissionDecision(bool approved, bool rememberForSession, bool persistAsAlways)
    {
        if (!approved)
        {
            return "Denied by operator";
        }

        if (persistAsAlways)
        {
            return "Always allow saved";
        }

        if (rememberForSession)
        {
            return "Allowed for this session";
        }

        return "Approved once";
    }

    private static string SummarizeToolRequest(string? toolName, string? reason, string? argumentsJson)
    {
        var argumentSummary = SummarizeToolArguments(toolName, argumentsJson);
        if (!string.IsNullOrWhiteSpace(argumentSummary))
        {
            return argumentSummary;
        }

        return FormatPermissionDetails(toolName, reason, argumentsJson);
    }

    private static string SummarizeAuditInput(string? toolName, string? inputSummary)
    {
        if (string.IsNullOrWhiteSpace(inputSummary))
        {
            return $"{FormatToolDisplayName(toolName)} completed without a captured input summary.";
        }

        var argumentSummary = SummarizeToolArguments(toolName, inputSummary);
        return string.IsNullOrWhiteSpace(argumentSummary)
            ? TruncateSingleLine(inputSummary, 180)
            : argumentSummary;
    }

    private static string SummarizeToolArguments(string? toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || !TryParseJsonDocument(argumentsJson, out var document))
        {
            return string.Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return TruncateSingleLine(argumentsJson, 180);
            }

            var normalizedTool = NormalizeToolName(toolName);
            return normalizedTool switch
            {
                "web_search" => BuildSearchSummary(root),
                "browser_navigate" => BuildUrlSummary(root, "Navigate"),
                _ => BuildGenericArgumentSummary(root)
            };
        }
    }

    private static string BuildSearchSummary(JsonElement root)
    {
        var query = ReadJsonPropertyAsString(root, "query")
            ?? ReadJsonPropertyAsString(root, "q")
            ?? ReadJsonPropertyAsString(root, "searchQuery");
        var recency = ReadJsonPropertyAsString(root, "recency");

        if (string.IsNullOrWhiteSpace(query))
        {
            return BuildGenericArgumentSummary(root);
        }

        return string.IsNullOrWhiteSpace(recency)
            ? $"Query: {query}"
            : $"Query: {query} | Recency: {recency}";
    }

    private static string BuildUrlSummary(JsonElement root, string label)
    {
        var url = ReadJsonPropertyAsString(root, "url")
            ?? ReadJsonPropertyAsString(root, "uri")
            ?? ReadJsonPropertyAsString(root, "address");

        return string.IsNullOrWhiteSpace(url)
            ? BuildGenericArgumentSummary(root)
            : $"{label}: {url}";
    }

    private static string BuildGenericArgumentSummary(JsonElement root)
    {
        foreach (var name in new[] { "query", "prompt", "path", "filePath", "url", "uri", "command", "text" })
        {
            var value = ReadJsonPropertyAsString(root, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return $"{ToTitleLabel(name)}: {value}";
            }
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return $"{ToTitleLabel(property.Name)}: {ReadJsonElementAsString(property.Value)}";
            }
        }

        return TruncateSingleLine(root.GetRawText(), 180);
    }

    private static string BuildAuditPayloadPreview(
        AuditEntryDto entry,
        string toolName,
        string requestId,
        string result,
        string approvalScope,
        string? inputSummary,
        string? outputSummary,
        string? durationMs)
    {
        var builder = new StringBuilder();
        builder.Append("Audit event: ").Append(entry.Category).AppendLine();
        builder.Append("Tool: ").Append(toolName).AppendLine();
        builder.Append("Request id: ").Append(requestId).AppendLine();
        builder.Append("Result: ").Append(result).AppendLine();
        builder.Append("Authorization: ").Append(approvalScope).AppendLine();

        if (!string.IsNullOrWhiteSpace(durationMs))
        {
            builder.Append("Duration: ").Append(durationMs).AppendLine(" ms");
        }

        if (!string.IsNullOrWhiteSpace(inputSummary))
        {
            builder.Append("Input: ").AppendLine(inputSummary);
        }

        if (!string.IsNullOrWhiteSpace(outputSummary))
        {
            builder.Append("Output summary: ").AppendLine(TruncateSingleLine(outputSummary, 240));
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatAuditPermission(string? permission)
    {
        return permission?.ToLowerInvariant() switch
        {
            "granted" => "Explicit approval recorded",
            "denied" => "Denied by operator",
            "policy_always" => "Always-allow policy",
            "session_grant" => "Allowed for this session",
            "tool_exempt" => "Exempt tool; no prompt required",
            "not_required" => "No prompt required",
            _ => "Audit permission status unavailable"
        };
    }

    private static string FormatAuditStatus(string result)
    {
        return result.ToLowerInvariant() switch
        {
            "ok" => "Completed",
            "blocked" => "Blocked",
            "error" => "Error",
            "cancelled" => "Cancelled",
            _ => ToTitleLabel(result)
        };
    }

    private static string DescribeAuditResult(string result)
    {
        return result.ToLowerInvariant() switch
        {
            "ok" => "completed",
            "blocked" => "blocked",
            "error" => "failed",
            "cancelled" => "cancelled",
            _ => "updated"
        };
    }

    private static string ExtractAuditResult(string message)
    {
        var openParen = message.LastIndexOf('(');
        var closeParen = message.LastIndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
        {
            return message[(openParen + 1)..closeParen];
        }

        return "ok";
    }

    private static Symbol GetToolActivityIcon(string? toolName)
    {
        return NormalizeToolName(toolName) switch
        {
            "web_search" => Symbol.SearchInfo,
            "browser_navigate" => Symbol.Open,
            "file_read" or "file_write" or "file_list" => Symbol.FolderOpen,
            _ => Symbol.Scan
        };
    }

    private static string FormatToolDisplayName(string? toolName)
    {
        var normalized = NormalizeToolName(toolName);
        return normalized switch
        {
            "web_search" => "Web search",
            "browser_navigate" => "Browser navigation",
            _ => ToTitleLabel(normalized.Replace("_", " "))
        };
    }

    private static string NormalizeToolName(string? toolName)
    {
        return string.IsNullOrWhiteSpace(toolName)
            ? "tool"
            : toolName.Trim().ToLowerInvariant();
    }

    private static string PrettyPrintJsonIfPossible(string text)
    {
        if (!TryParseJsonDocument(text, out var document))
        {
            return text;
        }

        using (document)
        {
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static bool TryParseJsonDocument(string? text, out JsonDocument document)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            document = null!;
            return false;
        }

        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static string? ReadJsonPropertyAsString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return ReadJsonElementAsString(property.Value);
            }
        }

        return null;
    }

    private static string? ReadJsonElementAsString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
    }

    private static string ToTitleLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var parts = value
            .Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant());
        return string.Join(" ", parts);
    }

    private static string TruncateSingleLine(string text, int maxLength)
    {
        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..(maxLength - 3)] + "...";
    }

    private IBrush ResolveThemeBrush(string key, IBrush fallback)
    {
        return (IBrush?)this.FindResource(key) ?? fallback;
    }

    private sealed class SuggestionChipItem(Symbol iconSymbol, string label, string promptText, SuggestionActionKind actionKind)
    {
        public Symbol IconSymbol { get; } = iconSymbol;
        public string Label { get; } = label;
        public string PromptText { get; } = promptText;
        public SuggestionActionKind ActionKind { get; } = actionKind;
    }

    private enum SuggestionActionKind
    {
        FillPrompt,
        OpenAuditTrail
    }

    private sealed class RuntimeStatusItem(Symbol iconSymbol, string label, string value, IBrush accentBrush)
    {
        public Symbol IconSymbol { get; } = iconSymbol;
        public string Label { get; } = label;
        public string Value { get; } = value;
        public IBrush AccentBrush { get; } = accentBrush;
    }

    private sealed class RecentActivityItem(
        Symbol iconSymbol,
        string actionName,
        string toolLabel,
        string purpose,
        string timestampLabel,
        string resultStatus,
        string approvalScope,
        string rawPayloadPreview,
        IBrush accentBrush)
    {
        public Symbol IconSymbol { get; } = iconSymbol;
        public string ActionName { get; } = actionName;
        public string ToolLabel { get; } = toolLabel;
        public string Purpose { get; } = purpose;
        public string TimestampLabel { get; } = timestampLabel;
        public string TimeLabel { get; } = timestampLabel;
        public string ResultStatus { get; } = resultStatus;
        public string ApprovalScope { get; } = approvalScope;
        public string RawPayloadPreview { get; } = rawPayloadPreview;
        public IBrush AccentBrush { get; } = accentBrush;
    }

    private sealed class PendingPermissionAuditContext(string toolName, string purpose, string argumentsJson, string requestedAtLabel)
    {
        public string ToolName { get; } = toolName;
        public string Purpose { get; } = purpose;
        public string ArgumentsJson { get; } = argumentsJson;
        public string RequestedAtLabel { get; } = requestedAtLabel;
        public string? DecisionSummary { get; set; }
    }

    private sealed class AuditActivitySnapshot(RecentActivityItem activity, DateTimeOffset timestampUtc, string signature)
    {
        public RecentActivityItem Activity { get; } = activity;
        public DateTimeOffset TimestampUtc { get; } = timestampUtc;
        public string Signature { get; } = signature;
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

        // Large model outputs with many bullet markers can make broad markdown
        // regex passes expensive on the UI thread. Keep this transformation
        // bounded and failure-safe.
        try
        {
            // *italic* and _italic_ (single markers, only if not inside a word)
            text = MarkdownItalicAsteriskRegex.Replace(text, "$1");
            text = MarkdownItalicUnderscoreRegex.Replace(text, "$1");

            // `inline code` Ã¢â‚¬â€ just remove the backticks
            text = MarkdownInlineCodeRegex.Replace(text, "$1");

            // ### Headings Ã¢â‚¬â€ strip leading hashes
            text = MarkdownHeadingRegex.Replace(text, "");
        }
        catch (RegexMatchTimeoutException)
        {
            // Fall back to lightweight cleanup so the UI still completes the turn.
            text = text.Replace("`", "", StringComparison.Ordinal);
        }

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
