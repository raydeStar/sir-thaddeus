using System.Speech.Synthesis;
using SirThaddeus.AuditLog;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Wraps the Windows SAPI speech synthesizer for TTS output.
/// Speaks agent responses when the overlay is hidden (headless mode)
/// or whenever TTS is enabled in settings.
///
/// Threading note: SpeechSynthesizer.SpeakCompleted is posted to the
/// SynchronizationContext captured at construction time. If that context
/// is the WPF DispatcherSynchronizationContext, the event fires on the
/// UI thread — which can be delayed if the dispatcher is congested.
/// A monotonic generation counter ensures stale completions from a
/// previous SpeakAsyncCancelAll never resolve a newer speech request.
/// </summary>
public sealed class TextToSpeechService : IDisposable
{
    private readonly SpeechSynthesizer _synth;
    private readonly IAuditLogger _audit;
    private readonly object _speakGate = new();
    private bool _enabled;
    private bool _disposed;
    private bool _isSpeaking;
    private long _generation;
    private TaskCompletionSource<bool>? _activeSpeakCompletion;

    public TextToSpeechService(IAuditLogger audit, bool enabled = true)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _enabled = enabled;
        _synth = new SpeechSynthesizer();
        _synth.SetOutputToDefaultAudioDevice();
        _synth.Rate = 1;
        _synth.SpeakCompleted += OnSpeakCompleted;
    }

    /// <summary>
    /// Gets or sets whether TTS output is active.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Speaks the given text asynchronously. No-op if disabled.
    /// </summary>
    public void Speak(string text)
    {
        _ = SpeakAsync(text, CancellationToken.None);
    }

    /// <summary>
    /// Speaks the given text and completes when playback ends or is canceled.
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        if (!_enabled || _disposed || string.IsNullOrWhiteSpace(text))
            return;

        var toSpeak = PrepareSpeechText(text);
        if (string.IsNullOrWhiteSpace(toSpeak))
            return;
        TaskCompletionSource<bool> completion;

        lock (_speakGate)
        {
            // Bump generation BEFORE cancelling pending speech.
            // Any SpeakCompleted raised for the old generation will be discarded.
            _generation++;

            _synth.SpeakAsyncCancelAll();
            _isSpeaking = true;
            _activeSpeakCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _activeSpeakCompletion;
            _synth.SpeakAsync(toSpeak);
        }

        using var registration = cancellationToken.Register(() =>
        {
            lock (_speakGate)
            {
                _synth.SpeakAsyncCancelAll();
                _isSpeaking = false;
                _activeSpeakCompletion?.TrySetCanceled();
                _activeSpeakCompletion = null;
            }
        });

        try
        {
            await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation cleanly.
            throw;
        }

        _audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "TTS_SPEAK",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["length"] = toSpeak.Length,
                ["originalLength"] = text.Length
            }
        });
    }

    /// <summary>
    /// Stops any in-progress speech.
    /// </summary>
    public void Stop()
    {
        _synth.SpeakAsyncCancelAll();
        lock (_speakGate)
        {
            _generation++;
            _isSpeaking = false;
            _activeSpeakCompletion?.TrySetResult(true);
            _activeSpeakCompletion = null;
        }
    }

    public bool IsSpeaking
    {
        get
        {
            lock (_speakGate)
                return _isSpeaking;
        }
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        lock (_speakGate)
        {
            // Only complete if we're still in the same generation.
            // Stale completions from SpeakAsyncCancelAll must be discarded —
            // otherwise they prematurely resolve the next speech's TCS
            // before SAPI has actually finished speaking.
            if (_activeSpeakCompletion is null)
                return;

            _isSpeaking = false;
            _activeSpeakCompletion.TrySetResult(true);
            _activeSpeakCompletion = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _synth.SpeakCompleted -= OnSpeakCompleted;
        _synth.SpeakAsyncCancelAll();
        _synth.Dispose();
    }

    private static string PrepareSpeechText(string text)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0)
            return normalized;

        // Never speak this literal marker even if upstream text includes it.
        normalized = normalized.Replace("(truncated)", "", StringComparison.OrdinalIgnoreCase);
        normalized = string.Join(
            " ",
            normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Trim();
    }
}
