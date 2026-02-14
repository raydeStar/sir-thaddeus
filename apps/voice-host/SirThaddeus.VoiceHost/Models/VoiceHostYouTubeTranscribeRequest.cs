using System.Text.Json.Serialization;

namespace SirThaddeus.VoiceHost.Models;

/// <summary>
/// Inbound/outbound YouTube transcribe payload.
/// camelCase names match both the WPF client and the Python backend contract.
/// </summary>
public sealed record VoiceHostYouTubeTranscribeRequest
{
    [JsonPropertyName("videoUrl")]
    public string VideoUrl { get; init; } = "";

    [JsonPropertyName("languageHint")]
    public string? LanguageHint { get; init; }

    [JsonPropertyName("keepAudio")]
    public bool KeepAudio { get; init; }

    [JsonPropertyName("asrProvider")]
    public string? AsrProvider { get; init; }

    [JsonPropertyName("asrModel")]
    public string? AsrModel { get; init; }
}
