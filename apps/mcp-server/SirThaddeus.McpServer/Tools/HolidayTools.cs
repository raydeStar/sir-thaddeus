using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Holiday Tools (Nager.Date)
//
// Keyless public holidays APIs:
//   - holidays_get
//   - holidays_next
//   - holidays_is_today
//
// Notes:
//   - countryCode must be ISO-3166 alpha-2 (e.g. US, CA, JP)
//   - optional regionCode supports county/state filtering (e.g. US-ID)
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class HolidayTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    [McpServerTool, Description(
        "Returns public holidays for a country/year from Nager.Date (no API key). " +
        "Optional region code filters to county/state-specific holidays.")]
    public static async Task<string> HolidaysGet(
        [Description("2-letter country code, e.g. US, CA, JP")] string countryCode,
        [Description("Year to query (1900..2100). Defaults to current year.")] int year = 0,
        [Description("Optional region code, e.g. US-ID or CA-ON")] string? regionCode = null,
        [Description("Max holidays to return (1-100, default 25)")] int maxItems = 25,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            return Json(new { error = "countryCode is required." });

        if (year == 0)
            year = DateTime.UtcNow.Year;
        year = Math.Clamp(year, 1900, 2100);
        maxItems = Math.Clamp(maxItems, 1, 100);

        try
        {
            var result = await PublicApiToolContext.HolidaysProvider.Value.GetHolidaysAsync(
                countryCode,
                year,
                regionCode,
                maxItems,
                cancellationToken);

            return Json(new
            {
                countryCode = result.CountryCode,
                regionCode = result.RegionCode,
                year = result.Year,
                source = result.Source,
                cache = new
                {
                    hit = result.Cache.Hit,
                    ageSeconds = result.Cache.AgeSeconds
                },
                holidays = result.Holidays.Select(h => new
                {
                    date = h.Date.ToString("yyyy-MM-dd"),
                    localName = h.LocalName,
                    name = h.Name,
                    countryCode = h.CountryCode,
                    global = h.Global,
                    launchYear = h.LaunchYear,
                    counties = h.Counties,
                    types = h.Types
                }).ToArray()
            });
        }
        catch (OperationCanceledException)
        {
            return Json(new
            {
                error = "Holiday lookup cancelled.",
                countryCode,
                year,
                regionCode
            });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = $"Holiday lookup failed: {ex.Message}",
                countryCode,
                year,
                regionCode
            });
        }
    }

    [McpServerTool, Description(
        "Returns upcoming public holidays for a country from Nager.Date (no API key). " +
        "Optional region code filters to county/state-specific holidays.")]
    public static async Task<string> HolidaysNext(
        [Description("2-letter country code, e.g. US, CA, JP")] string countryCode,
        [Description("Optional region code, e.g. US-ID or CA-ON")] string? regionCode = null,
        [Description("Max holidays to return (1-25, default 5)")] int maxItems = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            return Json(new { error = "countryCode is required." });

        maxItems = Math.Clamp(maxItems, 1, 25);

        try
        {
            var result = await PublicApiToolContext.HolidaysProvider.Value.GetNextPublicHolidaysAsync(
                countryCode,
                regionCode,
                maxItems,
                cancellationToken);

            return Json(new
            {
                countryCode = result.CountryCode,
                regionCode = result.RegionCode,
                source = result.Source,
                cache = new
                {
                    hit = result.Cache.Hit,
                    ageSeconds = result.Cache.AgeSeconds
                },
                holidays = result.Holidays.Select(h => new
                {
                    date = h.Date.ToString("yyyy-MM-dd"),
                    localName = h.LocalName,
                    name = h.Name,
                    countryCode = h.CountryCode,
                    global = h.Global,
                    launchYear = h.LaunchYear,
                    counties = h.Counties,
                    types = h.Types
                }).ToArray()
            });
        }
        catch (OperationCanceledException)
        {
            return Json(new
            {
                error = "Next-holiday lookup cancelled.",
                countryCode,
                regionCode
            });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = $"Next-holiday lookup failed: {ex.Message}",
                countryCode,
                regionCode
            });
        }
    }

    [McpServerTool, Description(
        "Checks if today is a public holiday in a country via Nager.Date (no API key). " +
        "Optionally filters by region code and includes the next holiday.")]
    public static async Task<string> HolidaysIsToday(
        [Description("2-letter country code, e.g. US, CA, JP")] string countryCode,
        [Description("Optional region code, e.g. US-ID or CA-ON")] string? regionCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            return Json(new { error = "countryCode is required." });

        try
        {
            var result = await PublicApiToolContext.HolidaysProvider.Value.IsTodayPublicHolidayAsync(
                countryCode,
                regionCode,
                cancellationToken);

            return Json(new
            {
                countryCode = result.CountryCode,
                regionCode = result.RegionCode,
                date = result.Date.ToString("yyyy-MM-dd"),
                isPublicHoliday = result.IsPublicHoliday,
                source = result.Source,
                cache = new
                {
                    hit = result.Cache.Hit,
                    ageSeconds = result.Cache.AgeSeconds
                },
                holidaysToday = result.HolidaysToday.Select(h => new
                {
                    date = h.Date.ToString("yyyy-MM-dd"),
                    localName = h.LocalName,
                    name = h.Name,
                    countryCode = h.CountryCode,
                    global = h.Global,
                    launchYear = h.LaunchYear,
                    counties = h.Counties,
                    types = h.Types
                }).ToArray(),
                nextHoliday = result.NextHoliday is null
                    ? null
                    : new
                    {
                        date = result.NextHoliday.Date.ToString("yyyy-MM-dd"),
                        localName = result.NextHoliday.LocalName,
                        name = result.NextHoliday.Name,
                        countryCode = result.NextHoliday.CountryCode,
                        global = result.NextHoliday.Global,
                        launchYear = result.NextHoliday.LaunchYear,
                        counties = result.NextHoliday.Counties,
                        types = result.NextHoliday.Types
                    }
            });
        }
        catch (OperationCanceledException)
        {
            return Json(new
            {
                error = "Is-today-holiday lookup cancelled.",
                countryCode,
                regionCode
            });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = $"Is-today-holiday lookup failed: {ex.Message}",
                countryCode,
                regionCode
            });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);
}
