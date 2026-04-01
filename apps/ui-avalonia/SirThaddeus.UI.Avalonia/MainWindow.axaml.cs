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

    private static string ToAuditLine(AuditEntryDto dto)
    {
        return $"{dto.TimestampUtc:O} [{dto.Category}] {dto.Message}";
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

    private IBrush ResolveThemeBrush(string key, IBrush fallback)
    {
        return (IBrush?)this.FindResource(key) ?? fallback;
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
