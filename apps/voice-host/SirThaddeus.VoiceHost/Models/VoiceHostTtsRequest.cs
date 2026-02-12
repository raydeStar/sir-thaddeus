namespace SirThaddeus.VoiceHost.Models;

public sealed record VoiceHostTtsRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("voice")]
    public string Voice { get; init; } = "default";

    [System.Text.Json.Serialization.JsonPropertyName("format")]
    public string Format { get; init; } = "pcm_s16le";

    [System.Text.Json.Serialization.JsonPropertyName("sampleRate")]
    public int SampleRate { get; init; } = 24000;
}
