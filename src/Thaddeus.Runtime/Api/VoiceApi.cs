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
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly HttpClient TtsHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static IEndpointRouteBuilder MapVoiceApi(this IEndpointRouteBuilder app)
    {
        // GET /api/voice/piper-voices — curated + filesystem-discovered Piper voices.
        app.MapGet("/api/voice/piper-voices", () =>
        {
            var response = new PiperVoicesResponse(PiperVoiceCatalog.Discover());
            return Results.Json(response, VoiceJsonContext.Default.PiperVoicesResponse);
        });

        // GET /api/voice/host-health — probes the configured VoiceHost base URL.
        app.MapGet("/api/voice/host-health", async (ISettingsStore store, CancellationToken ct) =>
        {
            var doc = await store.GetAsync(ct).ConfigureAwait(false);
            var baseUrl = (doc.Voice.VoiceHostBaseUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Results.Json(
                    new VoiceHostHealthResponse(false, "No VoiceHost URL configured.", null, 0),
                    VoiceJsonContext.Default.VoiceHostHealthResponse);
            }

            var url = baseUrl.TrimEnd('/') + "/health";
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var res = await SharedHttp
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Results.Json(
                    new VoiceHostHealthResponse(
                        Ok: res.IsSuccessStatusCode,
                        Message: res.IsSuccessStatusCode
                            ? $"Reachable ({(int)res.StatusCode})"
                            : $"HTTP {(int)res.StatusCode} from {url}",
                        Body: Trim(body, 512),
                        ElapsedMs: (int)stopwatch.ElapsedMilliseconds),
                    VoiceJsonContext.Default.VoiceHostHealthResponse);
            }
            catch (TaskCanceledException)
            {
                stopwatch.Stop();
                return Results.Json(
                    new VoiceHostHealthResponse(false, "Timed out reaching VoiceHost.", null, (int)stopwatch.ElapsedMilliseconds),
                    VoiceJsonContext.Default.VoiceHostHealthResponse);
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                return Results.Json(
                    new VoiceHostHealthResponse(false, $"Could not reach {baseUrl}: {ex.Message}", null, (int)stopwatch.ElapsedMilliseconds),
                    VoiceJsonContext.Default.VoiceHostHealthResponse);
            }
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

                var audio = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (audio.Length == 0)
                {
                    return Results.Json(
                        new VoiceTtsErrorResponse("voice_host_empty_audio", "VoiceHost returned an empty audio response."),
                        VoiceJsonContext.Default.VoiceTtsErrorResponse,
                        statusCode: StatusCodes.Status502BadGateway);
                }

                CopyHeader(response, context.Response, "X-Sample-Rate");
                CopyHeader(response, context.Response, "X-Channels");
                CopyHeader(response, context.Response, "X-Format");
                CopyHeader(response, context.Response, "X-Request-Id");
                return Results.File(audio, response.Content.Headers.ContentType?.ToString() ?? "audio/wav");
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

        return app;
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

public sealed record VoiceHostHealthResponse(bool Ok, string Message, string? Body, int ElapsedMs);

public sealed record VoiceTtsRequest(string Text, string? RequestId = null);

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
[JsonSerializable(typeof(VoiceTtsRequest))]
[JsonSerializable(typeof(VoiceHostTtsProxyRequest))]
[JsonSerializable(typeof(VoiceTtsErrorResponse))]
public partial class VoiceJsonContext : JsonSerializerContext
{
}
