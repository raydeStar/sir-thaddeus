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
}
