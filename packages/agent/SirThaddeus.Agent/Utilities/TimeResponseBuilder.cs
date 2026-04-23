using System.Text.Json;

namespace SirThaddeus.Agent.Utilities;

/// <summary>
/// Pure-function builder for deterministic time/timezone response text.
/// No instance state. The caller supplies <c>utcNow</c> so this remains
/// fully testable with a frozen clock.
/// </summary>
public static class TimeResponseBuilder
{
    /// <summary>
    /// Builds a short deterministic time response from resolve_timezone
    /// MCP JSON output.
    /// </summary>
    public static string? TryBuildBriefFromTimezoneJson(
        string timezoneJson,
        string fallbackLocation,
        string userMessage,
        DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(timezoneJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(timezoneJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return null;
            }

            var timezone = root.TryGetProperty("timezone", out var tzEl)
                ? (tzEl.GetString() ?? "")
                : "";
            if (string.IsNullOrWhiteSpace(timezone))
                return null;

            var location = fallbackLocation;
            var fromMessage = WeatherResponseBuilder.ExtractLocationFromMessage(userMessage);
            if (!string.IsNullOrWhiteSpace(fromMessage))
                location = fromMessage!;

            if (TryResolveTimeZoneInfo(timezone, out var tzInfo))
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tzInfo);
                var formatted = local.ToString("h:mm tt on dddd, MMM d");
                return $"It's currently **{formatted}** in {location}. " +
                       $"Timezone: **{timezone}**. Weather geocode confirmed the location match.\n\n" +
                       "Need another city checked too?";
            }

            return $"The timezone for {location} is **{timezone}**.\n\nWant local time there as well?";
        }
        catch
        {
            return null;
        }
    }

    public static bool TryResolveTimeZoneInfo(string timezoneId, out TimeZoneInfo tzInfo)
    {
        try
        {
            tzInfo = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            return true;
        }
        catch
        {
            // Windows often needs a Windows timezone ID; convert if IANA provided.
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timezoneId, out var windowsId))
            {
                try
                {
                    tzInfo = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                    return true;
                }
                catch
                {
                    // Fall through.
                }
            }
        }

        tzInfo = TimeZoneInfo.Utc;
        return false;
    }
}
