using System.Diagnostics;
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

        return app;
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}

public sealed record PiperVoicesResponse(IReadOnlyList<PiperVoiceEntry> Voices);

public sealed record VoiceHostHealthResponse(bool Ok, string Message, string? Body, int ElapsedMs);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PiperVoicesResponse))]
[JsonSerializable(typeof(PiperVoiceEntry))]
[JsonSerializable(typeof(VoiceHostHealthResponse))]
public partial class VoiceJsonContext : JsonSerializerContext
{
}
