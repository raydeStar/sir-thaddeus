using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
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
        {
            return;
        }

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
        {
            return;
        }

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
        {
            parts.Add($"{_toolCallsInCurrentRun.Count} tool{(_toolCallsInCurrentRun.Count == 1 ? string.Empty : "s")}");
        }

        if (_workflowRetryCount > 0)
        {
            parts.Add($"{_workflowRetryCount} retr{(_workflowRetryCount == 1 ? "y" : "ies")}");
        }

        if (!string.IsNullOrWhiteSpace(_workflowConfidenceBand) &&
            !string.Equals(_workflowConfidenceBand, "n/a", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Confidence {_workflowConfidenceBand}");
        }

        WorkflowToolStripText.Text = parts.Count > 0 ? string.Join(" | ", parts) : string.Empty;
    }

    private static string FormatCompletionReasonForDisplay(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        return reason switch
        {
            "SuccessHighConfidence" => "High confidence",
            "SuccessMediumConfidence" => "Medium confidence",
            "Timeout" => "Timed out",
            "ToolBudgetExhausted" => "Tool budget reached",
            "RetryBudgetExhausted" => "Retry budget reached",
            "BlockedByPolicy" => "Blocked by policy",
            "Cancelled" => "Cancelled",
            "Failed" => "Failed",
            _ => reason
        };
    }
}