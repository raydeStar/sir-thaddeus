using System.Net.Http.Headers;
using System.Text.Json;

namespace SirThaddeus.UI.Avalonia;

internal sealed class LocalAsrHttpTranscriptionService : ISpeechTranscriptionService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Func<string> _asrUrlProvider;
    private bool _disposed;

    public LocalAsrHttpTranscriptionService(string asrUrl = "http://127.0.0.1:17845/asr", HttpMessageHandler? handler = null)
        : this(() => asrUrl, handler)
    {
    }

    public LocalAsrHttpTranscriptionService(Func<string> asrUrlProvider, HttpMessageHandler? handler = null)
    {
        _asrUrlProvider = asrUrlProvider ?? throw new ArgumentNullException(nameof(asrUrlProvider));
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(45);
    }

    public string Endpoint => ResolveEndpoint();

    public async Task<string> TranscribeAsync(byte[] wavBytes, string sessionId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (wavBytes is null || wavBytes.Length == 0)
        {
            return string.Empty;
        }

        var endpoint = ResolveEndpoint();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        using var payload = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(wavBytes);

        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        payload.Add(audioContent, "audio", "audio.wav");
        payload.Add(new StringContent(sessionId), "sessionId");
        payload.Add(new StringContent("faster-whisper"), "engine");
        payload.Add(new StringContent("base"), "modelId");

        request.Content = payload;

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = string.IsNullOrWhiteSpace(body)
                ? $"ASR request failed ({(int)response.StatusCode})."
                : $"ASR request failed ({(int)response.StatusCode}): {body.Trim()}";
            throw new InvalidOperationException(detail);
        }

        return ParseTranscript(body, response.Content.Headers.ContentType?.MediaType);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    private string ResolveEndpoint()
    {
        var value = _asrUrlProvider();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "http://127.0.0.1:17845/asr";
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains("/asr", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.TrimEnd('/') + "/asr";
        }

        return trimmed;
    }

    private static string ParseTranscript(string payload, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        var looksJson = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                        || payload.TrimStart().StartsWith('{');
        if (!looksJson)
        {
            return payload.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (TryRead(root, "text", out var text)) return text;
            if (TryRead(root, "transcript", out var transcript)) return transcript;
            if (TryRead(root, "result", out var result)) return result;
            if (TryRead(root, "output", out var output)) return output;
        }
        catch
        {
            // Fall through to raw payload.
        }

        return payload.Trim();
    }

    private static bool TryRead(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }
}

