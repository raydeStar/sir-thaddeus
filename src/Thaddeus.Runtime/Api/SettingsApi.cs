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

        return app;
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

/// <summary>Source-generated JSON context for the settings payload.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SettingsDocument))]
public partial class SettingsJsonContext : JsonSerializerContext
{
}
