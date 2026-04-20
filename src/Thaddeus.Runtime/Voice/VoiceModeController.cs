using Thaddeus.Runtime.State;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Voice;

/// <summary>
/// Bridges PTT input and audio capture to the runtime state machine. Phase 2.1
/// only owns the orchestration; concrete audio capture, STT, and TTS arrive in
/// Phase 2.2/2.3 via <see cref="ISpeechToTextProvider"/> and
/// <see cref="ITextToSpeechProvider"/>.
/// </summary>
/// <remarks>
/// The controller never decides what state the runtime is in — it only feeds
/// triggers into <see cref="RuntimeStateMachine"/>. The state machine remains
/// the single source of truth (spec §7.1).
/// </remarks>
public sealed class VoiceModeController
{
    /// <summary>
    /// Minimum capture duration that counts as an intentional utterance. Releases
    /// shorter than this are treated as <see cref="StateTrigger.UserPttReleaseSilent"/>.
    /// Spec §11.2 calls for a 250–400ms window; we use 250ms so feather-light
    /// taps are forgiving.
    /// </summary>
    public static readonly TimeSpan MinimumCaptureDuration = TimeSpan.FromMilliseconds(250);

    private readonly RuntimeStateMachine _stateMachine;
    private readonly ISpeechToTextProvider _stt;
    private readonly ITextToSpeechProvider _tts;
    private readonly ILogger<VoiceModeController> _logger;
    private readonly object _captureLock = new();
    private CaptureSession? _activeCapture;

    /// <summary>Wires the controller to its dependencies.</summary>
    public VoiceModeController(
        RuntimeStateMachine stateMachine,
        ISpeechToTextProvider stt,
        ITextToSpeechProvider tts,
        ILogger<VoiceModeController> logger)
    {
        _stateMachine = stateMachine;
        _stt = stt;
        _tts = tts;
        _logger = logger;
    }

    /// <summary>True when voice mode can be exercised end-to-end.</summary>
    public bool IsAvailable => _stt.IsAvailable && _tts.IsAvailable;

    /// <summary>
    /// Records that the PTT key was pressed. Transitions Idle → Listening and
    /// opens a capture session. Subsequent calls before <see cref="EndPushToTalkAsync"/>
    /// are ignored so noisy key repeats don't churn the state machine.
    /// </summary>
    public void BeginPushToTalk()
    {
        lock (_captureLock)
        {
            if (_activeCapture is not null)
            {
                _logger.LogDebug("voice.ptt_press.ignored already_capturing=true");
                return;
            }
            if (!_stateMachine.TryTransition(StateTrigger.UserPttPress, voiceMode: true))
            {
                _logger.LogWarning("voice.ptt_press.illegal state={State}", _stateMachine.Current);
                return;
            }
            _activeCapture = new CaptureSession(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Records that the PTT key was released. If the capture was long enough,
    /// transitions Listening → Transcribing and runs STT. Otherwise transitions
    /// Listening → Idle without invoking STT.
    /// </summary>
    /// <returns>The transcript when STT ran and produced text; null otherwise.</returns>
    public async Task<string?> EndPushToTalkAsync(ReadOnlyMemory<byte> capturedPcm, CancellationToken ct)
    {
        CaptureSession? session;
        lock (_captureLock)
        {
            session = _activeCapture;
            _activeCapture = null;
        }

        if (session is null)
        {
            _logger.LogDebug("voice.ptt_release.no_active_capture");
            return null;
        }

        var elapsed = DateTimeOffset.UtcNow - session.StartedAt;
        if (elapsed < MinimumCaptureDuration || capturedPcm.Length == 0)
        {
            _stateMachine.TryTransition(StateTrigger.UserPttReleaseSilent, voiceMode: true);
            return null;
        }

        _stateMachine.TryTransition(StateTrigger.UserPttReleaseCaptured, voiceMode: true);

        if (!_stt.IsAvailable)
        {
            _logger.LogWarning("voice.stt.unavailable transcript_skipped=true");
            _stateMachine.TryTransition(StateTrigger.SttDoneEmpty, voiceMode: true);
            return null;
        }

        try
        {
            var result = await _stt.TranscribeAsync(capturedPcm, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Transcript))
            {
                _stateMachine.TryTransition(StateTrigger.SttDoneEmpty, voiceMode: true);
                return null;
            }
            _stateMachine.TryTransition(StateTrigger.SttDoneTranscript, voiceMode: true);
            return result.Transcript;
        }
        catch (OperationCanceledException)
        {
            _stateMachine.TryTransition(StateTrigger.SttDoneEmpty, voiceMode: true);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "voice.stt.failed");
            _stateMachine.TryTransition(StateTrigger.SttDoneEmpty, voiceMode: true);
            return null;
        }
    }

    /// <summary>
    /// Plays an utterance through the configured TTS provider. Drives the state
    /// machine into <see cref="RuntimeState.Speaking"/> only when the runtime is
    /// already in Thinking; otherwise the caller is expected to hand off via the
    /// normal <see cref="StateTrigger.PlanTextOnly"/> path before invoking us.
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (!_tts.IsAvailable)
        {
            _logger.LogWarning("voice.tts.unavailable text_length={Length}", text.Length);
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            await _tts.SpeakAsync(text, ct).ConfigureAwait(false);
        }
        finally
        {
            _stateMachine.TryTransition(StateTrigger.TtsDone, voiceMode: true);
        }
    }

    private sealed record CaptureSession(DateTimeOffset StartedAt);
}
