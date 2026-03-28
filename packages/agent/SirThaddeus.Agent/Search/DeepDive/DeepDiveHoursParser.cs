using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Search.DeepDive;

/// <summary>
/// Deterministic extraction for common business-hours formats found
/// in web search results and scraped page content.
///
/// Handles: "Monday: 9 AM - 5 PM", "Mon 8:00am-8:00pm", "Mon-Fri: 9-5",
/// "Closed", "Open 24 hours", 24h time, tab/comma separators, and
/// HTML table patterns like "Monday&lt;/td&gt;&lt;td&gt;8:00 AM".
///
/// LLM extraction can be layered on top later when this parser fails.
/// </summary>
public static class DeepDiveHoursParser
{
    // ── Day matching ───────────────────────────────────────────────
    private static readonly string[] FullDays =
        ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    private static readonly string[] ShortDays =
        ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    // ── Core patterns ──────────────────────────────────────────────

    // Pattern 1: "Day: hours" or "Day - hours" or "Day  hours" (colon, dash, or whitespace separator)
    private static readonly Regex SingleDayRegex = new(
        @"(?<day>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|wed|thu|fri|sat|sun)\s*[:\-–—]?\s+(?<hours>.+?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    // Pattern 2: Day ranges — "Mon-Fri: 9am-5pm", "Monday - Friday 9:00 AM - 5:00 PM", "Monday through Saturday"
    private static readonly Regex DayRangeRegex = new(
        @"(?<start>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|wed|thu|fri|sat|sun)\s*(?:[-–—]|through|thru)\s*(?<end>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|wed|thu|fri|sat|sun)\s*[:\-–—]?\s+(?<hours>.+?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    // Pattern 2b: Natural-language hours — "from 10:30 am to 6:00 pm Monday through Saturday"
    private static readonly Regex NaturalLanguageHoursRegex = new(
        @"(?:from\s+)?(?<hours>\d{1,2}(?::\d{2})?\s*(?:am|pm)\s*(?:to|-|–|—)\s*\d{1,2}(?::\d{2})?\s*(?:am|pm))\s*,?\s*(?<start>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|wed|thu|fri|sat|sun)\s*(?:[-–—]|through|thru)\s*(?<end>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|wed|thu|fri|sat|sun)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern 3: HTML table cell artifacts — "Monday</td><td>8:00 AM - 5:00 PM"
    private static readonly Regex HtmlCellRegex = new(
        @"(?<day>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|wed|thu|fri|sat|sun)\s*</?\w+[^>]*>\s*<?\w+[^>]*>?\s*(?<hours>\d.+?)(?:<|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Time range: "9:00 AM - 5:00 PM", "9am-5pm", "08:00 – 20:00", "9 AM to 5 PM"
    private static readonly Regex TimeRangeRegex = new(
        @"(?<open>(?:\d{1,2}:\d{2}\s*(?:am|pm|AM|PM)?|\d{1,2}\s*(?:am|pm|AM|PM)))\s*(?:-|–|—|to)\s*(?<close>(?:\d{1,2}:\d{2}\s*(?:am|pm|AM|PM)?|\d{1,2}\s*(?:am|pm|AM|PM)))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Special status: "Closed", "Open 24 hours", "Open daily"
    private static readonly Regex ClosedRegex = new(
        @"\bclosed\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Open24Regex = new(
        @"\bopen\s+24\s*(?:hours?|hrs?)?\b|\b24\s*(?:hours?|hrs?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OpenNowRegex = new(
        @"\bopen\s+now\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OpensAtRegex = new(
        @"\bopens?\s*(?:at)?\s*(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm|AM|PM))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ClosesAtRegex = new(
        @"\bcloses?\s*(?:at)?\s*(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm|AM|PM))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static DeepDiveHoursParseResult Parse(IEnumerable<string> textChunks)
    {
        var byDay = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in textChunks)
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            // Strip common HTML noise to expose the text
            var cleaned = NormalizeTimePeriods(StripHtmlTags(chunk));
            var lines = cleaned
                .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0);

            // Try natural-language patterns on the full cleaned text
            // before line-by-line parsing ("from 10:30 am to 6:00 pm Monday through Saturday")
            TryParseNaturalLanguageHours(cleaned, byDay);

            foreach (var line in lines)
            {
                // Try day ranges first (more specific)
                if (TryParseDayRange(line, byDay))
                    continue;

                // Try single-day patterns
                if (TryParseSingleDay(line, byDay))
                    continue;

                // Try HTML cell artifacts on the raw chunk
                TryParseHtmlCell(line, byDay);
            }

            // Also try HTML cell pattern on the original (unstripped) chunk
            // in case stripping mangled the structure
            if (chunk.Contains("</td>", StringComparison.OrdinalIgnoreCase) ||
                chunk.Contains("</th>", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in HtmlCellRegex.Matches(chunk))
                    AddDayHours(byDay, m.Groups["day"].Value, m.Groups["hours"].Value);
            }
        }

        if (byDay.Count == 0)
        {
            var genericHours = TryExtractGenericHours(textChunks);
            if (!string.IsNullOrWhiteSpace(genericHours))
            {
                return new DeepDiveHoursParseResult
                {
                    Bullets = [genericHours],
                    HasConflict = false,
                    HasAnyHours = true
                };
            }

            return new DeepDiveHoursParseResult
            {
                Bullets = [],
                HasConflict = false,
                HasAnyHours = false
            };
        }

        var conflict = byDay.Any(kvp => kvp.Value.Count > 1);
        var orderedDays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        var bullets = new List<string>();

        foreach (var day in orderedDays)
        {
            if (!byDay.TryGetValue(day, out var values) || values.Count == 0)
                continue;

            var chosen = values.OrderBy(x => x.Length).First();
            bullets.Add($"{day}: {chosen}");
        }

        return new DeepDiveHoursParseResult
        {
            Bullets = bullets,
            HasConflict = conflict,
            HasAnyHours = bullets.Count > 0
        };
    }

    /// <summary>
    /// Fallback for snippets that expose hours without day labels.
    /// Examples:
    /// - "Open now · Closes 10 PM"
    /// - "Store hours: 6 AM to 11 PM"
    /// </summary>
    private static string? TryExtractGenericHours(IEnumerable<string> textChunks)
    {
        foreach (var chunk in textChunks)
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            var text = NormalizeTimePeriods(StripHtmlTags(chunk));
            text = Regex.Replace(text, @"\s+", " ");

            var closesMatch = ClosesAtRegex.Match(text);
            if (closesMatch.Success)
            {
                var closeTime = NormalizeTime(closesMatch.Groups["time"].Value);
                return OpenNowRegex.IsMatch(text)
                    ? $"Open now, closes {closeTime}"
                    : $"Closes {closeTime}";
            }

            var opensMatch = OpensAtRegex.Match(text);
            if (opensMatch.Success)
            {
                var openTime = NormalizeTime(opensMatch.Groups["time"].Value);
                return $"Opens {openTime}";
            }

            var rangeMatch = TimeRangeRegex.Match(text);
            if (rangeMatch.Success)
            {
                var open = NormalizeTime(rangeMatch.Groups["open"].Value);
                var close = NormalizeTime(rangeMatch.Groups["close"].Value);
                return $"{open} - {close}";
            }

            if (ClosedRegex.IsMatch(text))
                return "Closed";
        }

        return null;
    }

    // ── Parse strategies ───────────────────────────────────────────

    private static bool TryParseSingleDay(string line, Dictionary<string, HashSet<string>> byDay)
    {
        var match = SingleDayRegex.Match(line);
        if (!match.Success)
            return false;

        return AddDayHours(byDay, match.Groups["day"].Value, match.Groups["hours"].Value);
    }

    private static bool TryParseDayRange(string line, Dictionary<string, HashSet<string>> byDay)
    {
        var match = DayRangeRegex.Match(line);
        if (!match.Success)
            return false;

        var startDay = NormalizeDay(match.Groups["start"].Value);
        var endDay = NormalizeDay(match.Groups["end"].Value);
        var hoursText = match.Groups["hours"].Value;

        if (string.IsNullOrWhiteSpace(startDay) || string.IsNullOrWhiteSpace(endDay))
            return false;

        var canonical = CanonicalizeHours(hoursText);
        if (string.IsNullOrWhiteSpace(canonical))
            return false;

        // Expand range into individual days
        var allDays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        var startIdx = Array.IndexOf(allDays, startDay);
        var endIdx = Array.IndexOf(allDays, endDay);

        if (startIdx < 0 || endIdx < 0)
            return false;

        // Handle wrap-around (e.g., Fri-Mon)
        var added = false;
        for (var i = startIdx; ; i = (i + 1) % 7)
        {
            AddToDay(byDay, allDays[i], canonical);
            added = true;
            if (i == endIdx) break;
        }

        return added;
    }

    private static bool TryParseHtmlCell(string line, Dictionary<string, HashSet<string>> byDay)
    {
        var match = HtmlCellRegex.Match(line);
        if (!match.Success)
            return false;

        return AddDayHours(byDay, match.Groups["day"].Value, match.Groups["hours"].Value);
    }

    private static bool AddDayHours(
        Dictionary<string, HashSet<string>> byDay,
        string rawDay,
        string rawHours)
    {
        var normalizedDay = NormalizeDay(rawDay);
        if (string.IsNullOrWhiteSpace(normalizedDay))
            return false;

        var canonical = CanonicalizeHours(rawHours);
        if (string.IsNullOrWhiteSpace(canonical))
            return false;

        AddToDay(byDay, normalizedDay, canonical);
        return true;
    }

    private static void AddToDay(
        Dictionary<string, HashSet<string>> byDay,
        string day,
        string hours)
    {
        if (!byDay.TryGetValue(day, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            byDay[day] = set;
        }

        set.Add(hours);
    }

    // ── Canonicalization ───────────────────────────────────────────

    private static string CanonicalizeHours(string rawHours)
    {
        var text = (rawHours ?? "").Trim();

        // Strip trailing HTML noise
        var htmlIdx = text.IndexOf('<');
        if (htmlIdx > 0)
            text = text[..htmlIdx].Trim();

        // Check for special statuses
        if (ClosedRegex.IsMatch(text))
            return "Closed";

        if (Open24Regex.IsMatch(text))
            return "Open 24 hours";

        // Try extracting a time range
        var rangeMatch = TimeRangeRegex.Match(text);
        if (rangeMatch.Success)
        {
            var open = NormalizeTime(rangeMatch.Groups["open"].Value);
            var close = NormalizeTime(rangeMatch.Groups["close"].Value);
            return $"{open} - {close}";
        }

        // Fallback: clean up whatever text remains (if it looks time-ish)
        var simplified = SimplifyHoursText(text);
        if (simplified.Length > 2 && simplified.Length < 60 &&
            Regex.IsMatch(simplified, @"\d"))
        {
            return simplified;
        }

        return "";
    }

    internal static string NormalizeDay(string raw)
    {
        var lower = (raw ?? "").Trim().ToLowerInvariant();
        return lower switch
        {
            "mon" or "monday"    => "Monday",
            "tue" or "tuesday"   => "Tuesday",
            "wed" or "wednesday" => "Wednesday",
            "thu" or "thursday"  => "Thursday",
            "fri" or "friday"    => "Friday",
            "sat" or "saturday"  => "Saturday",
            "sun" or "sunday"    => "Sunday",
            _ => ""
        };
    }

    private static string NormalizeTime(string value)
    {
        var compact = (value ?? "").Trim();
        compact = Regex.Replace(compact, @"\s+", " ");

        // Already has AM/PM
        if (compact.EndsWith("am", StringComparison.OrdinalIgnoreCase) ||
            compact.EndsWith("pm", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = compact[^2..].ToUpper();
            var number = compact[..^2].Trim();
            return $"{number} {suffix}";
        }

        return compact;
    }

    private static string SimplifyHoursText(string value)
    {
        var compact = (value ?? "").Trim();
        compact = Regex.Replace(compact, @"\s+", " ");
        compact = compact.Replace("—", "-").Replace("–", "-");
        return compact;
    }

    /// <summary>
    /// Strips HTML tags but preserves whitespace/newlines at tag boundaries
    /// so "Monday 9 AM" cell pairs become "Monday  9 AM".
    /// </summary>
    private static string StripHtmlTags(string input)
    {
        if (!input.Contains('<'))
            return input;

        // Replace block-level tags with newlines, inline with spaces
        var result = Regex.Replace(input, @"<(?:br|/tr|/div|/p|/li|/dt|/dd)[^>]*>", "\n",
            RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"</?(?:td|th)[^>]*>", " ",
            RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"<[^>]+>", " ");
        result = Regex.Replace(result, @"[ \t]+", " ");
        return result;
    }

    /// <summary>
    /// Normalizes period-separated time markers to their compact forms:
    /// "a.m." → "am", "p.m." → "pm". Many real-world sites use the
    /// period-separated form (AP style).
    /// </summary>
    private static string NormalizeTimePeriods(string input)
    {
        var result = Regex.Replace(input, @"a\.m\.", "am", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"p\.m\.", "pm", RegexOptions.IgnoreCase);
        return result;
    }

    /// <summary>
    /// Matches natural-language hour statements like
    /// "from 10:30 am to 6:00 pm Monday through Saturday" or
    /// "open 10 am to 8 pm, 7 days a week".
    /// </summary>
    private static void TryParseNaturalLanguageHours(
        string text,
        Dictionary<string, HashSet<string>> byDay)
    {
        foreach (Match match in NaturalLanguageHoursRegex.Matches(text))
        {
            var hours = match.Groups["hours"].Value.Trim();
            var startDay = NormalizeDay(match.Groups["start"].Value.Trim());
            var endDay = NormalizeDay(match.Groups["end"].Value.Trim());

            if (string.IsNullOrWhiteSpace(startDay) || string.IsNullOrWhiteSpace(endDay))
                continue;

            var canonical = CanonicalizeHours(hours);
            if (string.IsNullOrWhiteSpace(canonical))
                continue;

            var allDays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            var startIdx = Array.IndexOf(allDays, startDay);
            var endIdx = Array.IndexOf(allDays, endDay);

            if (startIdx < 0 || endIdx < 0)
                continue;

            for (var i = startIdx; ; i = (i + 1) % 7)
            {
                AddToDay(byDay, allDays[i], canonical);
                if (i == endIdx) break;
            }
        }
    }
}

public sealed record DeepDiveHoursParseResult
{
    public IReadOnlyList<string> Bullets { get; init; } = [];
    public bool HasConflict { get; init; }
    public bool HasAnyHours { get; init; }
}
