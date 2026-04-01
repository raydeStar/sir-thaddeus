using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using SirThaddeus.Config;
using SirThaddeus.UI.Avalonia.ViewModels;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
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

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
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

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

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
}