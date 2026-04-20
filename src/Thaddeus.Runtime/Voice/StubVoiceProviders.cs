namespace Thaddeus.Runtime.Voice;

/// <summary>
/// Default STT provider used when no real speech engine is configured. Always
/// reports unavailable; <see cref="TranscribeAsync"/> throws so callers that
/// ignore <see cref="IsAvailable"/> fail loudly instead of silently dropping
/// audio.
/// </summary>
public sealed class StubSpeechToTextProvider : ISpeechToTextProvider
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task<SttResult> TranscribeAsync(ReadOnlyMemory<byte> pcm16Mono16k, CancellationToken ct)
        => throw new InvalidOperationException(
            "No speech-to-text provider is configured. Configure a sidecar in Phase 2.2.");
}

/// <summary>
/// Default TTS provider used when no real speech engine is configured. Always
/// reports unavailable; <see cref="SpeakAsync"/> throws.
/// </summary>
public sealed class StubTextToSpeechProvider : ITextToSpeechProvider
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task SpeakAsync(string text, CancellationToken ct)
        => throw new InvalidOperationException(
            "No text-to-speech provider is configured. Configure a sidecar in Phase 2.3.");
}
