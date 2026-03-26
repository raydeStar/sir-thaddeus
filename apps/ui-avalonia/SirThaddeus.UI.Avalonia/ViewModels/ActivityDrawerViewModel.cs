using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using SirThaddeus.Contracts;

namespace SirThaddeus.UI.Avalonia.ViewModels;

/// <summary>
/// View model for the trust-ledger activity drawer.
/// Bound to the drawer XAML; updated from <see cref="ActivitySummaryResponse"/>.
/// </summary>
public sealed class ActivityDrawerViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Connection status (top card) ─────────────────────────────────

    private string _connectionState = "Disconnected";
    private IBrush _connectionBrush = Brushes.Gray;
    private string _runtimeSummary = "";
    private string _runtimeEndpoint = "";

    public string ConnectionState
    {
        get => _connectionState;
        set { _connectionState = value; OnPropertyChanged(nameof(ConnectionState)); }
    }

    public IBrush ConnectionBrush
    {
        get => _connectionBrush;
        set { _connectionBrush = value; OnPropertyChanged(nameof(ConnectionBrush)); }
    }

    public string RuntimeSummary
    {
        get => _runtimeSummary;
        set { _runtimeSummary = value; OnPropertyChanged(nameof(RuntimeSummary)); }
    }

    public string RuntimeEndpoint
    {
        get => _runtimeEndpoint;
        set { _runtimeEndpoint = value; OnPropertyChanged(nameof(RuntimeEndpoint)); }
    }

    // ── Session summary ──────────────────────────────────────────────

    private int _totalToolCalls;
    private int _approvedCalls;
    private int _deniedCalls;
    private int _errorCalls;
    private string _sessionTimeRange = "";

    public int TotalToolCalls
    {
        get => _totalToolCalls;
        set { _totalToolCalls = value; OnPropertyChanged(nameof(TotalToolCalls)); OnPropertyChanged(nameof(SessionSummaryText)); }
    }

    public int ApprovedCalls
    {
        get => _approvedCalls;
        set { _approvedCalls = value; OnPropertyChanged(nameof(ApprovedCalls)); OnPropertyChanged(nameof(SessionSummaryText)); }
    }

    public int DeniedCalls
    {
        get => _deniedCalls;
        set { _deniedCalls = value; OnPropertyChanged(nameof(DeniedCalls)); OnPropertyChanged(nameof(SessionSummaryText)); }
    }

    public int ErrorCalls
    {
        get => _errorCalls;
        set { _errorCalls = value; OnPropertyChanged(nameof(ErrorCalls)); OnPropertyChanged(nameof(SessionSummaryText)); }
    }

    public string SessionTimeRange
    {
        get => _sessionTimeRange;
        set { _sessionTimeRange = value; OnPropertyChanged(nameof(SessionTimeRange)); }
    }

    public string SessionSummaryText
    {
        get
        {
            if (_totalToolCalls == 0) return "No tool calls this session";
            var parts = new List<string> { $"{_totalToolCalls} tool call{(_totalToolCalls != 1 ? "s" : "")}" };
            if (_deniedCalls > 0) parts.Add($"{_deniedCalls} denied");
            if (_errorCalls > 0) parts.Add($"{_errorCalls} error{(_errorCalls != 1 ? "s" : "")}");
            return string.Join(" · ", parts);
        }
    }

    public bool HasActivity => _totalToolCalls > 0;

    // ── Category summaries ───────────────────────────────────────────

    public ObservableCollection<ToolCategoryViewModel> Categories { get; } = [];

    // ── MCP Connections ──────────────────────────────────────────────

    public ObservableCollection<McpConnectionViewModel> Connections { get; } = [];

    // ── Selected detail ──────────────────────────────────────────────

    private ToolCallDetailViewModel? _selectedCall;

    public ToolCallDetailViewModel? SelectedCall
    {
        get => _selectedCall;
        set { _selectedCall = value; OnPropertyChanged(nameof(SelectedCall)); OnPropertyChanged(nameof(HasSelectedCall)); }
    }

    public bool HasSelectedCall => _selectedCall is not null;

    // ── Update from API response ─────────────────────────────────────

    public void UpdateFromResponse(ActivitySummaryResponse response)
    {
        // Session
        TotalToolCalls = response.Session.TotalToolCalls;
        ApprovedCalls = response.Session.ApprovedCalls;
        DeniedCalls = response.Session.DeniedCalls;
        ErrorCalls = response.Session.ErrorCalls;
        SessionTimeRange = FormatTimeRange(response.Session.FirstCallUtc, response.Session.LastCallUtc);

        // Categories
        Categories.Clear();
        foreach (var cat in response.Categories)
        {
            var vm = new ToolCategoryViewModel
            {
                CategoryKey = cat.CategoryKey,
                DisplayName = cat.DisplayName,
                TotalCalls = cat.TotalCalls,
                SucceededCalls = cat.SucceededCalls,
                DeniedCalls = cat.DeniedCalls,
                ErrorCalls = cat.ErrorCalls,
                LastCallTime = cat.LastCallUtc?.LocalDateTime.ToString("g") ?? "",
            };

            foreach (var call in cat.RecentCalls)
            {
                vm.RecentCalls.Add(new ToolCallDetailViewModel
                {
                    RequestId = call.RequestId,
                    ToolName = call.ToolName,
                    DisplayName = call.DisplayName,
                    InputSummary = call.InputSummary,
                    OutputSummary = call.OutputSummary,
                    PermissionLabel = FormatPermissionLabel(call.PermissionStatus),
                    ResultStatus = call.ResultStatus,
                    DurationMs = call.DurationMs,
                    Timestamp = call.TimestampUtc.LocalDateTime.ToString("g"),
                    AccentBrush = GetResultBrush(call.ResultStatus),
                    ErrorMessage = call.ErrorMessage,
                });
            }

            Categories.Add(vm);
        }

        // Connections
        Connections.Clear();
        foreach (var conn in response.Connections)
        {
            Connections.Add(new McpConnectionViewModel
            {
                ConnectionId = conn.ConnectionId,
                DisplayName = conn.DisplayName,
                ApprovalState = conn.ApprovalState,
                ApprovalLabel = FormatApprovalLabel(conn.ApprovalState),
                ApprovalBrush = GetApprovalBrush(conn.ApprovalState),
                ToolCount = conn.ToolCount,
                TotalCalls = conn.TotalCalls,
                LastCallTime = conn.LastCallUtc?.LocalDateTime.ToString("g") ?? "",
            });
        }

        OnPropertyChanged(nameof(HasActivity));
    }

    public void Clear()
    {
        TotalToolCalls = 0;
        ApprovedCalls = 0;
        DeniedCalls = 0;
        ErrorCalls = 0;
        SessionTimeRange = "";
        Categories.Clear();
        Connections.Clear();
        SelectedCall = null;
    }

    // ── Formatting helpers ───────────────────────────────────────────

    private static string FormatTimeRange(DateTimeOffset? first, DateTimeOffset? last)
    {
        if (first is null) return "";
        var start = first.Value.LocalDateTime.ToString("t");
        if (last is null || last == first) return start;
        return $"{start} – {last.Value.LocalDateTime:t}";
    }

    internal static string FormatPermissionLabel(string status) => status switch
    {
        "policy_always" => "Always-allow policy",
        "session_grant" => "Allowed for this session",
        "tool_exempt" => "Exempt tool",
        "granted" => "Approved",
        "denied" => "Denied",
        "not_required" => "No approval needed",
        _ => status
    };

    internal static string FormatApprovalLabel(string state) => state switch
    {
        ConnectionApprovalStates.AlwaysAllow => "Always allow",
        ConnectionApprovalStates.PerRequest => "Ask each time",
        ConnectionApprovalStates.SessionAllow => "Allowed this session",
        ConnectionApprovalStates.Revoked => "Revoked",
        ConnectionApprovalStates.Disabled => "Disabled",
        _ => state
    };

    internal static IBrush GetResultBrush(string resultStatus) => resultStatus switch
    {
        "success" or "completed" => new SolidColorBrush(Color.Parse("#4CAF50")),
        "denied" => new SolidColorBrush(Color.Parse("#FF9800")),
        "error" => new SolidColorBrush(Color.Parse("#F44336")),
        _ => Brushes.Gray,
    };

    internal static IBrush GetApprovalBrush(string approvalState) => approvalState switch
    {
        ConnectionApprovalStates.AlwaysAllow => new SolidColorBrush(Color.Parse("#4CAF50")),
        ConnectionApprovalStates.PerRequest => new SolidColorBrush(Color.Parse("#FF9800")),
        ConnectionApprovalStates.SessionAllow => new SolidColorBrush(Color.Parse("#2196F3")),
        ConnectionApprovalStates.Revoked or ConnectionApprovalStates.Disabled
            => new SolidColorBrush(Color.Parse("#9E9E9E")),
        _ => Brushes.Gray,
    };

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Child view models ────────────────────────────────────────────────

