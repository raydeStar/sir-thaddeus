using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Api;

/// <summary>Routes for reading and replacing the runtime settings document.</summary>
public static class SettingsApi
{
    private const string SecretMask = "***";
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    public static IEndpointRouteBuilder MapSettingsApi(this IEndpointRouteBuilder app)
    {
        // GET /api/settings — current settings, with secrets masked.
        app.MapGet("/api/settings", async (ISettingsStore store, CancellationToken ct) =>
        {
            var doc = await store.GetAsync(ct).ConfigureAwait(false);
            return Results.Json(MaskSecrets(doc), SettingsJsonContext.Default.SettingsDocument);
        });

        // PUT /api/settings — replace the entire document. Secrets sent as the
        // mask are interpreted as "leave unchanged"; any other value (including
        // empty string and null) replaces the stored secret.
        app.MapPut("/api/settings", async (HttpContext ctx, ISettingsStore store, CancellationToken ct) =>
        {
            SettingsDocument? incoming;
            try
            {
                incoming = await JsonSerializer
                    .DeserializeAsync(ctx.Request.Body, SettingsJsonContext.Default.SettingsDocument, ct)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "invalid_json" });
            }

            if (incoming is null) return Results.BadRequest(new { error = "empty_body" });

            var current = await store.GetAsync(ct).ConfigureAwait(false);
            var merged = MergeSecrets(current, incoming);
            var saved = await store.ReplaceAsync(merged, ct).ConfigureAwait(false);
            return Results.Json(MaskSecrets(saved), SettingsJsonContext.Default.SettingsDocument);
        });

        // POST /api/settings/test-llm — probes an OpenAI-compatible endpoint
        // (LM Studio, Ollama with the OpenAI shim, OpenAI itself, ...) for its
        // model list. The body is optional; if absent we use the saved settings.
        app.MapPost("/api/settings/test-llm", async (HttpContext ctx, ISettingsStore store, CancellationToken ct) =>
        {
            TestLlmRequest? incoming = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    incoming = await JsonSerializer
                        .DeserializeAsync(ctx.Request.Body, SettingsJsonContext.Default.TestLlmRequest, ct)
                        .ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "invalid_json" });
                }
            }

            var current = await store.GetAsync(ct).ConfigureAwait(false);
            var baseUrl = !string.IsNullOrWhiteSpace(incoming?.BaseUrl) ? incoming!.BaseUrl! : current.Llm.BaseUrl;
            var apiKey = incoming?.ApiKey == SecretMask || string.IsNullOrEmpty(incoming?.ApiKey)
                ? current.Llm.ApiKey
                : incoming!.ApiKey;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Results.Json(
                    new TestLlmResponse(false, "No base URL configured.", Array.Empty<string>()),
                    SettingsJsonContext.Default.TestLlmResponse);
            }

            try
            {
                var probeUrl = baseUrl.TrimEnd('/') + "/models";
                using var req = new HttpRequestMessage(HttpMethod.Get, probeUrl);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                }
                using var res = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!res.IsSuccessStatusCode)
                {
                    return Results.Json(
                        new TestLlmResponse(false, $"HTTP {(int)res.StatusCode} from {probeUrl}", Array.Empty<string>()),
                        SettingsJsonContext.Default.TestLlmResponse);
                }

                var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var models = ParseModelIds(stream);
                var msg = models.Count == 0
                    ? "Connected, but the server returned no models."
                    : $"Connected. Found {models.Count} model{(models.Count == 1 ? "" : "s")}.";
                return Results.Json(
                    new TestLlmResponse(true, msg, models),
                    SettingsJsonContext.Default.TestLlmResponse);
            }
            catch (TaskCanceledException)
            {
                return Results.Json(
                    new TestLlmResponse(false, "Timed out reaching the server.", Array.Empty<string>()),
                    SettingsJsonContext.Default.TestLlmResponse);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(
                    new TestLlmResponse(false, $"Could not reach {baseUrl}: {ex.Message}", Array.Empty<string>()),
                    SettingsJsonContext.Default.TestLlmResponse);
            }
        });

        return app;
    }

    private static IReadOnlyList<string> ParseModelIds(Stream json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String)
                {
                    var s = id.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) ids.Add(s);
                }
            }
            return ids;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static SettingsDocument MaskSecrets(SettingsDocument doc) => doc with
    {
        Llm = doc.Llm with { ApiKey = string.IsNullOrEmpty(doc.Llm.ApiKey) ? null : SecretMask },
    };

    private static SettingsDocument MergeSecrets(SettingsDocument current, SettingsDocument incoming) => incoming with
    {
        Llm = incoming.Llm with
        {
            ApiKey = incoming.Llm.ApiKey == SecretMask ? current.Llm.ApiKey : incoming.Llm.ApiKey,
        },
    };
}

/// <summary>Optional body for POST /api/settings/test-llm.</summary>
public sealed record TestLlmRequest(string? BaseUrl, string? ApiKey);

/// <summary>Response shape for POST /api/settings/test-llm.</summary>
public sealed record TestLlmResponse(bool Ok, string Message, IReadOnlyList<string> Models);

/// <summary>Source-generated JSON context for the settings payload.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SettingsDocument))]
[JsonSerializable(typeof(TestLlmRequest))]
[JsonSerializable(typeof(TestLlmResponse))]
public partial class SettingsJsonContext : JsonSerializerContext
{
}
