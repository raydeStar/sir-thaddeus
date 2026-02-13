using System.Threading.Channels;
using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Voice;

/// <summary>
/// Single-threaded voice lifecycle orchestrator.
/// The event loop is the only place allowed to change voice state.
/// </summary>
public sealed class VoiceSessionOrchestrator :
    IVoiceStateSource,
    IAsyncDisposable
{
    private static readonly Regex DictationCommandRegex = new(
        @"\b(?:comma|period|full stop|question mark|exclamation point)\b[.,!?;:]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingPunctuationNoiseRegex = new(
        @"^(?:[\s\.\,\!\?\;\:\-_/\\\(\)\[\]\{\}'""]+)+",
        RegexOptions.Compiled);

    private static readonly Regex MultiWhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled);

    private readonly IAudioCaptureService _capture;
    private readonly IAudioPlaybackService _playback;
    private readonly IAsrService _asr;
    private readonly IVoiceAgentService _agent;
    private readonly IAuditLogger _audit;
    private readonly VoiceSessionOrchestratorOptions _options;
    private readonly TimeProvider _timeProvider;

    private readonly Channel<VoiceEvent> _events =
        Channel.CreateUnbounded<VoiceEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly object _sessionGate = new();
    private CancellationTokenSource _loopCts = new();
    private Task? _loopTask;
    private bool _started;

    private VoiceState _currentState = VoiceState.Idle;
    private long _sessionCounter;
    private string? _currentSessionId;
    private CancellationTokenSource? _currentSessionCts;
    private VoiceEndReason? _pendingCancelReason;
    private string _realtimeTranscriptHint = "";
    private DateTimeOffset? _realtimeTranscriptHintAtUtc;

    public VoiceSessionOrchestrator(
        IAudioCaptureService capture,
        IAudioPlaybackService playback,
        IAsrService asr,
        IVoiceAgentService agent,
        IAuditLogger audit,
        VoiceSessionOrchestratorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _asr = asr ?? throw new ArgumentNullException(nameof(asr));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? new VoiceSessionOrchestratorOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<VoiceStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Fired at meaningful progress points (transcript ready, agent response, etc.)
    /// so the UI can display live debug text during a voice session.
    /// </summary>
    public event EventHandler<VoiceProgressEventArgs>? ProgressUpdated;

    public VoiceState CurrentState
    {
        get
        {
            lock (_sessionGate)
                return _currentState;
        }
    }

    public string? CurrentSessionId
    {
        get
        {
            lock (_sessionGate)
                return _currentSessionId;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sessionGate)
        {
            if (_started)
                return Task.CompletedTask;

            _started = true;
            _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token), cancellationToken);
        }

        WriteAudit("VOICE_ORCHESTRATOR_STARTED", "ok");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loopTask;
        lock (_sessionGate)
        {
            if (!_started)
                return;

            _started = false;
            loopTask = _loopTask;
        }

        // Route shutdown cleanup through the same event path used at runtime.
        EnqueueShutup();

        var deadline = _timeProvider.GetUtcNow() + _options.QueueDrainTimeout;
        while (CurrentState != VoiceState.Idle && _timeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(25, cancellationToken);
        }

        _loopCts.Cancel();

        try
        {
            if (loopTask is not null)
            {
                var completed = await Task.WhenAny(
                    loopTask,
                    Task.Delay(_options.QueueDrainTimeout, cancellationToken));
                if (completed != loopTask)
                    WriteAudit("VOICE_ORCHESTRATOR_STOP_TIMEOUT", "timeout");
            }
        }
        catch
        {
            // best effort on shutdown
        }

        WriteAudit("VOICE_ORCHESTRATOR_STOPPED", "ok");
    }

    public void EnqueueMicDown()
    {
        SignalSessionCancellation(VoiceEndReason.Interrupt);
        _events.Writer.TryWrite(VoiceEvent.MicDown());
    }

    public void EnqueueMicUp()
    {
        _events.Writer.TryWrite(VoiceEvent.MicUp());
    }

    public void EnqueueShutup()
    {
        SignalSessionCancellation(VoiceEndReason.Shutup);
        _events.Writer.TryWrite(VoiceEvent.Shutup());
    }

    public void EnqueueFault(string reason)
    {
        var message = string.IsNullOrWhiteSpace(reason)
            ? "Voice faulted."
            : reason.Trim();
        SignalSessionCancellation(VoiceEndReason.Fault);
        _events.Writer.TryWrite(VoiceEvent.Fault(message));
    }

    public void SetRealtimeTranscriptHint(string transcript, DateTimeOffset observedAtUtc)
    {
        var normalized = (transcript ?? "").Trim();
        lock (_sessionGate)
        {
            _realtimeTranscriptHint = normalized;
            _realtimeTranscriptHintAtUtc = string.IsNullOrWhiteSpace(normalized)
                ? null
                : observedAtUtc;
        }
    }

    private async Task RunLoopAsync(CancellationToken loopToken)
    {
        while (!loopToken.IsCancellationRequested)
        {
            VoiceEvent evt;
            try
            {
                evt = await _events.Reader.ReadAsync(loopToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                switch (evt.Type)
                {
                    case VoiceEventType.MicDown:
                        await HandleMicDownAsync(loopToken);
                        break;
                    case VoiceEventType.MicUp:
                        await HandleMicUpAsync(loopToken);
                        break;
                    case VoiceEventType.Shutup:
                        await HandleShutupAsync(loopToken);
                        break;
                    case VoiceEventType.Fault:
                        await HandleFaultAsync(evt.Detail, loopToken);
                        break;
                }
            }
            catch (Exception ex)
            {
                WriteAudit("VOICE_LOOP_ERROR", "error", new Dictionary<string, object>
                {
                    ["message"] = ex.Message
                });
                SetState(VoiceState.Faulted, "Loop error");
                await SafeEndSessionAsync(VoiceEndReason.Fault, loopToken, "loop_exception");
            }
        }
    }

    private async Task HandleMicDownAsync(CancellationToken loopToken)
    {
        if (CurrentState == VoiceState.Listening)
        {
            WriteAudit("VOICE_MICDOWN_IGNORED", "ignored", new Dictionary<string, object>
            {
                ["reason"] = "already_listening"
            });
            return;
        }

        await SafeEndSessionAsync(VoiceEndReason.Interrupt, loopToken, "micdown");

        var sessionId = BeginNewSession();
        SetState(VoiceState.Listening, "MicDown");

        try
        {
            await _capture.StartCaptureAsync(sessionId, SessionToken(sessionId));
            WriteAudit("VOICE_CAPTURE_STARTED", "ok", new Dictionary<string, object>
            {
                ["sessionId"] = sessionId
            });
        }
        catch (OperationCanceledException)
        {
            await SafeEndSessionAsync(ConsumeCancelReasonOrDefault(VoiceEndReason.Interrupt), loopToken, "capture_cancel");
        }
        catch (Exception ex)
        {
            WriteAudit("VOICE_CAPTURE_START_ERROR", "error", new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["message"] = ex.Message
            });
            SetState(VoiceState.Faulted, "Capture start failed");
            await SafeEndSessionAsync(VoiceEndReason.Fault, loopToken, "capture_start_error");
        }
    }

    private async Task HandleMicUpAsync(CancellationToken loopToken)
    {
        if (CurrentState != VoiceState.Listening)
        {
            WriteAudit("VOICE_MICUP_IGNORED", "ignored", new Dictionary<string, object>
            {
                ["reason"] = "not_listening",
                ["state"] = CurrentState.ToString()
            });
            return;
        }

        var sessionId = CurrentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            await SafeEndSessionAsync(VoiceEndReason.Fault, loopToken, "missing_session");
            return;
        }

        VoiceAudioClip? clip;
        try
        {
            clip = await _capture.StopCaptureAsync(sessionId, SessionToken(sessionId));
        }
        catch (OperationCanceledException)
        {
            await SafeEndSessionAsync(ConsumeCancelReasonOrDefault(VoiceEndReason.Interrupt), loopToken, "capture_stop_cancel");
            return;
        }
        catch (Exception ex)
        {
            WriteAudit("VOICE_CAPTURE_STOP_ERROR", "error", new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["message"] = ex.Message
            });
            SetState(VoiceState.Faulted, "Capture stop failed");
            await SafeEndSessionAsync(VoiceEndReason.Fault, loopToken, "capture_stop_error");
            return;
        }

        if (!IsCurrentSession(sessionId) || CurrentState != VoiceState.Listening)
            return;

        if (HasPendingCancellation())
        {
            await SafeEndSessionAsync(ConsumeCancelReasonOrDefault(VoiceEndReason.Interrupt), loopToken, "pending_cancel_after_stop");
            return;
        }

        if (clip is null || clip.AudioBytes.Length == 0)
        {
            WriteAudit("VOICE_EMPTY_CLIP", "ok", new Dictionary<string, object>
            {
                ["sessionId"] = sessionId
            });
            await SafeEndSessionAsync(VoiceEndReason.Complete, loopToken, "empty_clip");
            return;
        }

        SetState(VoiceState.Transcribing, "ASR");
        string transcript;
        if (TryTakeRealtimeTranscriptHint(sessionId, out var hintedTranscript, out var hintAgeMs, out var hintRejectReason))
        {
            transcript = hintedTranscript;
            WriteAudit("VOICE_ASR_HINT_ACCEPTED", "ok", new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["hintAgeMs"] = hintAgeMs,
                ["transcriptLength"] = transcript.Length
            });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(hintRejectReason))
            {
                WriteAudit("VOICE_ASR_HINT_REJECTED", "ok", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["reason"] = hintRejectReason
                });
            }

            try
            {
                transcript = await ExecuteWithTimeoutAsync(
                    ct => _asr.TranscribeAsync(clip, sessionId, ct),
                    _options.AsrTimeout,
                    SessionToken(sessionId));
            }
            catch (OperationCanceledException)
            {
                await SafeEndSessionAsync(ConsumeCancelReasonOrDefault(VoiceEndReason.Interrupt), loopToken, "asr_cancel");
                return;
            }
            catch (TimeoutException)
            {
                SetState(VoiceState.Faulted, "ASR timeout");
                await SafeEndSessionAsync(VoiceEndReason.Timeout, loopToken, "asr_timeout");
                return;
            }
            catch (Exception ex)
            {
                WriteAudit("VOICE_ASR_ERROR", "error", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["message"] = ex.Message
                });
                SetState(VoiceState.Faulted, "ASR failed");
                await SafeEndSessionAsync(VoiceEndReason.Fault, loopToken, "asr_error");
                return;
            }
        }

        if (!IsCurrentSession(sessionId) || CurrentState != VoiceState.Transcribing)
            return;

        if (HasPendingCancellation())
        {
            await SafeEndSessionAsync(ConsumeCancelReasonOrDefault(VoiceEndReason.Interrupt), loopToken, "pending_cancel_after_asr");
            return;
        }

        // Fire progress so the UI can display the recognized text live.
        FireProgress(VoiceProgressKind.TranscriptReady, transcript, sessionId);

        if (string.IsNullOrWhiteSpace(transcript))
        {
            await SafeEndSessionAsync(VoiceEndReason.Complete, loopToken, "empty_transcript");
            return;
        }

        SetState(VoiceState.Thinking, "Agent");
        var agentStartAt = DateTimeOffset.UtcNow;
        WriteAudit("VOICE_AGENT_START", "ok", new Dictionary<string, object>
        {
            ["sessionId"] = sessionId
        });
        VoiceAgentResponse response;
        try
        {
            response = await ExecuteWithTimeoutAsync(
                ct => _agent.ProcessAsync(transcript, sessionId, ct),
                _options.AgentTimeout,
                SessionToken(sessionId));
        }
        catch (OperationCanceledException)
        {
            await SafeEndSessionAsync(ConsumeCancelReasonOrDefault(VoiceEndReason.Interrupt), loopToken, "agent_cancel");
            return;
        }
        catch (TimeoutException)
        {
            SetState(VoiceState.Faulted, "Agent timeout");
            await SafeEndSessionAsync(VoiceEndReason.Timeout, loopToken, "agent_timeout");
            return;
        }
        catch (Exception ex)
        {
            WriteAudit("VOICE_AGENT_ERROR", "error", new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["message"] = ex.Message
            });
            SetState(VoiceState.Faulted, "Agent failed");
            await SafeEndSessionAsync(VoiceEndReason.Fault, loopToken, "agent_error");
            return;
        }

        WriteAudit("VOICE_AGENT_FINAL", "ok", new Dictionary<string, object>
        {
            ["sessionId"] = sessionId,
            ["elapsedMs"] = (long)Math.Round((DateTimeOffset.UtcNow - agentStartAt).TotalMilliseconds),
            ["success"] = response.Success,
            ["textLength"] = (response.Text ?? "").Length
        });

        if (!IsCurrentSession(sessionId) || CurrentState != VoiceState.Thinking)
            return;

        if (HasPendingCancellation())
        {
            await SafeEndSessionAsync(ConsumeCancelReasonOrDefault(VoiceEndReason.Interrupt), loopToken, "pending_cancel_after_agent");
            return;
        }

        var responseForVoiceUiAndSpeech = response.Text ?? response.Error ?? "";

        // Fire progress so the UI can display the agent response live.
        FireProgress(
            VoiceProgressKind.AgentResponseReady,
            responseForVoiceUiAndSpeech,
            sessionId,
            response.GuardrailsUsed,
            response.GuardrailsRationale,
            response.HasTokenUsage,
            response.TokensIn,
            response.TokensOut,
            response.ContextFillPercent);

        if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
        {
            if (!string.IsNullOrWhiteSpace(response.Error))
                WriteAudit("VOICE_AGENT_UNSUCCESSFUL", "error", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["error"] = response.Error
                });

            await SafeEndSessionAsync(
                response.Success ? VoiceEndReason.Complete : VoiceEndReason.Fault,
                loopToken,
                "agent_unsuccessful");
            return;
        }

        SetState(VoiceState.Speaking, "Playback");
        try
        {
            await ExecuteWithTimeoutAsync(
                ct => _playback.PlayTextAsync(responseForVoiceUiAndSpeech, sessionId, ct),
                _options.SpeakingTimeout,
                SessionToken(sessionId));
        }
        catch (OperationCanceledException)
        {
            await SafeEndSessionAsync(ConsumeCancelReasonOrDefault(VoiceEndReason.Interrupt), loopToken, "playback_cancel");
            return;
        }
        catch (TimeoutException)
        {
            SetState(VoiceState.Faulted, "Playback timeout");
            await SafeEndSessionAsync(VoiceEndReason.Timeout, loopToken, "playback_timeout");
            return;
        }
        catch (Exception ex)
        {
            WriteAudit("VOICE_PLAYBACK_ERROR", "error", new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["message"] = ex.Message
            });
            SetState(VoiceState.Faulted, "Playback failed");
            await SafeEndSessionAsync(VoiceEndReason.Fault, loopToken, "playback_error");
            return;
        }

        await SafeEndSessionAsync(VoiceEndReason.Complete, loopToken, "completed");
    }

    private async Task HandleShutupAsync(CancellationToken loopToken)
    {
        await SafeEndSessionAsync(VoiceEndReason.Shutup, loopToken, "shutup");
    }

    private async Task HandleFaultAsync(string reason, CancellationToken loopToken)
    {
        var message = string.IsNullOrWhiteSpace(reason)
            ? "Voice faulted."
            : reason;

        SetState(VoiceState.Faulted, message);
        FireProgress(
            VoiceProgressKind.PhaseInfo,
            message,
            CurrentSessionId ?? "");
        await SafeEndSessionAsync(VoiceEndReason.Fault, loopToken, "external_fault");
    }

    private string BeginNewSession()
    {
        lock (_sessionGate)
        {
            var next = Interlocked.Increment(ref _sessionCounter);
            _currentSessionId = $"voice-{next:D6}";
            _currentSessionCts?.Dispose();
            _currentSessionCts = new CancellationTokenSource();
            _pendingCancelReason = null;
            _realtimeTranscriptHint = "";
            _realtimeTranscriptHintAtUtc = null;
            return _currentSessionId;
        }
    }

    private void SignalSessionCancellation(VoiceEndReason reason)
    {
        lock (_sessionGate)
        {
            // Preserve the strongest operator intent.
            if (_pendingCancelReason != VoiceEndReason.Shutup)
                _pendingCancelReason = reason;
            _currentSessionCts?.Cancel();
        }
    }

    private VoiceEndReason ConsumeCancelReasonOrDefault(VoiceEndReason fallback)
    {
        lock (_sessionGate)
        {
            var reason = _pendingCancelReason ?? fallback;
            _pendingCancelReason = null;
            return reason;
        }
    }

    private bool HasPendingCancellation()
    {
        lock (_sessionGate)
            return _pendingCancelReason is not null;
    }

    private bool IsCurrentSession(string sessionId)
    {
        lock (_sessionGate)
            return string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal);
    }

    private bool TryTakeRealtimeTranscriptHint(
        string sessionId,
        out string transcript,
        out long hintAgeMs,
        out string rejectReason)
    {
        transcript = "";
        hintAgeMs = -1;
        rejectReason = "";

        DateTimeOffset? observedAt;
        lock (_sessionGate)
        {
            if (!string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
            {
                rejectReason = "session_mismatch";
                return false;
            }

            transcript = _realtimeTranscriptHint;
            observedAt = _realtimeTranscriptHintAtUtc;
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            rejectReason = "missing_hint";
            return false;
        }

        transcript = NormalizeRealtimeHintTranscript(transcript);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            rejectReason = "hint_noisy";
            return false;
        }

        if (transcript.Length < Math.Max(1, _options.RealtimeHintMinChars))
        {
            rejectReason = "hint_too_short";
            return false;
        }

        if (observedAt is null)
        {
            rejectReason = "missing_hint_timestamp";
            return false;
        }

        var age = _timeProvider.GetUtcNow() - observedAt.Value;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        hintAgeMs = (long)Math.Round(age.TotalMilliseconds);

        if (age > _options.RealtimeHintMaxAge)
        {
            rejectReason = "hint_stale";
            return false;
        }

        lock (_sessionGate)
        {
            _realtimeTranscriptHint = "";
            _realtimeTranscriptHintAtUtc = null;
        }

        return true;
    }

    private static string NormalizeRealtimeHintTranscript(string transcript)
    {
        var normalized = (transcript ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        normalized = DictationCommandRegex.Replace(normalized, " ");
        normalized = LeadingPunctuationNoiseRegex.Replace(normalized, "");
        normalized = MultiWhitespaceRegex.Replace(normalized, " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        normalized = CollapseRepeatedTokenSequences(normalized);
        normalized = MultiWhitespaceRegex.Replace(normalized, " ").Trim();
        return normalized;
    }

    private static string CollapseRepeatedTokenSequences(string transcript)
    {
        var tokens = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return transcript;

        var output = new List<string>(tokens.Length);
        var index = 0;
        while (index < tokens.Length)
        {
            var collapsed = false;
            var remaining = tokens.Length - index;
            var maxSpan = Math.Min(4, remaining / 2);

            for (var span = maxSpan; span >= 1; span--)
            {
                if (!AreEqualTokenRanges(tokens, index, span, index + span))
                    continue;

                for (var tokenIndex = 0; tokenIndex < span; tokenIndex++)
                    output.Add(tokens[index + tokenIndex]);
                index += span * 2;
                collapsed = true;
                break;
            }

            if (!collapsed)
            {
                output.Add(tokens[index]);
                index++;
            }
        }

        return string.Join(" ", output);
    }

    private static bool AreEqualTokenRanges(string[] tokens, int firstStart, int length, int secondStart)
    {
        for (var index = 0; index < length; index++)
        {
            if (!string.Equals(tokens[firstStart + index], tokens[secondStart + index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private CancellationToken SessionToken(string sessionId)
    {
        lock (_sessionGate)
        {
            if (!string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal) ||
                _currentSessionCts is null)
            {
                return new CancellationToken(true);
            }

            return _currentSessionCts.Token;
        }
    }

    private async Task SafeEndSessionAsync(
        VoiceEndReason reason,
        CancellationToken loopToken,
        string source)
    {
        string? sessionId;
        CancellationTokenSource? cts;

        lock (_sessionGate)
        {
            sessionId = _currentSessionId;
            cts = _currentSessionCts;
            _currentSessionId = null;
            _currentSessionCts = null;
            _pendingCancelReason = null;
            _realtimeTranscriptHint = "";
            _realtimeTranscriptHintAtUtc = null;
        }

        // Single cleanup path.
        try { await _playback.StopAsync(CancellationToken.None); } catch { }
        try { await _capture.AbortCaptureAsync(sessionId ?? "", CancellationToken.None); } catch { }

        try { cts?.Cancel(); } catch { }
        cts?.Dispose();

        if (CurrentState != VoiceState.Idle)
            SetState(VoiceState.Idle, $"EndSession:{reason}");

        WriteAudit("VOICE_SESSION_ENDED", "ok", new Dictionary<string, object>
        {
            ["reason"] = reason.ToString(),
            ["source"] = source,
            ["sessionId"] = sessionId ?? ""
        });

        // Keep compiler from warning about unused token in signature.
        _ = loopToken;
    }

    private void SetState(VoiceState next, string? reason = null)
    {
        VoiceState previous;
        lock (_sessionGate)
        {
            previous = _currentState;
            if (previous == next)
                return;
            _currentState = next;
        }

        StateChanged?.Invoke(this, new VoiceStateChangedEventArgs(previous, next, reason));
        WriteAudit("VOICE_STATE_CHANGE", "ok", new Dictionary<string, object>
        {
            ["from"] = previous.ToString(),
            ["to"] = next.ToString(),
            ["reason"] = reason ?? ""
        });
    }

    private async Task<T> ExecuteWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout,
        CancellationToken sessionToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            sessionToken, timeoutCts.Token);

        try
        {
            return await action(linked.Token);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !sessionToken.IsCancellationRequested)
        {
            throw new TimeoutException("Voice stage timed out.");
        }
    }

    private async Task ExecuteWithTimeoutAsync(
        Func<CancellationToken, Task> action,
        TimeSpan timeout,
        CancellationToken sessionToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            sessionToken, timeoutCts.Token);

        try
        {
            await action(linked.Token);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !sessionToken.IsCancellationRequested)
        {
            throw new TimeoutException("Voice stage timed out.");
        }
    }

    private void FireProgress(
        VoiceProgressKind kind,
        string text,
        string sessionId,
        bool guardrailsUsed = false,
        IReadOnlyList<string>? guardrailsRationale = null,
        bool hasTokenUsage = false,
        int tokensIn = 0,
        int tokensOut = 0,
        int contextFillPercent = 0)
    {
        try
        {
            ProgressUpdated?.Invoke(this, new VoiceProgressEventArgs
            {
                Kind = kind,
                Text = text,
                SessionId = sessionId,
                GuardrailsUsed = guardrailsUsed,
                GuardrailsRationale = guardrailsRationale ?? [],
                HasTokenUsage = hasTokenUsage,
                TokensIn = tokensIn,
                TokensOut = tokensOut,
                ContextFillPercent = contextFillPercent
            });
        }
        catch
        {
            // Progress events are best-effort; never break the voice loop.
        }
    }

    private void WriteAudit(
        string action,
        string result,
        Dictionary<string, object>? details = null)
    {
        try
        {
            _audit.Append(new AuditEvent
            {
                Actor = "voice",
                Action = action,
                Result = result,
                Details = details
            });
        }
        catch
        {
            // Diagnostics must never break the voice event loop.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _loopCts.Dispose();
    }
}
