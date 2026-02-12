using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using SirThaddeus.AuditLog;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Local-first TTS client against a localhost synthesis endpoint.
/// </summary>
public sealed class LocalTtsHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Func<string> _baseUrlProvider;
    private readonly IAuditLogger _auditLogger;
    private bool _disposed;

    public LocalTtsHttpClient(
        string voiceHostBaseUrl,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
        : this(() => voiceHostBaseUrl, auditLogger, handler)
    {
    }

    public LocalTtsHttpClient(
        Func<string> baseUrlProvider,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _baseUrlProvider = baseUrlProvider ?? throw new ArgumentNullException(nameof(baseUrlProvider));
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
    }

    public async Task<byte[]?> SynthesizeAsync(
        string text,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var endpoint = BuildEndpointUrl();
        var payloadJson = JsonSerializer.Serialize(new
        {
            text,
            voice = "default",
            format = "pcm_s16le",
            sampleRate = 24000,
            sessionId
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        await bodyStream.CopyToAsync(buffer, cancellationToken);
        var bodyBytes = buffer.ToArray();
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = TryDecodeUtf8(bodyBytes);
            throw new InvalidOperationException(
                $"TTS request failed ({(int)response.StatusCode}): {errorBody}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var audioBytes = TryExtractAudioPayload(bodyBytes, mediaType);

        _auditLogger.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICE_TTS_SYNTHESIZE",
            Result = audioBytes is null ? "empty" : "ok",
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["bytes"] = audioBytes?.Length ?? 0,
                ["endpoint"] = endpoint
            }
        });

        return audioBytes;
    }

    private static byte[]? TryExtractAudioPayload(byte[] bodyBytes, string mediaType)
    {
        if (bodyBytes.Length == 0)
            return null;

        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return bodyBytes;

        var payloadText = TryDecodeUtf8(bodyBytes);
        if (!payloadText.TrimStart().StartsWith('{'))
            return bodyBytes;

        try
        {
            using var doc = JsonDocument.Parse(payloadText);
            var root = doc.RootElement;

            if (TryDecodeBase64(root, "audioBase64", out var audioBase64))
                return audioBase64;
            if (TryDecodeBase64(root, "audio", out var audio))
                return audio;
            if (TryDecodeBase64(root, "data", out var data))
                return data;
        }
        catch
        {
            // Fall back to raw bytes.
        }

        return bodyBytes;
    }

    private static bool TryDecodeBase64(JsonElement root, string property, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!root.TryGetProperty(property, out var elem))
            return false;
        if (elem.ValueKind != JsonValueKind.String)
            return false;

        var text = elem.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            bytes = Convert.FromBase64String(text);
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string TryDecodeUtf8(byte[] bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "<binary>";
        }
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
        return baseUrl.TrimEnd('/') + "/tts";
    }
}
