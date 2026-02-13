using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Local-first TTS client against a localhost synthesis endpoint.
/// </summary>
public sealed class LocalTtsHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Func<string> _baseUrlProvider;
    private readonly Func<VoiceSettings> _voiceSettingsProvider;
    private readonly IAuditLogger _auditLogger;
    private bool _disposed;
    public event EventHandler<TtsTimingEventArgs>? TimingUpdated;

    public LocalTtsHttpClient(
        string voiceHostBaseUrl,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
        : this(() => voiceHostBaseUrl, () => new VoiceSettings(), auditLogger, handler)
    {
    }

    public LocalTtsHttpClient(
        Func<string> baseUrlProvider,
        Func<VoiceSettings> voiceSettingsProvider,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _baseUrlProvider = baseUrlProvider ?? throw new ArgumentNullException(nameof(baseUrlProvider));
        _voiceSettingsProvider = voiceSettingsProvider ?? throw new ArgumentNullException(nameof(voiceSettingsProvider));
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
    }

    public LocalTtsHttpClient(
        Func<string> baseUrlProvider,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
        : this(baseUrlProvider, () => new VoiceSettings(), auditLogger, handler)
    {
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
        var voiceSettings = GetVoiceSettingsSnapshot();
        var requestId = BuildRequestId(sessionId);
        var engine = voiceSettings.GetNormalizedTtsEngine();
        var modelId = voiceSettings.GetResolvedTtsModelId();
        var voiceId = voiceSettings.GetResolvedTtsVoiceId();
        var payloadJson = JsonSerializer.Serialize(new
        {
            text,
            requestId,
            engine,
            modelId,
            voiceId,
            voice = string.IsNullOrWhiteSpace(voiceId) ? "default" : voiceId,
            format = "pcm_s16le",
            sampleRate = 24000,
            sessionId
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        var startedAt = DateTimeOffset.UtcNow;
        RaiseTimingEvent(sessionId, requestId, TtsTimingStage.Start, startedAt);
        _auditLogger.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICE_TTS_START",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["requestId"] = requestId,
                ["engine"] = engine,
                ["modelId"] = modelId,
                ["voiceId"] = voiceId,
                ["endpoint"] = endpoint
            }
        });

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
        var completedAt = DateTimeOffset.UtcNow;
        RaiseTimingEvent(sessionId, requestId, TtsTimingStage.Final, completedAt);

        _auditLogger.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICE_TTS_SYNTHESIZE",
            Result = audioBytes is null ? "empty" : "ok",
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["requestId"] = requestId,
                ["engine"] = engine,
                ["modelId"] = modelId,
                ["voiceId"] = voiceId,
                ["bytes"] = audioBytes?.Length ?? 0,
                ["elapsedMs"] = (long)Math.Round((completedAt - startedAt).TotalMilliseconds),
                ["endpoint"] = endpoint
            }
        });

        return audioBytes;
    }

    private void RaiseTimingEvent(
        string sessionId,
        string requestId,
        TtsTimingStage stage,
        DateTimeOffset timestampUtc)
    {
        try
        {
            TimingUpdated?.Invoke(this, new TtsTimingEventArgs(sessionId, requestId, stage, timestampUtc));
        }
        catch
        {
            // Timing callbacks are best-effort only.
        }
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

    private VoiceSettings GetVoiceSettingsSnapshot()
    {
        try
        {
            return _voiceSettingsProvider.Invoke() ?? new VoiceSettings();
        }
        catch
        {
            return new VoiceSettings();
        }
    }

    private static string BuildRequestId(string sessionId)
    {
        var prefix = string.IsNullOrWhiteSpace(sessionId)
            ? "tts"
            : sessionId.Trim();
        return $"{prefix}-tts-{Guid.NewGuid():N}";
    }
}

public enum TtsTimingStage
{
    Start,
    Final
}

public sealed record TtsTimingEventArgs(
    string SessionId,
    string RequestId,
    TtsTimingStage Stage,
    DateTimeOffset TimestampUtc);
