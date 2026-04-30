namespace Thaddeus.Tts.Piper.Legacy;

public sealed record PiperOptions
{
    public string? BinaryPath { get; init; }

    public string? VoiceModelPath { get; init; }

    public int? SpeakerId { get; init; }

    public TimeSpan SynthesisTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int SampleRate { get; init; } = 22_050;

    public int Channels { get; init; } = 1;
}
