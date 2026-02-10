using System.Text.Json;

namespace SirThaddeus.McpShared;

// ─────────────────────────────────────────────────────────────────────────
// Time Helper — Pure, Cross-Platform Time Formatting
//
// Produces a bounded, deterministic JSON payload for the time_now tool.
// Extracted here so tests can verify formatting without referencing
// the Windows-only MCP server project.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Pure helper for formatting time payloads.
/// Stateless, no side effects, fully testable.
/// </summary>
public static class TimeHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Builds the time_now response payload from the given instant.
    /// </summary>
    /// <param name="now">The current local time as a DateTimeOffset.</param>
    /// <param name="windowsTimezoneId">
    /// The Windows timezone ID (e.g. "Eastern Standard Time").
    /// Typically <c>TimeZoneInfo.Local.Id</c>.
    /// </param>
    /// <returns>JSON string with iso, unix_ms, timezone, and offset.</returns>
    public static string BuildTimePayload(DateTimeOffset now, string windowsTimezoneId)
    {
        // Offset formatted as "+HH:mm" or "-HH:mm"
        var offset = now.Offset;
        var sign = offset >= TimeSpan.Zero ? "+" : "-";
        var absOffset = offset < TimeSpan.Zero ? offset.Negate() : offset;
        var offsetStr = $"{sign}{absOffset.Hours:D2}:{absOffset.Minutes:D2}";

        return JsonSerializer.Serialize(new
        {
            iso        = now.ToString("o"),               // ISO 8601 with offset
            unix_ms    = now.ToUnixTimeMilliseconds(),
            timezone   = windowsTimezoneId,
            offset     = offsetStr
        }, JsonOpts);
    }
}
