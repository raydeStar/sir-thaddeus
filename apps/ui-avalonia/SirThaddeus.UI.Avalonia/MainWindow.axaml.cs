using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
}
