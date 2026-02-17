namespace SirThaddeus.WebSearch;

/// <summary>
/// Deterministic helper methods for normalized places payloads.
/// This is deliberately free of HTTP/network calls.
/// </summary>
public static class PlacesNormalization
{
    public static IReadOnlyList<string> NormalizeWeekdayText(IEnumerable<string>? lines)
    {
        if (lines is null)
            return [];

        var normalized = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var compact = line.Trim().Replace('\u2013', '-').Replace('\u2014', '-');
            normalized.Add(compact);
        }

        return normalized;
    }

    public static string GetTodayHoursOrFallback(IReadOnlyList<string> weekdayText, DateTimeOffset nowLocal)
    {
        if (weekdayText.Count == 0)
            return "Hours not published";

        var day = nowLocal.DayOfWeek.ToString();
        var today = weekdayText.FirstOrDefault(
            entry => entry.StartsWith(day, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(today) ? weekdayText[0] : today;
    }
}
