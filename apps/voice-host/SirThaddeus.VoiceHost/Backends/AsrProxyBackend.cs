using System.Net.Http.Headers;
using System.Text.Json;

namespace SirThaddeus.VoiceHost.Backends;

public sealed class AsrProxyBackend : IAsrBackend
{
    private readonly HttpClient _httpClient;
    private readonly VoiceHostRuntimeOptions _options;

    public AsrProxyBackend(HttpClient httpClient, VoiceHostRuntimeOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<BackendReadiness> GetReadinessAsync(CancellationToken cancellationToken)
        => BackendHealthProbe.ProbeAsync(_httpClient, _options.AsrUpstreamUri, cancellationToken);

    public async Task<string> TranscribeAsync(
        IFormFile audioFile,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = audioFile.OpenReadStream();
        await using var memory = new MemoryStream();
        await sourceStream.CopyToAsync(memory, cancellationToken);
        var audioBytes = memory.ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.AsrUpstreamUri);
        using var payload = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(audioBytes);
        using var legacyAudioContent = new ByteArrayContent(audioBytes);

        audioContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(audioFile.ContentType) ? "audio/wav" : audioFile.ContentType);
        legacyAudioContent.Headers.ContentType = audioContent.Headers.ContentType;

        // Canonical VoiceHost contract field:
        payload.Add(audioContent, "audio", audioFile.FileName);
        // Compatibility field for older upstream ASR servers.
        payload.Add(legacyAudioContent, "file", audioFile.FileName);

        if (!string.IsNullOrWhiteSpace(sessionId))
            payload.Add(new StringContent(sessionId), "sessionId");

        request.Content = payload;

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new BackendProxyException(
                $"ASR upstream failed ({(int)response.StatusCode}): {body}");
        }

        return ParseTranscript(body, response.Content.Headers.ContentType?.MediaType);
    }

    private static string ParseTranscript(string body, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        var isJson =
            string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            body.TrimStart().StartsWith('{');
        if (!isJson)
            return body.Trim();

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (TryRead(root, "text", out var text)) return text;
            if (TryRead(root, "transcript", out var transcript)) return transcript;
            if (TryRead(root, "result", out var result)) return result;
            if (TryRead(root, "output", out var output)) return output;
        }
        catch
        {
            // Fall back to raw text payload.
        }

        return body.Trim();
    }

    private static bool TryRead(JsonElement root, string property, out string value)
    {
        value = "";
        if (!root.TryGetProperty(property, out var element))
            return false;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString() ?? "";
        return true;
    }

}
