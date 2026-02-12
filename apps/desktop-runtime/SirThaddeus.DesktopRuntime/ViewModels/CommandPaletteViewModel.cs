using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows.Input;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.AuditLog;
using SirThaddeus.Core;
using SirThaddeus.DesktopRuntime.Services;
using SirThaddeus.Invocation;
using SirThaddeus.LlmClient;

namespace SirThaddeus.DesktopRuntime.ViewModels;

// ─────────────────────────────────────────────────────────────────────────
// Command Palette ViewModel — Chat Mode
//
// Routes user input through the AgentOrchestrator to the local LLM.
// Tool calls go through MCP (orchestrator handles that internally).
// State transitions are forwarded to the RuntimeController so the
// overlay pill reflects what's actually happening.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Chat-oriented ViewModel for the command palette window.
/// Replaces the legacy regex planner with a direct LLM conversation.
/// </summary>
public sealed class CommandPaletteViewModel : ViewModelBase
{
    // ─────────────────────────────────────────────────────────────────
    // Dependencies
    // ─────────────────────────────────────────────────────────────────

    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILlmClient _llmClient;
    private readonly IToolExecutionHost _host;
    private readonly IAuditLogger _audit;
    private readonly Action _closeWindow;
    private readonly IDialogueStatePersistence? _dialogueStatePersistence;
    // Voice PTT delegates are now set as public properties (VoiceMicDown, VoiceMicUp, VoiceShutup)
    // after construction — the ViewModel doesn't own the orchestrator.

    // ─────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────

    private string _inputText = string.Empty;
    private bool _isProcessing;
    private bool _isLlmConnected;
    private string _connectionStatus = "Checking...";
    private bool _contextLocked;
    private CancellationTokenSource? _processingCts;

    // ── Voice debug state ────────────────────────────────────────────
    private string _voiceStatusText = "";
    private string _voiceTranscriptText = "";
    private bool _isVoiceActive;
    private string _reasoningGuardrailsMode = "off";
    // Old voice test fields removed — replaced by VoiceStatusText / VoiceTranscriptText / IsVoiceActive.

    private static readonly Regex TaggedThinkingRegex = new(
        @"<(?<tag>think|thinking|reasoning)>(?<body>[\s\S]*?)</\k<tag>>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumberedReasoningLeadRegex = new(
        @"^\d+[\.\)]\s*(analy(?:ze|sis)?|reason(?:ing)?|think(?:ing)?|thought|consult|plan|approach|breakdown)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumberedLineRegex = new(
        @"^\d+[\.\)]\s+",
        RegexOptions.Compiled);

    // ─────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────

