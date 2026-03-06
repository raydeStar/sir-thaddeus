using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System.Globalization;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private readonly DispatcherTimer _pttStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private WindowsGlobalPushToTalkHotkeyService? _globalPttHotkeyService;
    private bool _pttTranscriptionActive;
    private CancellationTokenSource? _pttTranscriptionCancellation;
    private DateTimeOffset _pttCaptureStartedAtUtc;
    private TimeSpan _pttLastCaptureDuration;
    private string _pttLastCaptureSource = "button";
    private CancellationTokenSource? _readAloudCancellation;
    private bool _readAloudActive;

    private void InitializePushToTalkUi()
    {
        _pttStatusTimer.Tick += PttStatusTimer_Tick;

        if (OperatingSystem.IsWindows())
        {
            SetPushToTalkReadyState();
            return;
        }

        SetPushToTalkPlatformUnavailable();
    }

    private void DisposePushToTalkUi()
    {
        _pttStatusTimer.Stop();
        _pttStatusTimer.Tick -= PttStatusTimer_Tick;

        if (_globalPttHotkeyService is not null)
        {
            _globalPttHotkeyService.Pressed -= GlobalPttHotkeyService_Pressed;
            _globalPttHotkeyService.Released -= GlobalPttHotkeyService_Released;
            _globalPttHotkeyService.CancelRequested -= GlobalPttHotkeyService_CancelRequested;
            _globalPttHotkeyService.Dispose();
            _globalPttHotkeyService = null;
        }

        _pttTranscriptionCancellation?.Cancel();
        _pttTranscriptionCancellation?.Dispose();
        _pttTranscriptionCancellation = null;

        _readAloudCancellation?.Cancel();
        _readAloudCancellation?.Dispose();
        _readAloudCancellation = null;
        _readAloudActive = false;
    }

    private void TryStartGlobalPushToTalkHotkey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_globalPttHotkeyService?.IsRunning == true)
        {
            return;
        }

        _globalPttHotkeyService ??= new WindowsGlobalPushToTalkHotkeyService();
        _globalPttHotkeyService.Pressed -= GlobalPttHotkeyService_Pressed;
        _globalPttHotkeyService.Released -= GlobalPttHotkeyService_Released;
        _globalPttHotkeyService.CancelRequested -= GlobalPttHotkeyService_CancelRequested;
        _globalPttHotkeyService.Pressed += GlobalPttHotkeyService_Pressed;
        _globalPttHotkeyService.Released += GlobalPttHotkeyService_Released;
        _globalPttHotkeyService.CancelRequested += GlobalPttHotkeyService_CancelRequested;

        if (_globalPttHotkeyService.Start())
        {
            SetPushToTalkReadyState();
            return;
        }

        SetPushToTalkReadyState(_globalPttHotkeyService.FailureReason);
    }

    private bool ShouldUseWindowScopedPttHotkey()
    {
        return !OperatingSystem.IsWindows() || _globalPttHotkeyService?.IsRunning != true;
    }

    private void SetPushToTalkPlatformUnavailable()
    {
        UpdatePushToTalkUi(
            stateText: "OFF",
            badgeKey: "Surface0Brush",
            headline: "Voice capture is not available on this platform yet.",
            detail: "The button and hotkey surface are kept in the UI so Linux/macOS can pick up native capture and hotkey backends in parallel.");
        ReadAloudButton.Content = "Read Aloud";
    }

    private void SetPushToTalkReadyState(string? globalFailureReason = null)
    {
        var cancelBinding = _globalPttHotkeyService?.CancelBindingText ?? "Ctrl+Alt+Esc";
        var headline = _globalPttHotkeyService?.IsRunning == true
            ? "Voice ready. Hold Talk or press Ctrl+Alt+M anywhere on Windows."
            : "Voice ready. Hold Talk or press Ctrl+Alt+M while this window is focused.";

        var detail = _globalPttHotkeyService?.IsRunning == true
            ? $"Global hotkey active. Cancel: {cancelBinding}. Local ASR endpoint: {DescribeAsrEndpoint()}."
            : $"Global hotkey unavailable{(string.IsNullOrWhiteSpace(globalFailureReason) ? string.Empty : $": {globalFailureReason}")}. Focused hotkey fallback remains active, including cancel on {cancelBinding}. Local ASR endpoint: {DescribeAsrEndpoint()}.";

        UpdatePushToTalkUi("READY", "GreenBrush", headline, detail);
        PttHoldButton.Content = "Hold Talk";
        ReadAloudButton.Content = _readAloudActive ? "Stop Reading" : "Read Aloud";
    }

    private void SetPushToTalkBusyTranscribing()
    {
        UpdatePushToTalkUi(
            stateText: "BUSY",
            badgeKey: "YellowBrush",
            headline: "Still transcribing the previous clip.",
            detail: $"Wait for the local ASR request to finish before starting another capture. Endpoint: {DescribeAsrEndpoint()}.");
        PttHoldButton.Content = "Transcribing...";
    }

    private void MarkPushToTalkCaptureStarted(string source)
    {
        _pttLastCaptureSource = source;
        _pttCaptureStartedAtUtc = DateTimeOffset.UtcNow;
        _pttLastCaptureDuration = TimeSpan.Zero;
        _pttStatusTimer.Start();

        UpdatePushToTalkUi(
            stateText: "LISTENING",
            badgeKey: "GreenBrush",
            headline: "Listening... release to transcribe.",
            detail: $"Source: {DescribeCaptureSource(source)} | Hold: 0.0s | ASR: {DescribeAsrEndpoint()}.");
        PttHoldButton.Content = "Release";
    }

    private void MarkPushToTalkTranscribing(string source)
    {
        _pttStatusTimer.Stop();
        _pttLastCaptureDuration = _pttCaptureStartedAtUtc == default
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - _pttCaptureStartedAtUtc;

        UpdatePushToTalkUi(
            stateText: "ASR",
            badgeKey: "YellowBrush",
            headline: "Transcribing audio locally.",
            detail: $"Captured {FormatDuration(_pttLastCaptureDuration)} from {DescribeCaptureSource(source)}. Sending to {DescribeAsrEndpoint()}.");
        PttHoldButton.Content = "Transcribing...";
    }

    private void MarkPushToTalkNoAudio()
    {
        UpdatePushToTalkUi(
            stateText: "EMPTY",
            badgeKey: "RedBrush",
            headline: "No audio was captured.",
            detail: "The microphone stream ended without data. Try holding the button or hotkey a little longer.");
        PttHoldButton.Content = "Hold Talk";
    }

    private void MarkPushToTalkNoSpeech()
    {
        UpdatePushToTalkUi(
            stateText: "EMPTY",
            badgeKey: "RedBrush",
            headline: "No speech was recognized.",
            detail: $"Captured {FormatDuration(_pttLastCaptureDuration)} but the local ASR backend returned empty text.");
        PttHoldButton.Content = "Hold Talk";
    }

    private void MarkPushToTalkTranscriptInserted(string transcript)
    {
        UpdatePushToTalkUi(
            stateText: "READY",
            badgeKey: "GreenBrush",
            headline: "Transcript inserted into the composer.",
            detail: $"{transcript.Length} chars from {FormatDuration(_pttLastCaptureDuration)} via {DescribeCaptureSource(_pttLastCaptureSource)}.");
        PttHoldButton.Content = "Hold Talk";
    }

    private void MarkPushToTalkCanceled(string headline, string detail)
    {
        _pttStatusTimer.Stop();
        UpdatePushToTalkUi(
            stateText: "STOP",
            badgeKey: "YellowBrush",
            headline: headline,
            detail: string.IsNullOrWhiteSpace(detail) ? "Voice work was canceled." : detail.Trim());
        PttHoldButton.Content = "Hold Talk";
        ReadAloudButton.Content = _readAloudActive ? "Stop Reading" : "Read Aloud";
    }

    private void MarkPushToTalkFailure(string headline, string detail)
    {
        _pttStatusTimer.Stop();
        UpdatePushToTalkUi(
            stateText: "ERROR",
            badgeKey: "RedBrush",
            headline: headline,
            detail: string.IsNullOrWhiteSpace(detail) ? "Voice action failed." : detail.Trim());
        PttHoldButton.Content = "Hold Talk";
        ReadAloudButton.Content = _readAloudActive ? "Stop Reading" : "Read Aloud";
    }

    private void MarkReadAloudStarted(int characterCount)
    {
        UpdatePushToTalkUi(
            stateText: "TTS",
            badgeKey: "YellowBrush",
            headline: "Reading aloud locally.",
            detail: $"Queued {characterCount:N0} chars for Windows speech playback. Press Ctrl+Alt+Esc to stop.");
        ReadAloudButton.Content = "Stop Reading";
    }

    private void MarkReadAloudCompleted(int characterCount)
    {
        UpdatePushToTalkUi(
            stateText: "READY",
            badgeKey: "GreenBrush",
            headline: "Read aloud complete.",
            detail: $"Played {characterCount:N0} chars locally. Cancel hotkey remains Ctrl+Alt+Esc.");
        ReadAloudButton.Content = "Read Aloud";
    }

    private void UpdatePushToTalkUi(string stateText, string badgeKey, string headline, string detail)
    {
        PttStateText.Text = stateText;
        PttStateBadge.Background = (IBrush?)this.FindResource(badgeKey)
            ?? (IBrush?)this.FindResource("Surface0Brush")
            ?? Brushes.Gray;
        PttStatusText.Text = headline;
        PttDetailText.Text = detail;
    }

    private void PttStatusTimer_Tick(object? sender, EventArgs e)
    {
        if (!_pttCaptureActive || _pttCaptureStartedAtUtc == default)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _pttCaptureStartedAtUtc;
        PttDetailText.Text = $"Source: {DescribeCaptureSource(_pttLastCaptureSource)} | Hold: {FormatDuration(elapsed)} | ASR: {DescribeAsrEndpoint()}.";
    }

    private void GlobalPttHotkeyService_Pressed()
    {
        Dispatcher.UIThread.Post(() => _ = BeginPushToTalkAsync("global hotkey"));
    }

    private void GlobalPttHotkeyService_Released()
    {
        Dispatcher.UIThread.Post(() => _ = EndPushToTalkAsync("global hotkey"));
    }

    private void GlobalPttHotkeyService_CancelRequested()
    {
        Dispatcher.UIThread.Post(() => _ = RequestVoiceCancelAsync("global cancel hotkey"));
    }

    private async Task RequestVoiceCancelAsync(string source)
    {
        CancellationTokenSource? transcriptionCancellation;
        CancellationTokenSource? readAloudCancellation;
        var captureWasActive = false;
        var runId = string.Empty;

        await _pttGate.WaitAsync();
        try
        {
            captureWasActive = _pttCaptureActive || _microphoneCaptureService.IsCapturing;
            _pttCaptureActive = false;
            transcriptionCancellation = _pttTranscriptionCancellation;
            readAloudCancellation = _readAloudCancellation;
            runId = _activeRunId ?? string.Empty;
            _pttHotkeyDown = false;
        }
        finally
        {
            _pttGate.Release();
        }

        var canceledTargets = new List<string>();

        if (captureWasActive)
        {
            try
            {
                await _microphoneCaptureService.AbortCaptureAsync(CancellationToken.None);
                canceledTargets.Add("microphone capture");
            }
            catch
            {
                // Best effort only.
            }
        }

        if (transcriptionCancellation is not null && !transcriptionCancellation.IsCancellationRequested)
        {
            transcriptionCancellation.Cancel();
            canceledTargets.Add("local transcription");
        }

        if (readAloudCancellation is not null && !readAloudCancellation.IsCancellationRequested)
        {
            readAloudCancellation.Cancel();
            canceledTargets.Add("read aloud");
        }

        if (!string.IsNullOrWhiteSpace(runId) && _runtimeApiClient is not null)
        {
            try
            {
                await _runtimeApiClient.CancelRunAsync(runId, CancellationToken.None);
                canceledTargets.Add("active run");
            }
            catch
            {
                // Best effort only.
            }
        }

        if (canceledTargets.Count == 0)
        {
            return;
        }

        MarkPushToTalkCanceled(
            headline: "Voice cancel requested.",
            detail: $"Stopped {string.Join(", ", canceledTargets)} via {DescribeCaptureSource(source)}.");
        AppendTranscript($"[system] Voice cancel requested via {DescribeCaptureSource(source)}.");
    }

    private async Task ClearPushToTalkTranscriptionAsync(CancellationTokenSource? cancellation)
    {
        await _pttGate.WaitAsync();
        try
        {
            _pttTranscriptionActive = false;
            if (ReferenceEquals(_pttTranscriptionCancellation, cancellation))
            {
                _pttTranscriptionCancellation = null;
            }
        }
        finally
        {
            _pttGate.Release();
        }

        cancellation?.Dispose();
    }

    private string DescribeAsrEndpoint()
    {
        var endpoint = _transcriptionService.Endpoint;
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/"
                ? uri.Authority
                : uri.Authority + uri.AbsolutePath;
        }

        return endpoint;
    }

    private static string DescribeCaptureSource(string source)
    {
        return source.Trim().ToLowerInvariant() switch
        {
            "button" => "button hold",
            "read aloud button" => "read aloud button",
            "hotkey" => "window hotkey",
            "global hotkey" => "global hotkey",
            "window cancel hotkey" => "window cancel hotkey",
            "global cancel hotkey" => "global cancel hotkey",
            "capture_lost" => "pointer capture",
            _ => source
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0.0s";
        }

        return duration.TotalSeconds >= 1
            ? duration.TotalSeconds.ToString("0.0s", CultureInfo.InvariantCulture)
            : duration.TotalMilliseconds.ToString("0ms", CultureInfo.InvariantCulture);
    }
}

