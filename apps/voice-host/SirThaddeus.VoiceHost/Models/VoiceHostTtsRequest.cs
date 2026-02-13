namespace SirThaddeus.VoiceHost.Models;

public sealed record VoiceHostTtsRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("requestId")]
    public string RequestId { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("engine")]
    public string Engine { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("modelId")]
    public string ModelId { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("voiceId")]
    public string VoiceId { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("voice")]
    public string Voice { get; init; } = "default";

    [System.Text.Json.Serialization.JsonPropertyName("format")]
    public string Format { get; init; } = "pcm_s16le";

    [System.Text.Json.Serialization.JsonPropertyName("sampleRate")]
    public int SampleRate { get; init; } = 24000;
}
