namespace Thaddeus.Runtime.Voice;

/// <summary>
/// Speech-to-text provider abstraction. Implementations adapt sidecar processes
/// (whisper.cpp, cloud STT) to a single Task-based API. Phase 2.1 ships only the
/// stub; concrete adapters land in Phase 2.2.
/// </summary>
public interface ISpeechToTextProvider
{
    /// <summary>
    /// True if the provider can currently accept transcription requests. Stubs
    /// and unconfigured providers return false so callers can fall back to text.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Transcribes a captured audio buffer (16-bit PCM, 16kHz, mono) and returns
    /// the recognised text. Empty string is returned when the audio contained no
    /// speech; callers should treat that case as <c>SttDoneEmpty</c>.
    /// </summary>
    Task<SttResult> TranscribeAsync(ReadOnlyMemory<byte> pcm16Mono16k, CancellationToken ct);
}

/// <summary>
/// Text-to-speech provider abstraction. Implementations adapt sidecar processes
/// (Piper, cloud TTS) to a streaming API. Phase 2.1 ships only the stub.
/// </summary>
public interface ITextToSpeechProvider
{
    /// <summary>True when the provider can synthesise speech.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Synthesises and plays the given utterance. Completes when playback drains
    /// or the cancellation token fires. Implementations are expected to honour
    /// cancellation promptly so stop-all stays responsive (spec §11.4).
    /// </summary>
    Task SpeakAsync(string text, CancellationToken ct);
}

/// <summary>
/// Result of a speech-to-text call.
/// </summary>
/// <param name="Transcript">The recognised text. Empty when no speech was detected.</param>
/// <param name="DurationMs">Wall-clock duration of the recognition pass, for diagnostics.</param>
public readonly record struct SttResult(string Transcript, int DurationMs);
