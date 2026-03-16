using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;

internal static partial class RuntimeApiServer
{
    private static void MapCoreEndpoints(
        WebApplication app,
        Func<CancellationToken, Task<SearchStatusResponse>> getSearchStatus,
        Func<AppSettings> getSettings,
        Action<AppSettings> persistSettings,
        IAuditLogger audit)
    {
        app.MapGet("/api/health", () =>
        {
            return new HealthResponse(
                Status: "ok",
                Version: typeof(RuntimeApiServer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Runtime: "headless-runtime",
                UtcNow: DateTimeOffset.UtcNow);
        });

        app.MapGet("/api/search/status", async (CancellationToken ct) =>
        {
            var snapshot = await getSearchStatus(ct);
            return Results.Json(snapshot, JsonOptions);
        });

        app.MapPut("/api/settings", (AppSettings request) =>
        {
            persistSettings(request);
            return Results.Json(getSettings(), JsonOptions);
        });

        app.MapGet("/api/audit", async (int? take, CancellationToken ct) =>
        {
            if (audit is not JsonLineAuditLogger logger)
            {
                return Results.Json(Array.Empty<AuditEntryDto>(), JsonOptions);
            }

            var max = Math.Clamp(take ?? 200, 1, 1000);
            var events = await logger.ReadTailAsync(max, ct);
            var dtos = events.Select((evt, index) => new AuditEntryDto(
                Id: $"{evt.Timestamp.ToUnixTimeMilliseconds()}-{index}",
                Category: evt.Action,
                Message: BuildAuditMessage(evt),
                TimestampUtc: evt.Timestamp,
                CorrelationId: evt.PermissionTokenId,
                MetadataJson: evt.Details is null ? null : JsonSerializer.Serialize(evt.Details, JsonOptions)))
                .ToArray();

            return Results.Json(dtos, JsonOptions);
        });
    }
}
