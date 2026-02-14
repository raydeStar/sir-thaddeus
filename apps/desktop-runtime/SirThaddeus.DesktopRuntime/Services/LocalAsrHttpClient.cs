using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Voice;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Local-first ASR client against an OpenAI-compatible localhost endpoint.
/// </summary>
public sealed class LocalAsrHttpClient : IAsrService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Func<string> _baseUrlProvider;
    private readonly Func<VoiceSettings> _voiceSettingsProvider;
    private readonly IAuditLogger _auditLogger;
    private bool _disposed;

    /// <summary>
    /// Raised each time an ASR transcript is returned.
    /// Consumers can use sessionId prefixes to distinguish preview vs final.
    /// </summary>
    public event EventHandler<AsrTranscriptReceivedEventArgs>? TranscriptReceived;
    public event EventHandler<AsrTimingEventArgs>? TimingUpdated;

    public LocalAsrHttpClient(
        string voiceHostBaseUrl,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
        : this(() => voiceHostBaseUrl, () => new VoiceSettings(), auditLogger, handler)
    {
    }

    public LocalAsrHttpClient(
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

    public LocalAsrHttpClient(
        Func<string> baseUrlProvider,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
        : this(baseUrlProvider, () => new VoiceSettings(), auditLogger, handler)
    {
    }

    public async Task<string> TranscribeAsync(
        VoiceAudioClip clip,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(clip);

        var endpoint = BuildEndpointUrl();
        var voiceSettings = GetVoiceSettingsSnapshot();
        var configuredSttEngine = voiceSettings.GetNormalizedSttEngine();
        // Keep interactive/front-end ASR pinned to faster-whisper.
        // Qwen ASR is reserved for explicit transcription workflows.
        var sttEngine = "faster-whisper";
        var sttModelId = ResolveFrontendSttModelId(voiceSettings, configuredSttEngine);
        var sttLanguage = voiceSettings.GetResolvedSttLanguage();

        var requestId = BuildRequestId(sessionId);
        var startedAt = DateTimeOffset.UtcNow;

        RaiseTimingEvent(sessionId, requestId, AsrTimingStage.Start, startedAt);
        _auditLogger.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICE_ASR_START",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["requestId"] = requestId,
                ["configuredEngine"] = configuredSttEngine,
                ["engine"] = sttEngine,
                ["modelId"] = sttModelId,
                ["language"] = string.IsNullOrWhiteSpace(sttLanguage) ? "auto" : sttLanguage,
                ["endpoint"] = endpoint
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        using var payload = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(clip.AudioBytes);

        audioContent.Headers.ContentType = new MediaTypeHeaderValue(clip.ContentType);
        payload.Add(audioContent, "audio", "audio.wav");
        payload.Add(new StringContent(sessionId), "sessionId");
        payload.Add(new StringContent(sttEngine), "engine");
        if (!string.IsNullOrWhiteSpace(sttModelId))
            payload.Add(new StringContent(sttModelId), "modelId");
        if (!string.IsNullOrWhiteSpace(sttLanguage))
        {
            payload.Add(new StringContent(sttLanguage), "sttLanguage");
            payload.Add(new StringContent(sttLanguage), "language");
        }
        payload.Add(new StringContent(requestId), "requestId");
        request.Content = payload;
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

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
        var completedAt = DateTimeOffset.UtcNow;
        RaiseTimingEvent(sessionId, requestId, AsrTimingStage.FirstToken, completedAt, "final_response");
        RaiseTimingEvent(sessionId, requestId, AsrTimingStage.Final, completedAt);

        _auditLogger.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICE_ASR_FIRST_TOKEN",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["requestId"] = requestId,
                ["source"] = "final_response",
                ["elapsedMs"] = (long)Math.Round((completedAt - startedAt).TotalMilliseconds)
            }
        });
        _auditLogger.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICE_ASR_FINAL",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["requestId"] = requestId,
                ["elapsedMs"] = (long)Math.Round((completedAt - startedAt).TotalMilliseconds),
                ["transcriptLength"] = transcript.Length
            }
        });

        TranscriptReceived?.Invoke(this, new AsrTranscriptReceivedEventArgs(sessionId, transcript));

        _auditLogger.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICE_ASR_TRANSCRIBE",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["requestId"] = requestId,
                ["configuredEngine"] = configuredSttEngine,
                ["engine"] = sttEngine,
                ["modelId"] = sttModelId,
                ["language"] = string.IsNullOrWhiteSpace(sttLanguage) ? "auto" : sttLanguage,
                ["transcriptLength"] = transcript.Length,
                ["endpoint"] = endpoint
            }
        });

        return transcript;
    }

    private void RaiseTimingEvent(
        string sessionId,
        string requestId,
        AsrTimingStage stage,
        DateTimeOffset timestampUtc,
        string source = "")
    {
        try
        {
            TimingUpdated?.Invoke(
                this,
                new AsrTimingEventArgs(sessionId, requestId, stage, timestampUtc, source));
        }
        catch
        {
            // Timing notifications are best-effort and must not affect ASR calls.
        }
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

    private static string ResolveFrontendSttModelId(VoiceSettings voiceSettings, string configuredSttEngine)
    {
        if (string.Equals(configuredSttEngine, "faster-whisper", StringComparison.OrdinalIgnoreCase))
        {
            var configuredModel = voiceSettings.GetResolvedSttModelId();
            return string.IsNullOrWhiteSpace(configuredModel) ? "base" : configuredModel;
        }

        return "base";
    }

    private static string BuildRequestId(string sessionId)
    {
        var prefix = string.IsNullOrWhiteSpace(sessionId)
            ? "asr"
            : sessionId.Trim();
        return $"{prefix}-asr-{Guid.NewGuid():N}";
    }
}

public sealed record AsrTranscriptReceivedEventArgs(string SessionId, string Transcript);
public enum AsrTimingStage
{
    Start,
    FirstToken,
    Final
}public sealed record AsrTimingEventArgs(
    string SessionId,
    string RequestId,
    AsrTimingStage Stage,
    DateTimeOffset TimestampUtc,
    string Source = "");
