using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
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

        // Wire pointer events with handledEventsToo so the Button's
        // built-in click handling doesn't swallow our hold-to-talk logic.
        PttHoldButton.AddHandler(
            InputElement.PointerPressedEvent,
            PttHoldButton_PointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PttHoldButton.AddHandler(
            InputElement.PointerReleasedEvent,
            PttHoldButton_PointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PttHoldButton.AddHandler(
            InputElement.PointerCaptureLostEvent,
            PttHoldButton_PointerCaptureLost,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

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

        if (_globalPttHotkeyService is not null)
        {
            _globalPttHotkeyService.Pressed -= GlobalPttHotkeyService_Pressed;
            _globalPttHotkeyService.Released -= GlobalPttHotkeyService_Released;
            _globalPttHotkeyService.CancelRequested -= GlobalPttHotkeyService_CancelRequested;
            _globalPttHotkeyService.Dispose();
            _globalPttHotkeyService = null;
        }

        _globalPttHotkeyService = new WindowsGlobalPushToTalkHotkeyService(_backendSettings.PttChord, _backendSettings.ShutupChord);
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
        SetVoiceChatStatus("Hold");
    }

    private void SetPushToTalkReadyState(string? globalFailureReason = null)
    {
        var pttBinding = _globalPttHotkeyService?.BindingText ?? _backendSettings.PttChord;
        var cancelBinding = _globalPttHotkeyService?.CancelBindingText ?? _backendSettings.ShutupChord;
        var headline = _globalPttHotkeyService?.IsRunning == true
            ? $"Voice ready. Hold Talk or press {pttBinding} anywhere on Windows."
            : $"Voice ready. Hold Talk or press {pttBinding} while this window is focused.";

        var detail = _globalPttHotkeyService?.IsRunning == true
            ? $"Global hotkey active. Cancel: {cancelBinding}. Local ASR endpoint: {DescribeAsrEndpoint()}."
            : $"Global hotkey unavailable{(string.IsNullOrWhiteSpace(globalFailureReason) ? string.Empty : $": {globalFailureReason}")}. Focused hotkey fallback remains active, including cancel on {cancelBinding}. Local ASR endpoint: {DescribeAsrEndpoint()}.";

        UpdatePushToTalkUi("READY", "GreenBrush", headline, detail);
        SetVoiceChatStatus("Hold");
    }

    private void SetPushToTalkBusyTranscribing()
    {
        UpdatePushToTalkUi(
            stateText: "BUSY",
            badgeKey: "YellowBrush",
            headline: "Still transcribing the previous clip.",
            detail: $"Wait for the local ASR request to finish before starting another capture. Endpoint: {DescribeAsrEndpoint()}.");
        SetVoiceChatStatus("Transcribing...");
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
        SetVoiceChatStatus("Listening...");
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
        SetVoiceChatStatus("Processing...");
    }

    private void MarkPushToTalkNoAudio()
    {
        UpdatePushToTalkUi(
            stateText: "EMPTY",
            badgeKey: "RedBrush",
            headline: "No audio was captured.",
            detail: "The microphone stream ended without data. Try holding the button or hotkey a little longer.");
        SetVoiceChatStatus("Hold");
    }

    private void MarkPushToTalkNoSpeech()
    {
        UpdatePushToTalkUi(
            stateText: "EMPTY",
            badgeKey: "RedBrush",
            headline: "No speech was recognized.",
            detail: $"Captured {FormatDuration(_pttLastCaptureDuration)} but the local ASR backend returned empty text.");
        SetVoiceChatStatus("Hold");
    }

    private void MarkPushToTalkTranscriptInserted(string transcript)
    {
        UpdatePushToTalkUi(
            stateText: "READY",
            badgeKey: "GreenBrush",
            headline: "Transcript inserted into the composer.",
            detail: $"{transcript.Length} chars from {FormatDuration(_pttLastCaptureDuration)} via {DescribeCaptureSource(_pttLastCaptureSource)}.");
        SetVoiceChatStatus("Processing...");
    }

    private void MarkPushToTalkCanceled(string headline, string detail)
    {
        _pttStatusTimer.Stop();
        UpdatePushToTalkUi(
            stateText: "STOP",
            badgeKey: "YellowBrush",
            headline: headline,
            detail: string.IsNullOrWhiteSpace(detail) ? "Voice work was canceled." : detail.Trim());
        SetVoiceChatStatus("Hold");
    }

    private void MarkPushToTalkFailure(string headline, string detail)
    {
        _pttStatusTimer.Stop();
        UpdatePushToTalkUi(
            stateText: "ERROR",
            badgeKey: "RedBrush",
            headline: headline,
            detail: string.IsNullOrWhiteSpace(detail) ? "Voice action failed." : detail.Trim());
        SetVoiceChatStatus("Hold");
    }

    private void MarkReadAloudStarted(int characterCount)
    {
        UpdatePushToTalkUi(
            stateText: "TTS",
            badgeKey: "YellowBrush",
            headline: "Reading aloud locally.",
            detail: $"Queued {characterCount:N0} chars for Windows speech playback. Press {_backendSettings.ShutupChord} to stop.");
        SetVoiceChatStatus("Speaking");
    }

    private void MarkReadAloudCompleted(int characterCount)
    {
        UpdatePushToTalkUi(
            stateText: "READY",
            badgeKey: "GreenBrush",
            headline: "Read aloud complete.",
            detail: $"Played {characterCount:N0} chars locally. Cancel hotkey remains {_backendSettings.ShutupChord}.");
        SetVoiceChatStatus("Hold");
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
        // No chat card — the button state change is sufficient feedback.
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

    private void PttHoldButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!PttHoldButton.IsEnabled)
        {
            return;
        }

        if (IsVoiceResponseActive())
        {
            _pttInterruptTapArmed = true;
            e.Handled = true;
            _ = RequestVoiceCancelAsync("button tap interrupt");
            return;
        }

        e.Pointer.Capture(PttHoldButton);
        e.Handled = true;
        _ = BeginPushToTalkAsync("button");
    }

    private void PttHoldButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pttInterruptTapArmed)
        {
            _pttInterruptTapArmed = false;
            e.Handled = true;
            return;
        }

        if (e.Pointer.Captured == PttHoldButton)
        {
            e.Pointer.Capture(null);
        }

        e.Handled = true;
        _ = EndPushToTalkAsync("button");
    }

    private void PttHoldButton_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_pttInterruptTapArmed)
        {
            _pttInterruptTapArmed = false;
            return;
        }

        _ = EndPushToTalkAsync("capture_lost");
    }

    private async Task BeginPushToTalkAsync(string source)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (IsVoiceResponseActive())
        {
            await RequestVoiceCancelAsync($"{source} interrupt");
            return;
        }

        await _pttGate.WaitAsync();
        try
        {
            if (_pttCaptureActive)
            {
                return;
            }

            if (_pttTranscriptionActive)
            {
                SetPushToTalkBusyTranscribing();
                return;
            }

            await _microphoneCaptureService.StartCaptureAsync(CancellationToken.None);
            _pttCaptureActive = true;
            MarkPushToTalkCaptureStarted(source);
        }
        catch (Exception ex)
        {
            _pttCaptureActive = false;
            MarkPushToTalkFailure("PTT start failed.", ex.Message);
            AppendTranscript("[error] PTT start failed: " + ex.Message);
        }
        finally
        {
            _pttGate.Release();
        }
    }

    private async Task EndPushToTalkAsync(string source)
    {
        byte[]? wavBytes;
        CancellationTokenSource? transcriptionCancellation = null;

        await _pttGate.WaitAsync();
        try
        {
            if (!_pttCaptureActive)
            {
                return;
            }

            _pttCaptureActive = false;
            _pttTranscriptionActive = true;
            transcriptionCancellation = new CancellationTokenSource();
            _pttTranscriptionCancellation = transcriptionCancellation;
            MarkPushToTalkTranscribing(source);
            wavBytes = await _microphoneCaptureService.StopCaptureAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _pttTranscriptionActive = false;
            if (ReferenceEquals(_pttTranscriptionCancellation, transcriptionCancellation))
            {
                _pttTranscriptionCancellation = null;
            }

            transcriptionCancellation?.Dispose();
            MarkPushToTalkFailure("PTT stop failed.", ex.Message);
            AppendTranscript("[error] PTT stop failed: " + ex.Message);
            return;
        }
        finally
        {
            _pttGate.Release();
        }

        if (wavBytes is null || wavBytes.Length == 0)
        {
            await ClearPushToTalkTranscriptionAsync(transcriptionCancellation);
            MarkPushToTalkNoAudio();
            return;
        }

        try
        {
            var sessionId = $"ui-ptt-{Interlocked.Increment(ref _pttSessionCounter)}";
            var transcript = (await _transcriptionService.TranscribeAsync(
                wavBytes,
                sessionId,
                transcriptionCancellation?.Token ?? CancellationToken.None)).Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                MarkPushToTalkNoSpeech();
                return;
            }

            var existing = PromptBox.Text;
            PromptBox.Text = string.IsNullOrWhiteSpace(existing)
                ? transcript
                : existing.TrimEnd() + " " + transcript;
            PromptBox.CaretIndex = PromptBox.Text.Length;
            MarkPushToTalkTranscriptInserted(transcript);

            var fullPrompt = PromptBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(fullPrompt))
            {
                PromptBox.Text = string.Empty;
                await ClearPushToTalkTranscriptionAsync(transcriptionCancellation);
                transcriptionCancellation = null;
                await SubmitPromptAsync(fullPrompt, voiceInitiated: true);
                return;
            }
        }
        catch (OperationCanceledException) when (transcriptionCancellation?.IsCancellationRequested == true)
        {
            MarkPushToTalkCanceled(
                headline: "Transcription canceled.",
                detail: $"The local ASR request for {DescribeCaptureSource(_pttLastCaptureSource)} was canceled before the composer changed.");
        }
        catch (Exception ex)
        {
            MarkPushToTalkFailure("Transcription failed.", ex.Message);
            AppendTranscript("[error] PTT transcription failed: " + ex.Message);
        }
        finally
        {
            await ClearPushToTalkTranscriptionAsync(transcriptionCancellation);
        }
    }

    private static readonly string[] PttStateClasses = ["pttListening", "pttProcessing", "pttSpeaking", "pttResponding"];

    private void SetVoiceChatStatus(string state, string? _detail = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(state) ? "Hold" : state.Trim();

        Symbol iconSymbol;
        string? cssClass;
        string brushKey;
        switch (trimmed)
        {
            case "Listening...":
                _voiceStatusLabel = "Listening";
                iconSymbol = Symbol.Mic;
                cssClass = "pttListening";
                brushKey = "AccentPrimary";
                break;
            case "Processing...":
            case "Transcribing...":
                _voiceStatusLabel = "Working";
                iconSymbol = Symbol.Scan;
                cssClass = "pttProcessing";
                brushKey = "TextSecondary";
                break;
            case "Speaking":
                _voiceStatusLabel = "Speaking";
                iconSymbol = Symbol.SpeakerSettings;
                cssClass = "pttSpeaking";
                brushKey = "TextSecondary";
                break;
            case "Responding...":
                _voiceStatusLabel = "Responding";
                iconSymbol = Symbol.Send;
                cssClass = "pttResponding";
                brushKey = "AccentPrimary";
                break;
            default:
                _voiceStatusLabel = PttHoldButton.IsEnabled ? "Ready" : "Unavailable";
                iconSymbol = Symbol.Mic;
                cssClass = null;
                brushKey = PttHoldButton.IsEnabled ? "TextSecondary" : "TextTertiary";
                break;
        }

        PttHoldButton.Content = new SymbolIcon
        {
            Symbol = iconSymbol,
            FontSize = 20,
            Foreground = ResolveThemeBrush(brushKey, Brushes.LightGray)
        };
        ToolTip.SetTip(PttHoldButton, string.Equals(trimmed, "Hold", StringComparison.Ordinal)
            ? "Hold to talk"
            : trimmed);

        foreach (var cls in PttStateClasses)
        {
            PttHoldButton.Classes.Set(cls, cls == cssClass);
        }

        UpdateRuntimeStatusStrip();
    }

    private bool IsVoiceResponseActive()
    {
        return _readAloudActive || !string.IsNullOrWhiteSpace(_activeRunId);
    }

    private static bool IsConfiguredHotkeyDown(KeyEventArgs e, string chord)
    {
        if (!TryParseUiChord(chord, out var triggerKey, out var modifiers))
        {
            return false;
        }

        return e.Key == triggerKey && ModifiersMatch(e.KeyModifiers, modifiers);
    }

    private static bool IsConfiguredHotkeyTriggerKey(Key key, string chord)
    {
        return TryParseUiChord(chord, out var triggerKey, out _) && key == triggerKey;
    }

    private static bool ModifiersMatch(KeyModifiers actual, KeyModifiers expected)
    {
        var flags = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta;
        return (actual & flags) == (expected & flags);
    }

    private static bool TryParseUiChord(string? chord, out Key triggerKey, out KeyModifiers modifiers)
    {
        triggerKey = Key.None;
        modifiers = KeyModifiers.None;

        if (string.IsNullOrWhiteSpace(chord))
        {
            return false;
        }

        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var token = parts[i];
            if (token.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Control;
            }
            else if (token.Equals("alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Alt;
            }
            else if (token.Equals("shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Shift;
            }
            else if (token.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("meta", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Meta;
            }
        }

        return TryParseUiKey(parts[^1], out triggerKey);
    }

    private static bool TryParseUiKey(string token, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var normalized = token.Trim();

        if ((normalized.StartsWith('F') || normalized.StartsWith('f')) &&
            int.TryParse(normalized[1..], out var fn) &&
            fn is >= 1 and <= 24)
        {
            key = (Key)((int)Key.F1 + (fn - 1));
            return true;
        }

        if (normalized.Length == 1)
        {
            var ch = char.ToUpperInvariant(normalized[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                key = Enum.Parse<Key>(ch.ToString(), ignoreCase: true);
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                key = (Key)((int)Key.D0 + (ch - '0'));
                return true;
            }
        }

        if (normalized.Equals("escape", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("esc", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Escape;
            return true;
        }

        if (normalized.Equals("space", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Space;
            return true;
        }

        if (normalized.Equals("enter", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Enter;
            return true;
        }

        if (normalized.Equals("tab", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Tab;
            return true;
        }

        if (normalized.Equals("backspace", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Back;
            return true;
        }

        return false;
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

