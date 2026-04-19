using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using FluentIcons.Common;
using SirThaddeus.AuditLog;
using SirThaddeus.Contracts;
using System.Text;
using System.Text.Json;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private async void ConnectionStatusButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleActionDrawer(!ActionDrawer.IsVisible);
        if (ActionDrawer.IsVisible)
        {
            await RefreshActionDrawerAsync();
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

    private void CloseActionDrawerButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleActionDrawer(false);
    }

    private void OpenActionsDrawerButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleActionDrawer(true);
        _ = RefreshActionDrawerAsync();
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
        {
            return;
        }

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
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var cleaned = reason;
            if (toolName is not null)
            {
                var prefixFull = $"Use tool '{toolName}'.";
                var prefixArgs = $"Use '{toolName}': ";
                if (cleaned.Equals(prefixFull, StringComparison.Ordinal))
                {
                    return string.IsNullOrWhiteSpace(argsJson) ? "(no additional details)" : argsJson;
                }

                if (cleaned.StartsWith(prefixArgs, StringComparison.Ordinal))
                {
                    cleaned = cleaned[prefixArgs.Length..];
                }
            }

            return cleaned;
        }

        return string.IsNullOrWhiteSpace(argsJson) ? "(no additional details)" : argsJson;
    }

    private static (string Category, string Description, string Warning) ClassifyTool(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return ("Unknown", "Perform an action", "Sir Thaddeus is requesting access to a tool on your behalf.");
        }

        var lower = toolName.ToLowerInvariant();

        if (lower.Contains("memory_retrieve") || lower.Contains("memory_list"))
        {
            return ("Memory Read", "Retrieve stored memories and facts",
                "This tool will read from your local memory database.");
        }

        if (lower.Contains("memory_store") || lower.Contains("memory_update") || lower.Contains("memory_delete"))
        {
            return ("Memory Write", "Store, update, or delete memories",
                "This tool will store or modify data in your local memory database.");
        }

        if (lower.Contains("screen") || lower.Contains("screenshot") || lower.Contains("active_window"))
        {
            return ("Screen Reading", "Read content visible on your screen",
                "This tool will capture what is currently visible on your screen.");
        }

        if (lower.Contains("file_read") || lower.Contains("file_write") || lower.Contains("file_list") || lower.Contains("file_"))
        {
            return ("File System Access", "Read or write files on your computer",
                "This tool can read or write files on your computer. Review the path before allowing.");
        }

        if (lower.Contains("system_execute") || lower.Contains("execute") || lower.Contains("shell") || lower.Contains("powershell"))
        {
            return ("System Command Execution", "Run commands on your system",
                "This tool can run commands on your system. Review the details carefully before allowing.");
        }

        if (lower.Contains("web_search") || lower.Contains("browser") || lower.Contains("navigate") ||
            lower.Contains("weather") || lower.Contains("places_lookup") || lower.Contains("feed_fetch") ||
            lower.Contains("status_check") || lower.Contains("holidays"))
        {
            return ("Web Access", "Search the web and navigate to pages",
                "This tool will make an outbound internet request on your behalf.");
        }

        return ("Agent Tool", "Perform a privileged operation",
            "Sir Thaddeus is requesting access to a tool on your behalf. Choose how to proceed.");
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
        var summaryParts = new List<string>
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
}