using System.Globalization;
using System.Text.RegularExpressions;

namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Handles the daily journal pattern — the highest-frequency use case.
/// Auto-creates daily files, formats entries with timestamps.
/// </summary>
public sealed partial class JournalHandler
{
    private readonly IKnowledgeStoreTools _store;
    private readonly string _rootId;
    private readonly TimeProvider _timeProvider;

    public JournalHandler(IKnowledgeStoreTools store, string rootId, TimeProvider timeProvider)
    {
        _store = store;
        _rootId = rootId;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Log an entry to today's journal file.
    /// Creates the file if it doesn't exist.
    /// </summary>
    public async Task<KnowledgeToolResult> LogEntryAsync(
        string entry, string? timeHint = null)
    {
        var now = _timeProvider.GetUtcNow().DateTime;
        var dateStr = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dayName = now.ToString("dddd", CultureInfo.InvariantCulture);
        var relativePath = $"journal/{dateStr}.md";

        // Parse the time from the entry or hint, or use current time
        var time = ParseTimeHint(timeHint, now);
        var timeStr = time.ToString("h:mm tt", CultureInfo.InvariantCulture);

        // Try to append first (file may already exist)
        var formattedEntry = $"\n### {timeStr}\n- {entry.Trim()}\n";

        var appendResult = await _store.AppendToFileAsync(_rootId, relativePath, formattedEntry);

        if (appendResult.Success)
            return KnowledgeToolResult.Ok($"Logged to journal ({dateStr}).", filePath: relativePath);

        // File doesn't exist — create it with header
        var header = $"""
            # {dateStr} — {dayName}

            ### {timeStr}
            - {entry.Trim()}

            """;

        return await _store.CreateFileAsync(_rootId, relativePath, header);
    }

    /// <summary>
    /// Read journal entries for a date range.
    /// </summary>
    public async Task<List<JournalDay>> ReadRangeAsync(DateTime start, DateTime end)
    {
        var days = new List<JournalDay>();
        var current = start.Date;

        while (current <= end.Date)
        {
            var dateStr = current.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var relativePath = $"journal/{dateStr}.md";
            var result = await _store.ReadFileAsync(_rootId, relativePath);

            if (result.Success && result.Content is not null)
            {
                days.Add(new JournalDay
                {
                    Date = current,
                    Content = result.Content,
                    RelativePath = relativePath
                });
            }

            current = current.AddDays(1);
        }

        return days;
    }

    /// <summary>
    /// Parse a natural-language time hint into a DateTime.
    /// Supports: "5 PM", "at 5:30 PM", "this morning", "tonight", etc.
    /// </summary>
    public static DateTime ParseTimeHint(string? hint, DateTime fallback)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return fallback;

        var lower = hint.Trim().ToLowerInvariant();

        // Handle named time periods
        if (lower.Contains("this morning") || lower.Contains("morning"))
            return fallback.Date.AddHours(8);
        if (lower.Contains("noon") || lower.Contains("lunch"))
            return fallback.Date.AddHours(12);
        if (lower.Contains("this afternoon") || lower.Contains("afternoon"))
            return fallback.Date.AddHours(14);
        if (lower.Contains("this evening") || lower.Contains("evening"))
            return fallback.Date.AddHours(18);
        if (lower.Contains("tonight") || lower.Contains("night"))
            return fallback.Date.AddHours(21);

        // Strip "at " prefix
        if (lower.StartsWith("at ", StringComparison.Ordinal))
            lower = lower[3..].Trim();

        // Try standard time formats
        var match = TimePatternRegex().Match(lower);
        if (match.Success)
        {
            var hourStr = match.Groups[1].Value;
            var minuteStr = match.Groups[2].Success ? match.Groups[2].Value : "00";
            var meridiem = match.Groups[3].Value.ToLowerInvariant();

            if (int.TryParse(hourStr, out var hour) &&
                int.TryParse(minuteStr, out var minute))
            {
                if (meridiem == "pm" && hour < 12) hour += 12;
                if (meridiem == "am" && hour == 12) hour = 0;
                return fallback.Date.AddHours(hour).AddMinutes(minute);
            }
        }

        return fallback;
    }

    [GeneratedRegex(@"(\d{1,2})(?::(\d{2}))?\s*(am|pm)", RegexOptions.IgnoreCase)]
    private static partial Regex TimePatternRegex();
}

/// <summary>
/// A single day's journal entry.
/// </summary>
public sealed record JournalDay
{
    public DateTime Date { get; init; }
    public string Content { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
}
