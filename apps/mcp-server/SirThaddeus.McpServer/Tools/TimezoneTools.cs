using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Timezone Tools
//
// Resolves timezone from coordinates (lat/lon) with provider fallback:
//   - NWS points timeZone for US
//   - Open-Meteo timezone=auto globally
//
// Bounded and deterministic:
//   - Coordinate validation
//   - Cache metadata returned
//   - Read-only, safe to retry
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class TimezoneTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    [McpServerTool, Description(
        "Resolves timezone for coordinates. " +
        "Uses NWS for US coordinates and Open-Meteo fallback globally. " +
        "Returns IANA timezone ID with source and cache metadata.")]
    public static async Task<string> ResolveTimezone(
        [Description("Latitude in decimal degrees")] double latitude,
        [Description("Longitude in decimal degrees")] double longitude,
        [Description("Optional 2-letter country code hint, e.g. 'US'")] string? countryCode = null,
        CancellationToken cancellationToken = default)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return Json(new
            {
                error = "Coordinates out of range.",
                latitude,
                longitude
            });
        }

        try
        {
            var resolved = await PublicApiToolContext.TimezoneProvider.Value.ResolveAsync(
                latitude,
                longitude,
                countryCode,
                cancellationToken);

            return Json(new
            {
                latitude = resolved.Latitude,
                longitude = resolved.Longitude,
                timezone = resolved.Timezone,
                source = resolved.Source,
                cache = new
                {
                    hit = resolved.Cache.Hit,
                    ageSeconds = resolved.Cache.AgeSeconds
                }
            });
        }
        catch (OperationCanceledException)
        {
            return Json(new
            {
                error = "Timezone lookup cancelled.",
                latitude,
                longitude
            });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = $"Timezone lookup failed: {ex.Message}",
                latitude,
                longitude
            });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);
}
