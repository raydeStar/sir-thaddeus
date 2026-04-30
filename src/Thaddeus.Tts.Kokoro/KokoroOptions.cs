namespace Thaddeus.Tts.Kokoro;

public sealed record KokoroOptions
{
    public string DefaultVoice { get; init; } = "bm_lewis";

    public string ModelPath { get; init; } = "";

    public string ModelVariant { get; init; } = "float16";

    public string ModelUrl { get; init; } = "";

    public string CacheDirectory { get; init; } = "";

    public string VoicesPath { get; init; } = "voices";

    public bool AutoDownloadModel { get; init; } = true;

    public float DefaultSpeed { get; init; } = 1.0f;
}
