using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private DispatcherTimer? _progressStripElapsedTimer;
    private DateTime? _progressStripStartedUtc;

    private void ResetWorkflowProgressUi()
    {
        CancelProgressDrawerAutoCollapse();
        _workflowChecklistItems.Clear();
        _lastWorkflowNarration = null;
        _lastWorkflowChecklistStamp = null;
        _workflowConfidenceBand = null;
        _workflowRetryCount = 0;
        WorkflowNarrationText.Text = "Working...";
        WorkflowToolStripText.Text = string.Empty;
        WorkflowToolStripText.IsVisible = false;
        ProgressStripToolBadge.IsVisible = false;
        ProgressStripConfidenceBadge.IsVisible = false;
        ProgressStripElapsedBadge.IsVisible = false;
        ProgressStripToggleButton.IsVisible = false;
        ProgressStripToggleButton.IsChecked = false;
        SetProgressStripPulseActive(true);
        HideProgressDrawer();
    }

    private void ShowProgressDrawer()
    {
        CancelProgressDrawerAutoCollapse();
        if (!ProgressStrip.IsVisible)
        {
            _progressStripStartedUtc = DateTime.UtcNow;
            StartProgressStripElapsedTimer();
        }
        ProgressStrip.IsVisible = true;
        SetProgressStripPulseActive(true);
        UpdateProgressStripToggleVisibility();
    }

    private void HideProgressDrawer()
    {
        CancelProgressDrawerAutoCollapse();
        StopProgressStripElapsedTimer();
        _progressStripStartedUtc = null;
        ProgressStrip.IsVisible = false;
        ProgressStripToggleButton.IsChecked = false;
    }

    private async Task AutoCollapseProgressDrawerAsync()
    {
        if (!ProgressStrip.IsVisible)
        {
            return;
        }

        // When the run finishes, freeze the strip into a "summary" state for a
        // few seconds before collapsing it. This gives the user a chance to read
        // the final narration and badges, and click into the timeline.
        SetProgressStripPulseActive(false);
        StopProgressStripElapsedTimer();

        CancelProgressDrawerAutoCollapse();
        var cancellation = new CancellationTokenSource();
        _progressDrawerAutoCollapseCancellation = cancellation;

        try
        {
            await Task.Delay(6000, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(_progressDrawerAutoCollapseCancellation, cancellation) && ProgressStrip.IsVisible)
            {
                HideProgressDrawer();
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
        var toolCount = _toolCallsInCurrentRun.Count;
        if (toolCount > 0)
        {
            ProgressStripToolBadgeText.Text = toolCount == 1 ? "1 tool" : $"{toolCount} tools";
            ProgressStripToolBadge.IsVisible = true;
        }
        else
        {
            ProgressStripToolBadge.IsVisible = false;
        }

        var hasConfidence = !string.IsNullOrWhiteSpace(_workflowConfidenceBand)
            && !string.Equals(_workflowConfidenceBand, "n/a", StringComparison.OrdinalIgnoreCase);
        if (hasConfidence)
        {
            ProgressStripConfidenceBadgeText.Text = $"Confidence {_workflowConfidenceBand}";
            ProgressStripConfidenceBadge.IsVisible = true;
            ApplyConfidenceBadgeAccent(_workflowConfidenceBand);
        }
        else
        {
            ProgressStripConfidenceBadge.IsVisible = false;
        }

        // Retry count goes into the secondary line for context.
        var detailParts = new System.Collections.Generic.List<string>();
        if (_workflowRetryCount > 0)
        {
            detailParts.Add($"{_workflowRetryCount} retr{(_workflowRetryCount == 1 ? "y" : "ies")}");
        }

        WorkflowToolStripText.Text = string.Join(" • ", detailParts);
        WorkflowToolStripText.IsVisible = detailParts.Count > 0;

        UpdateProgressStripToggleVisibility();
    }

    private void UpdateProgressStripToggleVisibility()
    {
        ProgressStripToggleButton.IsVisible = _workflowChecklistItems.Count > 0;
    }

    private void StartProgressStripElapsedTimer()
    {
        if (_progressStripElapsedTimer is null)
        {
            _progressStripElapsedTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                DispatcherPriority.Background,
                ProgressStripElapsedTimer_Tick);
        }

        ProgressStripElapsedBadge.IsVisible = true;
        UpdateProgressStripElapsedText();
        _progressStripElapsedTimer.Start();
    }

    private void StopProgressStripElapsedTimer()
    {
        _progressStripElapsedTimer?.Stop();
        UpdateProgressStripElapsedText();
    }

    private void ProgressStripElapsedTimer_Tick(object? sender, EventArgs e)
    {
        UpdateProgressStripElapsedText();
    }

    private void UpdateProgressStripElapsedText()
    {
        if (_progressStripStartedUtc is null)
        {
            ProgressStripElapsedBadge.IsVisible = false;
            return;
        }

        var elapsed = DateTime.UtcNow - _progressStripStartedUtc.Value;
        ProgressStripElapsedText.Text = FormatElapsed(elapsed);
        ProgressStripElapsedBadge.IsVisible = true;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60)
        {
            return $"{elapsed.TotalSeconds:0.0}s";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        }

        return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
    }

    private void SetProgressStripPulseActive(bool active)
    {
        if (active)
        {
            ProgressStripPulse.Background = (IBrush?)this.FindResource("AccentPrimary") ?? Brushes.DodgerBlue;
        }
        else
        {
            ProgressStripPulse.Background = (IBrush?)this.FindResource("GreenBrush") ?? Brushes.MediumSeaGreen;
        }
    }

    private void ApplyConfidenceBadgeAccent(string? band)
    {
        IBrush? brush = band?.ToLowerInvariant() switch
        {
            "high" => (IBrush?)this.FindResource("GreenBrush") ?? Brushes.MediumSeaGreen,
            "medium" => (IBrush?)this.FindResource("AccentPrimary") ?? Brushes.DodgerBlue,
            "low" => (IBrush?)this.FindResource("PeachBrush") ?? Brushes.Orange,
            _ => (IBrush?)this.FindResource("TextSecondary") ?? Brushes.Gray
        };

        ProgressStripConfidenceBadgeText.Foreground = brush ?? Brushes.Gray;
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