public sealed class ToolCategoryViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string CategoryKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int TotalCalls { get; init; }
    public int SucceededCalls { get; init; }
    public int DeniedCalls { get; init; }
    public int ErrorCalls { get; init; }
    public string LastCallTime { get; init; } = "";

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{TotalCalls} call{(TotalCalls != 1 ? "s" : "")}" };
            if (DeniedCalls > 0) parts.Add($"{DeniedCalls} denied");
            if (ErrorCalls > 0) parts.Add($"{ErrorCalls} error{(ErrorCalls != 1 ? "s" : "")}");
            return string.Join(" · ", parts);
        }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }

    public ObservableCollection<ToolCallDetailViewModel> RecentCalls { get; } = [];
}

public sealed class ToolCallDetailViewModel
{
    public string RequestId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string InputSummary { get; init; } = "";
    public string OutputSummary { get; init; } = "";
    public string PermissionLabel { get; init; } = "";
    public string ResultStatus { get; init; } = "";
    public long DurationMs { get; init; }
    public string Timestamp { get; init; } = "";
    public IBrush AccentBrush { get; init; } = Brushes.Gray;
    public string? ErrorMessage { get; init; }

    public string DurationText => DurationMs > 0 ? $"{DurationMs}ms" : "";

    public string TooltipText
    {
        get
        {
            var lines = new List<string>
            {
                $"Tool: {ToolName}",
                $"Status: {ResultStatus}",
                $"Permission: {PermissionLabel}",
            };
            if (!string.IsNullOrWhiteSpace(InputSummary))
                lines.Add($"Input: {InputSummary}");
            if (!string.IsNullOrWhiteSpace(OutputSummary))
                lines.Add($"Output: {OutputSummary}");
            if (DurationMs > 0)
                lines.Add($"Duration: {DurationMs}ms");
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
                lines.Add($"Error: {ErrorMessage}");
            if (!string.IsNullOrWhiteSpace(Timestamp))
                lines.Add($"Time: {Timestamp}");
            return string.Join("\n", lines);
        }
    }
}

public sealed class McpConnectionViewModel
{
    public string ConnectionId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ApprovalState { get; init; } = "";
    public string ApprovalLabel { get; init; } = "";
    public IBrush ApprovalBrush { get; init; } = Brushes.Gray;
    public int ToolCount { get; init; }
    public int TotalCalls { get; init; }
    public string LastCallTime { get; init; } = "";

    public string CallsSummary => TotalCalls > 0
        ? $"{TotalCalls} call{(TotalCalls != 1 ? "s" : "")}"
        : "No calls";

    public string TooltipText
    {
        get
        {
            var lines = new List<string>
            {
                DisplayName,
                $"Policy: {ApprovalLabel}",
                $"Tools: {ToolCount}",
                $"Calls: {TotalCalls}",
            };
            if (!string.IsNullOrWhiteSpace(LastCallTime))
                lines.Add($"Last call: {LastCallTime}");
            return string.Join("\n", lines);
        }
    }
}
