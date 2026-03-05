using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SirThaddeus.Contracts;
using System.Text.Json;
using System.Net.Http;
using System.Text;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow : Window
{
    private RuntimeApiClient? _runtimeApiClient;
    private string? _activeRunId;
    private string? _pendingPermissionRequestId;
    private CancellationTokenSource? _eventStreamCancellation;
    private readonly StringBuilder _transcript = new();

    // Tab references for view switching
    private readonly ToggleButton[] _viewTabs;
    private readonly Control[] _viewPanels;

    public MainWindow()
    {
        InitializeComponent();

        // Populated after InitializeComponent so x:Name fields are resolved
        _viewTabs = [ChatTabButton, PermTabButton, AuditTabButton, SettingsTabButton];
        _viewPanels = [ChatView, PermissionsView, AuditView, SettingsView];

        // Show the empty hero, hide transcript initially
        TranscriptBox.IsVisible = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _eventStreamCancellation?.Cancel();
        _eventStreamCancellation?.Dispose();
        _eventStreamCancellation = null;
        base.OnClosed(e);
    }

    // ═══════════════════════════════════════════════════════════════
    //  TAB SWITCHING
    // ═══════════════════════════════════════════════════════════════

    private void ViewTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked)
            return;

        // Radio-style: uncheck all others, check the clicked one
        foreach (var tab in _viewTabs)
            tab.IsChecked = ReferenceEquals(tab, clicked);

        // Show/hide the corresponding panel
        for (int i = 0; i < _viewTabs.Length; i++)
            _viewPanels[i].IsVisible = _viewTabs[i].IsChecked == true;

        // Show/hide input bar — only visible for Chat
        InputBar.IsVisible = ChatTabButton.IsChecked == true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  KEYBOARD SHORTCUTS
    // ═══════════════════════════════════════════════════════════════

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        // Enter sends (unless Shift is held for newline)
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && PromptBox.IsFocused)
        {
            e.Handled = true;
            SendButton_Click(sender, e);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  CONNECTION
    // ═══════════════════════════════════════════════════════════════

    private async void ConnectButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var baseUrl = RuntimeUrlBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                ConnectionStatusText.Text = "Invalid URL";
                return;
            }

            ConnectionStatusText.Text = "Connecting…";
            ConnectionStatusText.Foreground = Brushes.White;

            var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _runtimeApiClient = new RuntimeApiClient(httpClient);
            var health = await _runtimeApiClient.GetHealthAsync(CancellationToken.None);

            ConnectionStatusText.Text = "Connected";
            ConnectionStatusText.Foreground =
                (IBrush?)this.FindResource("GreenBrush")
                ?? Brushes.LightGreen;

            SettingsRuntimeText.Text = $"Runtime: {baseUrl} ({health?.Version ?? "?"})";
        }
        catch (Exception ex)
        {
            ConnectionStatusText.Text = $"Failed: {ex.Message}";
            ConnectionStatusText.Foreground =
                (IBrush?)this.FindResource("RedBrush")
                ?? Brushes.Salmon;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  CHAT — SEND / STOP
    // ═══════════════════════════════════════════════════════════════

    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_runtimeApiClient is null)
        {
            AppendTranscript("[system] Connect to runtime first.");
            return;
        }

        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        try
        {
            AppendTranscript($"[user] {prompt}");
            var run = await _runtimeApiClient.StartRunAsync(prompt, CancellationToken.None);
            _activeRunId = run.RunId;
            AppendTranscript($"[system] Run started: {run.RunId}");
            StartEventStream(run.RunId);
            PromptBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] {ex.Message}");
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_runtimeApiClient is null || string.IsNullOrWhiteSpace(_activeRunId))
        {
            AppendTranscript("[system] No active run to cancel.");
            return;
        }

        try
        {
            var accepted = await _runtimeApiClient.CancelRunAsync(_activeRunId, CancellationToken.None);
            AppendTranscript(accepted
                ? $"[system] STOP accepted for {_activeRunId}"
                : $"[system] STOP rejected for {_activeRunId}");
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] Cancel failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  AUDIT
    // ═══════════════════════════════════════════════════════════════

    private async void RefreshAuditButton_Click(object? sender, RoutedEventArgs e)
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
            AuditList.ItemsSource = new[] { $"Audit load failed: {ex.Message}" };
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  PERMISSIONS
    // ═══════════════════════════════════════════════════════════════

    private async void ApprovePermissionButton_Click(object? sender, RoutedEventArgs e)
    {
        await SubmitPermissionDecisionAsync(true);
    }

    private async void DenyPermissionButton_Click(object? sender, RoutedEventArgs e)
    {
        await SubmitPermissionDecisionAsync(false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  INTERNALS
    // ═══════════════════════════════════════════════════════════════

    private void AppendTranscript(string line)
    {
        // Once we have content, show the transcript and hide the hero
        if (EmptyHero.IsVisible)
        {
            EmptyHero.IsVisible = false;
            TranscriptBox.IsVisible = true;
        }

        _transcript.AppendLine(line);
        TranscriptBox.Text = _transcript.ToString();
    }

    private void StartEventStream(string runId)
    {
        _eventStreamCancellation?.Cancel();
        _eventStreamCancellation?.Dispose();
        _eventStreamCancellation = new CancellationTokenSource();
        _ = Task.Run(() => StreamEventsAsync(runId, _eventStreamCancellation.Token));
    }

    private async Task StreamEventsAsync(string runId, CancellationToken cancellationToken)
    {
        if (_runtimeApiClient is null)
        {
            return;
        }

        try
        {
            await foreach (var envelope in _runtimeApiClient.StreamRunEventsAsync(runId, cancellationToken))
            {
                await Dispatcher.UIThread.InvokeAsync(() => HandleEvent(envelope));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal when a new run starts or window closes.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => AppendTranscript($"[error] Event stream failed: {ex.Message}"));
        }
    }

    private void HandleEvent(RuntimeEventEnvelope envelope)
    {
        switch (envelope.EventType)
        {
            case RuntimeEventTypes.TokenDelta:
                var token = ReadPayload<TokenDeltaPayload>(envelope.Payload);
                if (token is not null)
                {
                    AppendTranscript($"[assistant] {token.Delta}");
                }
                break;
            case RuntimeEventTypes.RunCompleted:
                AppendTranscript("[system] Run completed.");
                _activeRunId = null;
                break;
            case RuntimeEventTypes.RunFailed:
                var failure = ReadPayload<RunFailedPayload>(envelope.Payload);
                AppendTranscript($"[system] Run failed: {failure?.Error ?? "unknown"}");
                _activeRunId = null;
                break;
            case RuntimeEventTypes.ToolRequested:
                var request = ReadPayload<ToolRequestedPayload>(envelope.Payload);
                if (request is not null)
                {
                    _pendingPermissionRequestId = request.RequestId;
                    PermissionSummaryText.Text = $"Permission requested for {request.ToolName}";
                    PermissionPayloadBox.Text = request.ArgumentsJson;
                    ApprovePermissionButton.IsEnabled = true;
                    DenyPermissionButton.IsEnabled = true;
                    AppendTranscript($"[system] Permission requested: {request.ToolName}");

                    // Auto-switch to Permissions tab
                    foreach (var tab in _viewTabs)
                        tab.IsChecked = ReferenceEquals(tab, PermTabButton);
                    for (int i = 0; i < _viewTabs.Length; i++)
                        _viewPanels[i].IsVisible = _viewTabs[i].IsChecked == true;
                    InputBar.IsVisible = false;
                }
                break;
            case RuntimeEventTypes.ToolApproved:
            case RuntimeEventTypes.ToolDenied:
                var decision = ReadPayload<ToolDecisionPayload>(envelope.Payload);
                PermissionSummaryText.Text = "No pending permission requests.";
                PermissionPayloadBox.Text = string.Empty;
                _pendingPermissionRequestId = null;
                ApprovePermissionButton.IsEnabled = false;
                DenyPermissionButton.IsEnabled = false;
                if (decision is not null)
                {
                    AppendTranscript($"[system] Permission {(decision.Approved ? "approved" : "denied")} for {decision.ToolName}");
                }
                break;
            default:
                break;
        }
    }

    private async Task SubmitPermissionDecisionAsync(bool approved)
    {
        if (_runtimeApiClient is null || string.IsNullOrWhiteSpace(_pendingPermissionRequestId))
        {
            return;
        }

        try
        {
            var applied = await _runtimeApiClient.SubmitPermissionDecisionAsync(
                _pendingPermissionRequestId,
                approved,
                CancellationToken.None);
            AppendTranscript(applied
                ? $"[system] Permission decision submitted ({(approved ? "approve" : "deny")})."
                : "[system] Permission decision rejected by runtime.");

            // Switch back to Chat tab after permission decision
            foreach (var tab in _viewTabs)
                tab.IsChecked = ReferenceEquals(tab, ChatTabButton);
            for (int i = 0; i < _viewTabs.Length; i++)
                _viewPanels[i].IsVisible = _viewTabs[i].IsChecked == true;
            InputBar.IsVisible = true;
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] Failed to submit permission decision: {ex.Message}");
        }
    }

    private static T? ReadPayload<T>(object payload)
    {
        if (payload is T typed)
        {
            return typed;
        }

        if (payload is JsonElement jsonElement)
        {
            return jsonElement.Deserialize<T>();
        }

        return default;
    }

    private static string ToAuditLine(AuditEntryDto dto)
    {
        return $"{dto.TimestampUtc:O} [{dto.Category}] {dto.Message}";
    }
}
