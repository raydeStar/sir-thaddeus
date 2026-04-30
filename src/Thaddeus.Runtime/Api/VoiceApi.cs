using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.Voice;

namespace Thaddeus.Runtime.Api;

/// <summary>Routes for discovering voice catalogs and probing VoiceHost health.</summary>
public static class VoiceApi
{
    private static readonly HttpClient TtsHttp = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly HttpClient AsrHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static IEndpointRouteBuilder MapVoiceApi(this IEndpointRouteBuilder app)
    {
        // GET /api/voice/piper-voices — curated + filesystem-discovered Piper voices.
        app.MapGet("/api/voice/piper-voices", () =>
        {
            var response = new PiperVoicesResponse(PiperVoiceCatalog.Discover());
            return Results.Json(response, VoiceJsonContext.Default.PiperVoicesResponse);
        });

        // GET /api/voice/host-health — probes or warms the configured VoiceHost base URL.
        app.MapGet("/api/voice/host-health", async (bool? ensure, VoiceRuntimeStatusService voiceStatus, CancellationToken ct) =>
        {
            var status = await voiceStatus.GetStatusAsync(ensureHost: ensure == true, ct).ConfigureAwait(false);
            return Results.Json(
                VoiceHostHealthResponse.FromStatus(status),
                VoiceJsonContext.Default.VoiceHostHealthResponse);
        });

        app.MapPost("/api/voice/warmup", async (VoiceRuntimeStatusService voiceStatus, CancellationToken ct) =>
        {
            var status = await voiceStatus.GetStatusAsync(ensureHost: true, ct).ConfigureAwait(false);
            return Results.Json(
                VoiceHostHealthResponse.FromStatus(status),
                VoiceJsonContext.Default.VoiceHostHealthResponse);
        });

        app.MapPost("/api/voice/tts", async (
            VoiceTtsRequest? req,
            HttpContext context,
            ISettingsStore store,
            VoiceHostProcessSupervisor voiceHost,
            CancellationToken ct) =>
        {
            var text = req?.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
                return Results.BadRequest(new VoiceTtsErrorResponse("text_required", "Text is required."));

            var doc = await store.GetAsync(ct).ConfigureAwait(false);
            if (!doc.Audio.TtsEnabled)
            {
                return Results.Json(
                    new VoiceTtsErrorResponse("tts_disabled", "Text-to-speech is disabled in Audio settings."),
                    VoiceJsonContext.Default.VoiceTtsErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (!doc.Voice.VoiceHostEnabled)
            {
                return Results.Json(
                    new VoiceTtsErrorResponse("voice_host_disabled", "Local VoiceHost is disabled in Voice settings."),
                    VoiceJsonContext.Default.VoiceTtsErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (UsesDisabledTtsProvider(doc.Voice.TtsProvider))
            {
                return Results.Json(
                    new VoiceTtsErrorResponse("tts_provider_disabled", "No text-to-speech provider is selected."),
                    VoiceJsonContext.Default.VoiceTtsErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (!TryBuildVoiceHostEndpoint(doc.Voice.VoiceHostBaseUrl, "/tts", out var ttsEndpoint, out var endpointError))
            {
                return Results.BadRequest(new VoiceTtsErrorResponse("voice_host_url_invalid", endpointError));
            }

            var hostEnsure = await voiceHost.EnsureResponsiveAsync(ttsEndpoint, doc.Voice, ct).ConfigureAwait(false);
            if (!hostEnsure.Success)
            {
                return Results.Json(
                    new VoiceTtsErrorResponse(hostEnsure.ErrorCode, hostEnsure.Message),
                    VoiceJsonContext.Default.VoiceTtsErrorResponse,
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var requestId = string.IsNullOrWhiteSpace(req?.RequestId)
                ? "chat-tts-" + Guid.NewGuid().ToString("N")[..12]
                : req!.RequestId.Trim();
            var voiceId = doc.Voice.TtsVoiceId?.Trim() ?? string.Empty;
            var payload = new VoiceHostTtsProxyRequest(
                Text: text,
                RequestId: requestId,
                Engine: doc.Voice.TtsProvider?.Trim() ?? string.Empty,
                ModelId: doc.Voice.TtsModelId?.Trim() ?? string.Empty,
                VoiceId: voiceId,
                Voice: string.IsNullOrWhiteSpace(voiceId) ? "default" : voiceId,
                Format: "pcm_s16le",
                SampleRate: 24000);

            using var outbound = new HttpRequestMessage(HttpMethod.Post, ttsEndpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, VoiceJsonContext.Default.VoiceHostTtsProxyRequest),
                    Encoding.UTF8,
                    "application/json"),
            };
            outbound.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

            try
            {
                using var response = await TtsHttp.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var statusCode = response.StatusCode == HttpStatusCode.ServiceUnavailable
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status502BadGateway;
                    return Results.Json(
                        new VoiceTtsErrorResponse(
                            "voice_host_tts_failed",
                            $"VoiceHost TTS failed with HTTP {(int)response.StatusCode}. {Trim(body, 512)}".Trim()),
                        VoiceJsonContext.Default.VoiceTtsErrorResponse,
                        statusCode: statusCode);
                }

                CopyHeader(response, context.Response, "X-Sample-Rate");
                CopyHeader(response, context.Response, "X-Channels");
                CopyHeader(response, context.Response, "X-Format");
                CopyHeader(response, context.Response, "X-Request-Id");
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "audio/wav";
                if (response.Content.Headers.ContentLength is { } contentLength)
                    context.Response.ContentLength = contentLength;
                await response.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
                return Results.Empty;
            }
            catch (TaskCanceledException)
            {
                return Results.Json(
                    new VoiceTtsErrorResponse("voice_host_tts_timeout", "Timed out waiting for VoiceHost TTS."),
                    VoiceJsonContext.Default.VoiceTtsErrorResponse,
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(
                    new VoiceTtsErrorResponse("voice_host_unreachable", $"Could not reach VoiceHost: {ex.Message}"),
                    VoiceJsonContext.Default.VoiceTtsErrorResponse,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/voice/asr", async (
            HttpRequest request,
            ISettingsStore store,
            VoiceHostProcessSupervisor voiceHost,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new VoiceAsrErrorResponse(
                    "multipart_required",
                    "Expected multipart/form-data with an 'audio' file."));
            }

            var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
            var audioFile = form.Files.GetFile("audio");
            if (audioFile is null || audioFile.Length == 0)
            {
                return Results.BadRequest(new VoiceAsrErrorResponse(
                    "audio_required",
                    "Audio is required."));
            }

            var doc = await store.GetAsync(ct).ConfigureAwait(false);
            if (!doc.Voice.VoiceHostEnabled)
            {
                return Results.Json(
                    new VoiceAsrErrorResponse("voice_host_disabled", "Local VoiceHost is disabled in Voice settings."),
                    VoiceJsonContext.Default.VoiceAsrErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (!TryBuildVoiceHostEndpoint(doc.Voice.VoiceHostBaseUrl, "/asr", out var asrEndpoint, out var endpointError))
            {
                return Results.BadRequest(new VoiceAsrErrorResponse("voice_host_url_invalid", endpointError));
            }

            var hostEnsure = await voiceHost.EnsureResponsiveAsync(asrEndpoint, doc.Voice, ct).ConfigureAwait(false);
            if (!hostEnsure.Success)
            {
                return Results.Json(
                    new VoiceAsrErrorResponse(hostEnsure.ErrorCode, hostEnsure.Message),
                    VoiceJsonContext.Default.VoiceAsrErrorResponse,
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var requestId = form.TryGetValue("requestId", out var requestValues) && !string.IsNullOrWhiteSpace(requestValues.ToString())
                ? requestValues.ToString().Trim()
                : "chat-asr-" + Guid.NewGuid().ToString("N")[..12];
            var sessionId = form.TryGetValue("sessionId", out var sessionValues) ? sessionValues.ToString().Trim() : "";

            await using var stream = audioFile.OpenReadStream();
            using var content = new MultipartFormDataContent();
            using var audioContent = new StreamContent(stream);
            audioContent.Headers.ContentType = ParseAudioContentType(audioFile.ContentType);
            content.Add(audioContent, "audio", string.IsNullOrWhiteSpace(audioFile.FileName) ? "speech.webm" : audioFile.FileName);
            content.Add(new StringContent(requestId), "requestId");
            if (!string.IsNullOrWhiteSpace(sessionId))
                content.Add(new StringContent(sessionId), "sessionId");

            using var outbound = new HttpRequestMessage(HttpMethod.Post, asrEndpoint)
            {
                Content = content,
            };
            outbound.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

            try
            {
                using var response = await AsrHttp.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return Results.Json(
                        new VoiceAsrErrorResponse(
                            "voice_host_asr_failed",
                            $"VoiceHost ASR failed with HTTP {(int)response.StatusCode}. {Trim(body, 512)}".Trim()),
                        VoiceJsonContext.Default.VoiceAsrErrorResponse,
                        statusCode: StatusCodes.Status502BadGateway);
                }

                var text = ExtractTranscript(body);
                return Results.Json(
                    new VoiceAsrResponse(text, requestId),
                    VoiceJsonContext.Default.VoiceAsrResponse);
            }
            catch (TaskCanceledException)
            {
                return Results.Json(
                    new VoiceAsrErrorResponse("voice_host_asr_timeout", "Timed out waiting for VoiceHost ASR."),
                    VoiceJsonContext.Default.VoiceAsrErrorResponse,
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(
                    new VoiceAsrErrorResponse("voice_host_unreachable", $"Could not reach VoiceHost: {ex.Message}"),
                    VoiceJsonContext.Default.VoiceAsrErrorResponse,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/voice/ptt/{phase}", (string phase, VoicePttEventHub hub) =>
        {
            var normalized = phase.Trim().ToLowerInvariant();
            if (normalized is not ("down" or "up" or "shutup"))
                return Results.BadRequest(new VoicePttErrorResponse("invalid_phase", "PTT phase must be down, up, or shutup."));

            hub.Publish(normalized, "shell");
            return Results.Json(new VoicePttPostResponse(true, normalized), VoiceJsonContext.Default.VoicePttPostResponse);
        });

        app.MapGet("/api/voice/ptt/events", async (HttpContext context, VoicePttEventHub hub, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.ContentType = "text/event-stream";

            await using var subscription = hub.Subscribe();
            await context.Response.WriteAsync(": connected\n\n", ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

            await foreach (var evt in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var payload = JsonSerializer.Serialize(evt, VoiceJsonContext.Default.VoicePttEvent);
                await context.Response.WriteAsync("event: ptt\n", ct).ConfigureAwait(false);
                await context.Response.WriteAsync("data: ", ct).ConfigureAwait(false);
                await context.Response.WriteAsync(payload, ct).ConfigureAwait(false);
                await context.Response.WriteAsync("\n\n", ct).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        });

        return app;
    }

    private static string ExtractTranscript(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            foreach (var propertyName in new[] { "text", "transcript" })
            {
                if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString()?.Trim() ?? "";
            }
        }
        catch (JsonException)
        {
            return body.Trim();
        }

        return "";
    }

    private static System.Net.Http.Headers.MediaTypeHeaderValue ParseAudioContentType(string? contentType)
    {
        var raw = string.IsNullOrWhiteSpace(contentType) ? "audio/webm" : contentType.Trim();
        if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(raw, out var parsed) && parsed is not null)
            return parsed;

        var mediaTypeOnly = raw.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(mediaTypeOnly, out parsed) && parsed is not null)
            return parsed;

        return new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
    }

    private static bool TryBuildVoiceHostEndpoint(string? baseUrl, string path, out Uri endpoint, out string error)
    {
        endpoint = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            error = "No VoiceHost URL configured.";
            return false;
        }

        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + path, UriKind.Absolute, out var parsed) || parsed is null)
        {
            error = "VoiceHost URL must be an absolute URL.";
            return false;
        }

        endpoint = parsed;

        if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
        {
            error = "VoiceHost URL must use http or https.";
            return false;
        }

        if (!IsLoopbackHost(endpoint.Host))
        {
            error = "VoiceHost URL must point to localhost or a loopback address.";
            return false;
        }

        return true;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool UsesDisabledTtsProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return false;
        var normalized = provider.Trim().ToLowerInvariant();
        return normalized is "stub" or "disabled" or "none";
    }

    private static void CopyHeader(HttpResponseMessage source, HttpResponse target, string name)
    {
        if (source.Headers.TryGetValues(name, out var values))
            target.Headers[name] = values.ToArray();
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}

public sealed record PiperVoicesResponse(IReadOnlyList<PiperVoiceEntry> Voices);

public sealed record VoiceHostHealthResponse(
    bool Ok,
    string Message,
    string? Body,
    int ElapsedMs,
    bool VoiceHostEnabled,
    bool HostReachable,
    bool AsrReady,
    bool TtsReady,
    bool InputAvailable,
    bool OutputAvailable,
    string Status,
    string? ErrorCode)
{
    public static VoiceHostHealthResponse FromStatus(VoiceRuntimeStatus status) => new(
        Ok: status.InputAvailable,
        Message: status.Message,
        Body: status.Body,
        ElapsedMs: status.ElapsedMs,
        VoiceHostEnabled: status.VoiceHostEnabled,
        HostReachable: status.HostReachable,
        AsrReady: status.AsrReady,
        TtsReady: status.TtsReady,
        InputAvailable: status.InputAvailable,
        OutputAvailable: status.OutputAvailable,
        Status: status.Status,
        ErrorCode: status.ErrorCode);
}

public sealed record VoiceTtsRequest(string Text, string? RequestId = null);

public sealed record VoiceAsrResponse(string Text, string RequestId);

public sealed record VoiceAsrErrorResponse(string Error, string Message);

public sealed record VoicePttPostResponse(bool Ok, string Phase);

public sealed record VoicePttErrorResponse(string Error, string Message);

public sealed record VoiceHostTtsProxyRequest(
    string Text,
    string RequestId,
    string Engine,
    string ModelId,
    string VoiceId,
    string Voice,
    string Format,
    int SampleRate);

public sealed record VoiceTtsErrorResponse(string Error, string Message);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PiperVoicesResponse))]
[JsonSerializable(typeof(PiperVoiceEntry))]
[JsonSerializable(typeof(VoiceHostHealthResponse))]
[JsonSerializable(typeof(VoiceRuntimeStatus))]
[JsonSerializable(typeof(VoiceTtsRequest))]
[JsonSerializable(typeof(VoiceHostTtsProxyRequest))]
[JsonSerializable(typeof(VoiceTtsErrorResponse))]
[JsonSerializable(typeof(VoiceAsrResponse))]
[JsonSerializable(typeof(VoiceAsrErrorResponse))]
[JsonSerializable(typeof(VoicePttEvent))]
[JsonSerializable(typeof(VoicePttPostResponse))]
[JsonSerializable(typeof(VoicePttErrorResponse))]
public partial class VoiceJsonContext : JsonSerializerContext
{
}
