using System.Speech.Synthesis;
using SirThaddeus.AuditLog;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Wraps the Windows SAPI speech synthesizer for TTS output.
/// Speaks agent responses when the overlay is hidden (headless mode)
/// or whenever TTS is enabled in settings.
/// </summary>
public sealed class TextToSpeechService : IDisposable
{
    private readonly SpeechSynthesizer _synth;
    private readonly IAuditLogger _audit;
    private bool _enabled;
    private bool _disposed;

    public TextToSpeechService(IAuditLogger audit, bool enabled = true)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _enabled = enabled;
        _synth = new SpeechSynthesizer();
        _synth.SetOutputToDefaultAudioDevice();
        _synth.Rate = 1;
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
        if (!_enabled || _disposed || string.IsNullOrWhiteSpace(text))
            return;

        // Truncate very long responses for speech
        var toSpeak = text.Length > 500 ? text[..500] + "... (truncated)" : text;

        _synth.SpeakAsyncCancelAll(); // Cancel any in-progress speech
        _synth.SpeakAsync(toSpeak);

        _audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "TTS_SPEAK",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["length"] = toSpeak.Length
            }
        });
    }

    /// <summary>
    /// Stops any in-progress speech.
    /// </summary>
    public void Stop()
    {
        _synth.SpeakAsyncCancelAll();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _synth.SpeakAsyncCancelAll();
        _synth.Dispose();
    }
}
