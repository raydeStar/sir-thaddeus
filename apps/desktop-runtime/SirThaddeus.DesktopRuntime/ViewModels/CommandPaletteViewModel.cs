using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Core;
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

    // ─────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────

    private string _inputText = string.Empty;
    private bool _isProcessing;
    private bool _isLlmConnected;
    private string _connectionStatus = "Checking...";
    private CancellationTokenSource? _processingCts;

    // ─────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────

    public CommandPaletteViewModel(
        IAgentOrchestrator orchestrator,
        ILlmClient llmClient,
        IToolExecutionHost host,
        IAuditLogger audit,
        Action closeWindow)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _llmClient    = llmClient    ?? throw new ArgumentNullException(nameof(llmClient));
        _host         = host         ?? throw new ArgumentNullException(nameof(host));
        _audit        = audit        ?? throw new ArgumentNullException(nameof(audit));
        _closeWindow  = closeWindow  ?? throw new ArgumentNullException(nameof(closeWindow));

        SendCommand   = new AsyncRelayCommand(SendAsync, CanSend);
        ClearCommand  = new RelayCommand(ClearConversation);
        CancelCommand = new RelayCommand(CancelProcessing, () => _isProcessing);

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

    // ─────────────────────────────────────────────────────────────────
    // Commands
    // ─────────────────────────────────────────────────────────────────

    public ICommand SendCommand   { get; }
    public ICommand ClearCommand  { get; }
    public ICommand CancelCommand { get; }

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
                var assistantMsg = new ChatMessageViewModel
                {
                    Role    = ChatMessageRole.Assistant,
                    Content = CleanLlmOutput(result.Text)
                };

                // ── Parse source cards from web search results ───
                // Some deterministic utility/fact responses intentionally
                // suppress source cards in the chat pane while keeping
                // full internal logs for diagnostics.
                if (!result.SuppressSourceCardsUi)
                    TryAttachSourceCards(assistantMsg, result.ToolCallsMade);

                Messages.Add(assistantMsg);
                MessageAdded?.Invoke();

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

                AddLog(LogEntryKind.Info,
                    $"{result.LlmRoundTrips} LLM round-trip(s)");
            }
            else
            {
                AddMessage(ChatMessageRole.Status, $"Error: {result.Error}");
                AddLog(LogEntryKind.Error, $"Agent error: {result.Error}");
            }
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
        _orchestrator.ResetConversation();

        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "CHAT_CLEARED",
            Result = "ok"
        });

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

    private void AddMessage(ChatMessageRole role, string content)
    {
        Messages.Add(new ChatMessageViewModel
        {
            Role    = role,
            Content = content
        });
        MessageAdded?.Invoke();
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
