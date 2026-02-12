using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SirThaddeus.AuditLog;
using SirThaddeus.Voice;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Local-first ASR client against an OpenAI-compatible localhost endpoint.
/// </summary>
public sealed class LocalAsrHttpClient : IAsrService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Func<string> _baseUrlProvider;
    private readonly IAuditLogger _auditLogger;
    private bool _disposed;

    /// <summary>
    /// Raised each time an ASR transcript is returned.
    /// Consumers can use sessionId prefixes to distinguish preview vs final.
    /// </summary>
    public event EventHandler<AsrTranscriptReceivedEventArgs>? TranscriptReceived;

    public LocalAsrHttpClient(
        string voiceHostBaseUrl,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
        : this(() => voiceHostBaseUrl, auditLogger, handler)
    {
    }

    public LocalAsrHttpClient(
        Func<string> baseUrlProvider,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _baseUrlProvider = baseUrlProvider ?? throw new ArgumentNullException(nameof(baseUrlProvider));
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
    }

    public async Task<string> TranscribeAsync(
        VoiceAudioClip clip,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(clip);

        var endpoint = BuildEndpointUrl();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        using var payload = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(clip.AudioBytes);

        audioContent.Headers.ContentType = new MediaTypeHeaderValue(clip.ContentType);
        payload.Add(audioContent, "audio", "audio.wav");
        payload.Add(new StringContent(sessionId), "sessionId");
        request.Content = payload;

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ASR request failed ({(int)response.StatusCode}): {body}");
        }

        var transcript = ParseTranscript(body, response.Content.Headers.ContentType?.MediaType);
        TranscriptReceived?.Invoke(this, new AsrTranscriptReceivedEventArgs(sessionId, transcript));

        _auditLogger.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICE_ASR_TRANSCRIBE",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["transcriptLength"] = transcript.Length,
                ["endpoint"] = endpoint
            }
        });

        return transcript;
    }

    private static string ParseTranscript(string body, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var isJson =
            string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            body.TrimStart().StartsWith('{');

        if (!isJson)
            return body.Trim();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (TryReadString(root, "text", out var text)) return text;
            if (TryReadString(root, "transcript", out var transcript)) return transcript;
            if (TryReadString(root, "result", out var result)) return result;
            if (TryReadString(root, "output", out var output)) return output;
        }
        catch
        {
            // Fall back to raw payload text.
        }

        return body.Trim();
    }

    private static bool TryReadString(JsonElement root, string property, out string value)
    {
        value = "";
        if (!root.TryGetProperty(property, out var elem))
            return false;
        if (elem.ValueKind != JsonValueKind.String)
            return false;

        value = elem.GetString() ?? "";
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
    }

    private string BuildEndpointUrl()
    {
        var baseUrl = _baseUrlProvider.Invoke();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "http://127.0.0.1:17845";
        return baseUrl.TrimEnd('/') + "/asr";
    }
}

public sealed record AsrTranscriptReceivedEventArgs(string SessionId, string Transcript);
