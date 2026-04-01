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
