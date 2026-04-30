namespace Thaddeus.Tts.Abstractions;

public interface ITtsEngine : IAsyncDisposable
{
    string EngineName { get; }

    Task<TtsAudio> SynthesizeAsync(
        string text,
        string voiceId,
        TtsSynthesisOptions? options = null,
        CancellationToken ct = default);

    IAsyncEnumerable<TtsAudioFrame> StreamAsync(
        string text,
        string voiceId,
        TtsSynthesisOptions? options = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<TtsVoiceInfo>> ListVoicesAsync(CancellationToken ct = default);

    Task<bool> SupportsVoiceAsync(string voiceId, CancellationToken ct = default);
}

public readonly record struct TtsAudio(
    byte[] Pcm16,
    int SampleRate,
    int Channels,
    TimeSpan Duration);

public readonly record struct TtsAudioFrame(
    ReadOnlyMemory<byte> Pcm16,
    int SampleRate,
    int Channels,
    bool IsFinal);

public sealed record TtsSynthesisOptions
{
    public float Speed { get; init; } = 1.0f;

    public string? StyleHint { get; init; }
}

public sealed record TtsVoiceInfo(
    string Id,
    string DisplayName,
    string Language,
    string? Gender,
    string? Style,
    string EngineName);