    public CommandPaletteViewModel(
        IAgentOrchestrator orchestrator,
        ILlmClient llmClient,
        IToolExecutionHost host,
        IAuditLogger audit,
        Action closeWindow,
        IDialogueStatePersistence? dialogueStatePersistence = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _llmClient    = llmClient    ?? throw new ArgumentNullException(nameof(llmClient));
        _host         = host         ?? throw new ArgumentNullException(nameof(host));
        _audit        = audit        ?? throw new ArgumentNullException(nameof(audit));
        _closeWindow  = closeWindow  ?? throw new ArgumentNullException(nameof(closeWindow));
        _dialogueStatePersistence = dialogueStatePersistence;

        SendCommand   = new AsyncRelayCommand(SendAsync, CanSend);
        ClearCommand  = new RelayCommand(ClearConversation);
        CancelCommand = new RelayCommand(CancelProcessing, () => _isProcessing);
        ToggleContextLockCommand = new RelayCommand(_ => ContextLocked = !ContextLocked);

        _contextLocked = _orchestrator.ContextLocked;
        ApplyContextSnapshot(_orchestrator.GetContextSnapshot());

        // Check connection on construction (fire-and-forget, logged on failure)
        _ = CheckConnectionAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Bindable Properties
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The conversation message feed (left pane).
    /// </summary>
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    /// <summary>
    /// Debug activity log (right pane). Shows tool calls, results, and diagnostics.
    /// </summary>
    public ObservableCollection<LogEntry> ActivityLog { get; } = [];

    /// <summary>
    /// Compact continuity chips rendered above chat input.
    /// </summary>
    public ObservableCollection<ContextChipViewModel> ContextChips { get; } = [];

    /// <summary>
    /// Current text in the input box.
    /// </summary>
    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    /// <summary>
    /// True while the orchestrator is processing a request.
    /// Disables Send, enables Cancel.
    /// </summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Whether the LLM endpoint is reachable.
    /// Drives the connection dot color in the header.
    /// </summary>
    public bool IsLlmConnected
    {
        get => _isLlmConnected;
        private set => SetProperty(ref _isLlmConnected, value);
    }

    /// <summary>
    /// Human-readable connection status for the header.
    /// </summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    /// <summary>
    /// Locks context updates to explicit location changes.
    /// </summary>
    public bool ContextLocked
    {
        get => _contextLocked;
        set
        {
            if (!SetProperty(ref _contextLocked, value))
                return;

            _orchestrator.ContextLocked = value;
            ApplyContextSnapshot(_orchestrator.GetContextSnapshot());
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Voice Debug Properties (push-button ASR test panel)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Current voice pipeline phase label (e.g. "Listening...", "Transcribing...").
    /// </summary>
    public string VoiceStatusText
    {
        get => _voiceStatusText;
        set => SetProperty(ref _voiceStatusText, value);
    }

    /// <summary>
    /// Live transcript / agent response text for debugging.
    /// </summary>
    public string VoiceTranscriptText
    {
        get => _voiceTranscriptText;
        set => SetProperty(ref _voiceTranscriptText, value);
    }

    /// <summary>
    /// True while a voice session is in progress (non-idle).
    /// Controls visibility of the transcript debug area.
    /// </summary>
    public bool IsVoiceActive
    {
        get => _isVoiceActive;
        set => SetProperty(ref _isVoiceActive, value);
    }

    /// <summary>
    /// Runtime first-principles mode mirrored from settings/tray/hotkey controls.
    /// </summary>
    public string ReasoningGuardrailsMode
    {
        get => _reasoningGuardrailsMode;
        set => SetProperty(ref _reasoningGuardrailsMode, NormalizeReasoningGuardrailsMode(value));
    }

    /// <summary>
    /// Delegates wired by the composition root to trigger PTT actions.
    /// The ViewModel doesn't own the voice orchestrator directly.
    /// </summary>
    public Action? VoiceMicDown { get; set; }
    public Action? VoiceMicUp  { get; set; }
    public Action? VoiceShutup { get; set; }

    // ─────────────────────────────────────────────────────────────────
    // Commands
    // ─────────────────────────────────────────────────────────────────

    public ICommand SendCommand   { get; }
    public ICommand ClearCommand  { get; }
    public ICommand CancelCommand { get; }
    public ICommand ToggleContextLockCommand { get; }

    // ─────────────────────────────────────────────────────────────────
    // Events
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised after a message is appended — the view uses this to auto-scroll.
    /// </summary>
    public event Action? MessageAdded;

    /// <summary>
    /// Raised after a log entry is appended — the view uses this to auto-scroll the log.
    /// </summary>
    public event Action? LogEntryAdded;

    /// <summary>
    /// Raised when the user clears the conversation ("New Chat").
    /// The runtime uses this to clear session-scoped permission grants.
    /// </summary>
    public event Action? ConversationCleared;

    // ─────────────────────────────────────────────────────────────────
    // Connection Health Check
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pings the LLM endpoint and MCP server, updating the connection status display.
    /// Called on construction and after "New Chat".
    /// </summary>
    public async Task CheckConnectionAsync()
    {
        try
        {
            // Check both LLM and MCP in parallel
            var modelTask = _llmClient.GetModelNameAsync();
            var toolCountTask = _orchestrator.GetAvailableToolCountAsync();

            await Task.WhenAll(modelTask, toolCountTask);

            var modelName = modelTask.Result;
            var toolCount = toolCountTask.Result;

            if (modelName != null)
            {
                IsLlmConnected = true;

                var toolStatus = toolCount > 0
                    ? $"{toolCount} tools"
                    : "no tools (MCP offline)";

                ConnectionStatus = $"Connected \u2022 {modelName} \u2022 {toolStatus}";
                AddStatus($"Ready \u2014 {modelName} \u2022 {toolStatus}");

                // Log to activity pane
                AddLog(LogEntryKind.Info, $"LLM: {modelName}");
                AddLog(toolCount > 0 ? LogEntryKind.Info : LogEntryKind.Error,
                    $"MCP: {toolCount} tool(s) available");

                if (toolCount == 0)
                {
                    AddStatus("\u26A0 MCP server not reachable. Tool calls will not work. " +
                              "Rebuild the solution and restart.");
                }
            }
            else
            {
                IsLlmConnected = false;
                ConnectionStatus = "Disconnected";
                AddStatus("Cannot reach LLM. Is LM Studio running on localhost:1234?");
            }
        }
        catch
        {
            IsLlmConnected = false;
            ConnectionStatus = "Error";
            AddStatus("Failed to check LLM connection.");
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Send / Cancel / Clear
    // ─────────────────────────────────────────────────────────────────

    private bool CanSend() => !IsProcessing && !string.IsNullOrWhiteSpace(InputText);

    private async Task SendAsync()
    {
        if (IsProcessing || string.IsNullOrWhiteSpace(InputText))
            return;

        var userText = InputText.Trim();
        InputText = string.Empty;
        IsProcessing = true;
        _processingCts = new CancellationTokenSource();

        // ── Display the user message ─────────────────────────────
        AddMessage(ChatMessageRole.User, userText);

        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "CHAT_MESSAGE_SENT",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["length"] = userText.Length
            }
        });

        // ── Transition to Thinking ───────────────────────────────
        _host.SetState(AssistantState.Thinking, "Processing chat message");

        var thinkingMsg = new ChatMessageViewModel
        {
            Role    = ChatMessageRole.Status,
            Content = "Thinking\u2026"
        };
        Messages.Add(thinkingMsg);
        MessageAdded?.Invoke();

        try
        {
            var result = await _orchestrator.ProcessAsync(
                userText, _processingCts.Token);

            // Remove the placeholder
            Messages.Remove(thinkingMsg);

            if (result.Success)
            {
                var displayParts = ParseAssistantDisplayParts(result.Text);
                var assistantMsg = new ChatMessageViewModel
                {
                    Role    = ChatMessageRole.Assistant,
                    Content = displayParts.DisplayText,
                    ThoughtContent = displayParts.ThinkingText
                };

                // ── Parse source cards from web search results ───
                // Some deterministic utility/fact responses intentionally
                // suppress source cards in the chat pane while keeping
                // full internal logs for diagnostics.
                if (!result.SuppressSourceCardsUi)
                    TryAttachSourceCards(assistantMsg, result.ToolCallsMade);

                Messages.Add(assistantMsg);
                MessageAdded?.Invoke();

                if (assistantMsg.HasThoughtContent)
                {
                    AddLog(LogEntryKind.Info,
                        $"Assistant reasoning captured ({assistantMsg.ThoughtContent.Length} chars, collapsed by default).");
                }

                // ── Log tool calls to activity pane ──────────────
                if (result.ToolCallsMade.Count > 0)
                {
                    if (!result.SuppressToolActivityUi)
                    {
                        var toolNames = string.Join(", ",
                            result.ToolCallsMade.Select(t => t.ToolName));

                        AddMessage(ChatMessageRole.ToolActivity,
                            $"\u26A1 {result.ToolCallsMade.Count} tool call(s): {toolNames} " +
                            $"\u2022 {result.LlmRoundTrips} LLM round-trip(s)");
                    }

                    foreach (var tc in result.ToolCallsMade)
                    {
                        AddLog(LogEntryKind.ToolInput,
                            $"\u2192 {tc.ToolName}({Truncate(tc.Arguments, 120)})");
                        AddLog(tc.Success ? LogEntryKind.ToolOutput : LogEntryKind.Error,
                            $"\u2190 {Truncate(tc.Result, 300)}");
                    }
                }

                if (result.GuardrailsUsed)
                {
                    AddLog(LogEntryKind.Info, "First principles thinking used.");
                    foreach (var line in result.GuardrailsRationale.Take(3))
                        AddLog(LogEntryKind.Info, line);
                }

                AddLog(LogEntryKind.Info,
                    $"{result.LlmRoundTrips} LLM round-trip(s)");
            }
            else
            {
                AddMessage(ChatMessageRole.Status, $"Error: {result.Error}");
                AddLog(LogEntryKind.Error, $"Agent error: {result.Error}");
            }

            ApplyContextSnapshot(result.ContextSnapshot ?? _orchestrator.GetContextSnapshot());
            await PersistDialogueStateAsync();
        }
        catch (OperationCanceledException)
        {
            Messages.Remove(thinkingMsg);
            AddStatus("Cancelled.");
        }
        catch (HttpRequestException ex)
        {
            Messages.Remove(thinkingMsg);
            AddStatus($"Cannot reach LLM: {ex.Message}");

            IsLlmConnected  = false;
            ConnectionStatus = "Disconnected";
        }
        catch (Exception ex)
        {
            Messages.Remove(thinkingMsg);
            AddStatus($"Unexpected error: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
            _host.SetState(AssistantState.Idle, "Chat response delivered");

            _processingCts?.Dispose();
            _processingCts = null;
        }
    }

    private void CancelProcessing()
    {
        _processingCts?.Cancel();
    }

    private void ClearConversation()
    {
        Messages.Clear();
        ActivityLog.Clear();
        ContextChips.Clear();
        _orchestrator.ResetConversation();
        ApplyContextSnapshot(_orchestrator.GetContextSnapshot());

        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "CHAT_CLEARED",
            Result = "ok"
        });

        if (_dialogueStatePersistence is not null)
        {
            _ = _dialogueStatePersistence.ClearAsync();
        }

        // Notify runtime to clear session-scoped permission grants
        ConversationCleared?.Invoke();

        // Re-check connection and show welcome status
        _ = CheckConnectionAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Window Lifecycle
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when Escape is pressed or the window is manually closed.
    /// </summary>
    public void Close()
    {
        _processingCts?.Cancel();
        _closeWindow();
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private void ApplyContextSnapshot(DialogueContextSnapshot? snapshot)
    {
        ContextChips.Clear();

        if (snapshot is null)
            return;

        if (!string.IsNullOrWhiteSpace(snapshot.Topic))
            ContextChips.Add(new ContextChipViewModel($"Topic: {snapshot.Topic}"));

        if (!string.IsNullOrWhiteSpace(snapshot.Location))
        {
            var inferred = snapshot.LocationInferred ? " (inferred)" : "";
            ContextChips.Add(new ContextChipViewModel($"Location: {snapshot.Location}{inferred}"));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.TimeScope))
            ContextChips.Add(new ContextChipViewModel($"Time: {snapshot.TimeScope}"));

        if (snapshot.GeocodeMismatch)
            ContextChips.Add(new ContextChipViewModel("Mismatch", isWarning: true));

        _contextLocked = snapshot.ContextLocked;
        OnPropertyChanged(nameof(ContextLocked));
        OnPropertyChanged(nameof(ContextChips));
    }

    private async Task PersistDialogueStateAsync()
    {
        if (_dialogueStatePersistence is null)
            return;

        try
        {
            var snapshot = _orchestrator.GetContextSnapshot();
            var state = new DialogueState
            {
                Topic = snapshot.Topic ?? "",
                LocationName = snapshot.Location,
                TimeScope = snapshot.TimeScope,
                ContextLocked = snapshot.ContextLocked,
                GeocodeMismatch = snapshot.GeocodeMismatch,
                LocationInferred = snapshot.LocationInferred,
                RollingSummary = "",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            await _dialogueStatePersistence.SaveAsync(state);
        }
        catch (Exception ex)
        {
            AddLog(LogEntryKind.Error, $"Dialogue state save failed: {ex.Message}");
        }
    }

    private void AddMessage(ChatMessageRole role, string content)
    {
        Messages.Add(new ChatMessageViewModel
        {
            Role    = role,
            Content = content
        });
        MessageAdded?.Invoke();
    }

    /// <summary>
    /// Appends a user chat bubble from the voice pipeline.
    /// </summary>
    public void AddVoiceUserMessage(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return;

        AddMessage(ChatMessageRole.User, transcript.Trim());
    }

    /// <summary>
    /// Appends an assistant chat bubble from the voice pipeline.
    /// Applies the same cleaning/reasoning extraction path as typed chat.
    /// </summary>
    public void AddVoiceAssistantMessage(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return;

        var displayParts = ParseAssistantDisplayParts(responseText);
        Messages.Add(new ChatMessageViewModel
        {
            Role = ChatMessageRole.Assistant,
            Content = displayParts.DisplayText,
            ThoughtContent = displayParts.ThinkingText
        });
        MessageAdded?.Invoke();
    }

    /// <summary>
    /// Appends a voice diagnostics entry to the activity log.
    /// </summary>
    public void AddVoiceLog(string text, LogEntryKind kind = LogEntryKind.Info)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        AddLog(kind, text.Trim());
    }

    private void AddStatus(string content)
        => AddMessage(ChatMessageRole.Status, content);

    private void AddLog(LogEntryKind kind, string text)
    {
        ActivityLog.Add(new LogEntry { Kind = kind, Text = text });
        LogEntryAdded?.Invoke();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";

        // Collapse whitespace for compact display
        var compact = value.Replace("\r\n", " ").Replace("\n", " ");
        return compact.Length <= maxLength
            ? compact
            : compact[..maxLength] + "\u2026";
    }

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

    /// <summary>
    /// Strips formatting artifacts that local LLMs sometimes echo back.
    /// They see our bracketed markers and mimic them in their output.
    /// </summary>
    private static string CleanLlmOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Remove lines that are just bracketed markers the LLM parroted
        var lines = text.Split('\n');
        var cleaned = lines
            .Where(line =>
            {
                var trimmed = line.Trim();
                if (IsLikelyInternalMarkerLine(trimmed))
                    return false;

                // Remove [END OF ...], [INSTRUCTIONS ...], [REFERENCE DATA ...] etc.
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']') &&
                    (trimmed.Contains("END OF", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("INSTRUCTIONS", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("REFERENCE DATA", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("ASSISTANT RESPONSE", StringComparison.OrdinalIgnoreCase)))
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
        {
            return true;
        }

        var normalized = marker.Replace("/", "", StringComparison.Ordinal).Trim();
        if (normalized.Length == 0)
            return false;

        return normalized.All(c =>
            char.IsUpper(c) ||
            char.IsDigit(c) ||
            c == '_' ||
            c == '-' ||
            c == ' ');
    }

    private static bool TryExtractTaggedThinking(
        string text,
        out string displayText,
        out string thinkingText)
    {
        displayText = text;
        thinkingText = "";

        var match = TaggedThinkingRegex.Match(text);
        if (!match.Success)
            return false;

        var thought = match.Groups["body"].Value.Trim();
        var visible = text.Remove(match.Index, match.Length).Trim();

        // If the model only produced thought text, keep existing behavior.
        if (string.IsNullOrWhiteSpace(visible) || string.IsNullOrWhiteSpace(thought))
            return false;

        displayText = visible;
        thinkingText = thought;
        return true;
    }

    private static bool TryExtractStructuredThinkingPreamble(
        string text,
        out string displayText,
        out string thinkingText)
    {
        displayText = text;
        thinkingText = "";

        var normalized = NormalizeNewlines(text);
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
        {
            return true;
        }

        if (trimmed.EndsWith(':') && trimmed.Length <= 120)
            return true;

        var lower = trimmed.ToLowerInvariant();
        return lower.Contains("analyze", StringComparison.Ordinal) ||
               lower.Contains("analysis", StringComparison.Ordinal) ||
               lower.Contains("reasoning", StringComparison.Ordinal) ||
               lower.Contains("consult memory", StringComparison.Ordinal) ||
               lower.Contains("step-by-step", StringComparison.Ordinal);
    }

    private static string NormalizeNewlines(string text)
        => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static string NormalizeReasoningGuardrailsMode(string? mode)
    {
        var normalized = (mode ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" => "auto",
            "always" => "always",
            _ => "off"
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Source Card Extraction
    //
    // Parses the <!-- SOURCES_JSON --> block from web search tool
    // results and populates the assistant message with rich cards.
    // ─────────────────────────────────────────────────────────────────

    private const string SourcesDelimiter = "<!-- SOURCES_JSON -->";

    /// <summary>
    /// Scans tool call results for WebSearch output containing source JSON.
    /// Attaches parsed SourceCardViewModels to the message for UI rendering.
    /// </summary>
    private static void TryAttachSourceCards(
        ChatMessageViewModel message,
        IReadOnlyList<ToolCallRecord> toolCalls)
    {
        // Find the first WebSearch tool result containing our delimiter.
        // MCP may register the tool as "web_search" (snake_case) or "WebSearch" (PascalCase)
        // depending on the framework version, so match flexibly.
        var webSearchResult = toolCalls
            .FirstOrDefault(tc =>
                IsWebSearchTool(tc.ToolName) &&
                tc.Success &&
                tc.Result.Contains(SourcesDelimiter));

        if (webSearchResult is null)
            return;

        try
        {
            var delimiterIndex = webSearchResult.Result.IndexOf(
                SourcesDelimiter, StringComparison.Ordinal);

            var jsonStart = delimiterIndex + SourcesDelimiter.Length;
            var json = webSearchResult.Result[jsonStart..].Trim();

            if (string.IsNullOrEmpty(json))
                return;

            var sources = JsonSerializer.Deserialize<List<SourceEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (sources is null || sources.Count == 0)
                return;

            for (var i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                message.SourceCards.Add(SourceCardViewModel.Create(
                    title:     s.Title ?? "(untitled)",
                    url:       s.Url ?? "",
                    domain:    s.Domain ?? "",
                    excerpt:   s.Excerpt ?? "",
                    favicon:   string.IsNullOrWhiteSpace(s.Favicon) ? null : s.Favicon,
                    thumbnail: string.IsNullOrWhiteSpace(s.Thumbnail) ? null : s.Thumbnail,
                    index:     i));
            }
        }
        catch
        {
            // Source card parsing is best-effort — never break the chat
        }
    }

    /// <summary>
    /// Matches tool names flexibly: MCP may register as "WebSearch" or "web_search".
    /// </summary>
    private static bool IsWebSearchTool(string toolName)
    {
        return toolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DTO for deserializing the SOURCES_JSON block from WebSearch output.
    /// </summary>
    private sealed record SourceEntry
    {
        public string? Title     { get; init; }
        public string? Url       { get; init; }
        public string? Domain    { get; init; }
        public string? Excerpt   { get; init; }
        public string? Favicon   { get; init; }
        public string? Thumbnail { get; init; }
    }
}

public sealed class ContextChipViewModel
{
    public ContextChipViewModel(string text, bool isWarning = false)
    {
        Text = text;
        IsWarning = isWarning;
    }

    public string Text { get; }
    public bool IsWarning { get; }
}
