using Thaddeus.Runtime.Activity;
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
    private readonly IActivityLog? _activity;
    private readonly ILogger<VoiceModeController> _logger;
    private readonly object _captureLock = new();
    private CaptureSession? _activeCapture;
    private CancellationTokenSource _stopAllCts = new();

    /// <summary>Wires the controller to its dependencies.</summary>
    public VoiceModeController(
        RuntimeStateMachine stateMachine,
        ISpeechToTextProvider stt,
        ITextToSpeechProvider tts,
        ILogger<VoiceModeController> logger,
        IActivityLog? activity = null)
    {
        _stateMachine = stateMachine;
        _stt = stt;
        _tts = tts;
        _activity = activity;
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
            var startedAt = DateTimeOffset.UtcNow;
            var entry = _activity?.Append(new ActivityEntry(
                Id: InMemoryActivityLog.NewId(),
                Kind: ActivityKind.VoiceTurn,
                Summary: "Voice capture…",
                Status: ActivityStatus.Running,
                StartedAt: startedAt,
                CompletedAt: null,
                ThreadId: null,
                Detail: null));
            _activeCapture = new CaptureSession(startedAt, entry?.Id);
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
            FinishActivity(session.ActivityId, ActivityStatus.Cancelled, summary: "Voice capture (silent)", detail: null);
            return null;
        }

        _stateMachine.TryTransition(StateTrigger.UserPttReleaseCaptured, voiceMode: true);

        if (!_stt.IsAvailable)
        {
            _logger.LogWarning("voice.stt.unavailable transcript_skipped=true");
            _stateMachine.TryTransition(StateTrigger.SttDoneEmpty, voiceMode: true);
            FinishActivity(session.ActivityId, ActivityStatus.Failed, summary: "Voice capture (STT unavailable)", detail: "speech_to_text_provider_unavailable");
            return null;
        }

        try
        {
            using var linked = LinkStopAll(ct);
            var result = await _stt.TranscribeAsync(capturedPcm, linked.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Transcript))
            {
                _stateMachine.TryTransition(StateTrigger.SttDoneEmpty, voiceMode: true);
                FinishActivity(session.ActivityId, ActivityStatus.Cancelled, summary: "Voice capture (no transcript)", detail: null);
                return null;
            }
            _stateMachine.TryTransition(StateTrigger.SttDoneTranscript, voiceMode: true);
            FinishActivity(session.ActivityId, ActivityStatus.Ok, summary: SummariseTranscript(result.Transcript), detail: result.Transcript);
            return result.Transcript;
        }
        catch (OperationCanceledException)
        {
            _stateMachine.TryTransition(StateTrigger.SttDoneEmpty, voiceMode: true);
            FinishActivity(session.ActivityId, ActivityStatus.Cancelled, summary: "Voice capture (cancelled)", detail: null);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "voice.stt.failed");
            _stateMachine.TryTransition(StateTrigger.SttDoneEmpty, voiceMode: true);
            FinishActivity(session.ActivityId, ActivityStatus.Failed, summary: "Voice capture (STT error)", detail: ex.Message);
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
            using var linked = LinkStopAll(ct);
            await _tts.SpeakAsync(text, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _stateMachine.TryTransition(StateTrigger.TtsDone, voiceMode: true);
        }
    }

    /// <summary>
    /// Cancels any in-flight STT, TTS, or capture session and drives the state
    /// machine through Stopping → Idle. Safe to call from any thread, including
    /// from the global shortcut pump (spec §11.4: stop-all is the panic button).
    ///
    /// Subsequent voice operations get a fresh cancellation chain, so a stop
    /// does not poison future PTT presses.
    /// </summary>
    public void StopAll()
    {
        _logger.LogInformation("voice.stop_all requested");
        _stateMachine.TryTransition(StateTrigger.UserStopAll, voiceMode: true);

        var previous = Interlocked.Exchange(ref _stopAllCts, new CancellationTokenSource());
        try { previous.Cancel(); } catch { /* best effort */ }
        previous.Dispose();

        lock (_captureLock)
        {
            _activeCapture = null;
        }

        _stateMachine.TryTransition(StateTrigger.StoppingComplete, voiceMode: true);
    }

    private CancellationTokenSource LinkStopAll(CancellationToken external)
    {
        var stopToken = Volatile.Read(ref _stopAllCts).Token;
        return CancellationTokenSource.CreateLinkedTokenSource(external, stopToken);
    }

    private void FinishActivity(string? id, ActivityStatus status, string summary, string? detail)
    {
        if (_activity is null || id is null) return;
        _activity.Update(id, status: status, completedAt: DateTimeOffset.UtcNow, summary: summary, detail: detail);
    }

    private static string SummariseTranscript(string transcript)
    {
        var cleaned = transcript.ReplaceLineEndings(" ").Trim();
        return cleaned.Length <= 140 ? cleaned : cleaned[..140];
    }

    private sealed record CaptureSession(DateTimeOffset StartedAt, string? ActivityId);
}
