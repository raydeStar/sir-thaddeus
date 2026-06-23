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
    private static readonly string[] ModelDiscoveryPaths = ["/api/v0/models", "/v1/models", "/models"];

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
                // LM Studio's JIT loader means /v1/models (OpenAI-compat) often
                // returns 0 when no model is currently in memory, even though
                // the user has a dozen downloaded. Try /api/v0/models first —
                // LM Studio's own REST API lists every downloaded model with
                // loaded/not-loaded state. Fall back to /v1/models and /models
                // for non-LM-Studio endpoints.
                var (models, probedUrl, reachErr) = await FetchModelIdsAsync(baseUrl, apiKey, ct)
                    .ConfigureAwait(false);

                if (reachErr is not null)
                {
                    return Results.Json(
                        new TestLlmResponse(false, reachErr, Array.Empty<string>()),
                        SettingsJsonContext.Default.TestLlmResponse);
                }

                var msg = models.Count == 0
                    ? $"Connected to {probedUrl}, but no models were returned. " +
                      "If this is LM Studio, download or load a model so it shows up here."
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

        // GET /api/settings/gatekeeper-status — reports whether the
        // gatekeeper model is reachable and whether it's really loaded
        // (not just listed) on the configured endpoint. Used by the
        // Settings UI to show an "active / unreachable / not configured"
        // indicator below the Verification-model block.
        app.MapGet("/api/settings/gatekeeper-status",
            async (ISettingsStore store, CancellationToken ct) =>
        {
            var current = await store.GetAsync(ct).ConfigureAwait(false);
            var llm = current.Llm;

            if (string.IsNullOrWhiteSpace(llm.GatekeeperModelId))
            {
                return Results.Json(
                    new GatekeeperStatusResponse(
                        Configured: false,
                        Ok: false,
                        ModelId: null,
                        BaseUrl: null,
                        ReusingPrimary: false,
                        Message: "Gatekeeper model is not configured."),
                    SettingsJsonContext.Default.GatekeeperStatusResponse);
            }

            var gkBaseUrl = string.IsNullOrWhiteSpace(llm.GatekeeperBaseUrl)
                ? llm.BaseUrl
                : llm.GatekeeperBaseUrl;
            var samePrimaryModel = string.Equals(llm.ModelId, llm.GatekeeperModelId,
                StringComparison.OrdinalIgnoreCase);
            var sameEndpoint = UriHostsMatchSafe(llm.BaseUrl ?? "", gkBaseUrl ?? "");
            var reusingPrimary = sameEndpoint && samePrimaryModel;

            if (string.IsNullOrWhiteSpace(gkBaseUrl))
            {
                return Results.Json(
                    new GatekeeperStatusResponse(
                        Configured: true,
                        Ok: false,
                        ModelId: llm.GatekeeperModelId,
                        BaseUrl: null,
                        ReusingPrimary: reusingPrimary,
                        Message: "No base URL — set one above or leave blank to share the primary's."),
                    SettingsJsonContext.Default.GatekeeperStatusResponse);
            }

            try
            {
                var (models, _, reachErr) = await FetchModelIdsAsync(gkBaseUrl, llm.ApiKey, ct)
                    .ConfigureAwait(false);
                if (reachErr is not null)
                {
                    return Results.Json(
                        new GatekeeperStatusResponse(
                            Configured: true,
                            Ok: false,
                            ModelId: llm.GatekeeperModelId,
                            BaseUrl: gkBaseUrl,
                            ReusingPrimary: reusingPrimary,
                            Message: reachErr),
                        SettingsJsonContext.Default.GatekeeperStatusResponse);
                }

                var modelFound = models.Any(m =>
                    string.Equals(m, llm.GatekeeperModelId, StringComparison.OrdinalIgnoreCase));

                var msg = modelFound
                    ? $"Active: {llm.GatekeeperModelId} is available on {gkBaseUrl}."
                    : $"Endpoint reachable, but '{llm.GatekeeperModelId}' isn't in the downloaded-models list.";

                return Results.Json(
                    new GatekeeperStatusResponse(
                        Configured: true,
                        Ok: modelFound,
                        ModelId: llm.GatekeeperModelId,
                        BaseUrl: gkBaseUrl,
                        ReusingPrimary: reusingPrimary,
                        Message: msg),
                    SettingsJsonContext.Default.GatekeeperStatusResponse);
            }
            catch (TaskCanceledException)
            {
                return Results.Json(
                    new GatekeeperStatusResponse(
                        Configured: true,
                        Ok: false,
                        ModelId: llm.GatekeeperModelId,
                        BaseUrl: gkBaseUrl,
                        ReusingPrimary: reusingPrimary,
                        Message: "Timed out reaching the gatekeeper endpoint."),
                    SettingsJsonContext.Default.GatekeeperStatusResponse);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(
                    new GatekeeperStatusResponse(
                        Configured: true,
                        Ok: false,
                        ModelId: llm.GatekeeperModelId,
                        BaseUrl: gkBaseUrl,
                        ReusingPrimary: reusingPrimary,
                        Message: $"Could not reach {gkBaseUrl}: {ex.Message}"),
                    SettingsJsonContext.Default.GatekeeperStatusResponse);
            }
        });

        return app;
    }

    /// <summary>
    /// Probes OpenAI-compatible <c>/v1/models</c> plus LM Studio's richer
    /// <c>/api/v0/models</c> (which includes downloaded-but-not-loaded
    /// entries) and returns the union. Returns (ids, which URL actually
    /// produced content, error). A non-null error means none of the probes
    /// reached the server; otherwise ids may be empty even on success.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Models, string ProbedUrl, string? Error)>
        FetchModelIdsAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        var candidateUrls = BuildModelDiscoveryProbeUrls(baseUrl);

        var reachedAny = false;
        var union = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastError = null;
        string? lastProbedUrl = null;
        string? successUrl = null;

        foreach (var probeUrl in candidateUrls)
        {
            lastProbedUrl = probeUrl;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, probeUrl);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                }
                using var res = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!res.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)res.StatusCode} from {probeUrl}";
                    continue;
                }

                reachedAny = true;
                successUrl ??= probeUrl;
                using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                foreach (var id in ParseModelIds(stream))
                {
                    if (seen.Add(id)) union.Add(id);
                }
            }
            catch (HttpRequestException ex)
            {
                lastError = $"Could not reach {probeUrl}: {ex.Message}";
            }
            catch (TaskCanceledException)
            {
                lastError = $"Timed out reaching {probeUrl}.";
            }
        }

        if (!reachedAny)
            return (Array.Empty<string>(), lastProbedUrl ?? baseUrl, lastError ?? "No response from server.");

        union.Sort(StringComparer.OrdinalIgnoreCase);
        return (union, successUrl ?? baseUrl, null);
    }

    internal static IReadOnlyList<string> BuildModelDiscoveryProbeUrls(string baseUrl)
    {
        var normalizedBase = NormalizeModelDiscoveryBaseUrl(baseUrl);
        return ModelDiscoveryPaths
            .Select(path => normalizedBase.TrimEnd('/') + path)
            .ToArray();
    }

    private static string NormalizeModelDiscoveryBaseUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3]
            : trimmed;
    }

    private static bool UriHostsMatchSafe(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a)) return false;
        if (!Uri.TryCreate(right, UriKind.Absolute, out var b)) return false;
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
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

/// <summary>Response shape for GET /api/settings/gatekeeper-status.</summary>
public sealed record GatekeeperStatusResponse(
    bool Configured,
    bool Ok,
    string? ModelId,
    string? BaseUrl,
    bool ReusingPrimary,
    string Message);

/// <summary>Source-generated JSON context for the settings payload.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SettingsDocument))]
[JsonSerializable(typeof(TestLlmRequest))]
[JsonSerializable(typeof(TestLlmResponse))]
[JsonSerializable(typeof(GatekeeperStatusResponse))]
public partial class SettingsJsonContext : JsonSerializerContext
{
}
