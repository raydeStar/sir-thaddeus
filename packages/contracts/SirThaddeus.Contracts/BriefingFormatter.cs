using System.Globalization;

namespace SirThaddeus.Contracts;

/// <summary>
/// Pure business-logic formatter for briefing data.
/// Extracted from Avalonia UI layer to enable testing and reuse in headless runtime.
/// </summary>
public static class BriefingFormatter
{
    /// <summary>
    /// Maps confidence level to a user-facing label.
    /// </summary>
    public static string FormatConfidenceLabel(string? confidence) => confidence?.ToLowerInvariant() switch
    {
        "high" => "Verified",
        "medium" => "Partial",
        "low" => "Unverified",
        _ => "Unknown"
    };

    /// <summary>
    /// Builds a status message combining title and confidence-aware suffix.
    /// </summary>
    public static string BuildBriefingStatusMessage(DeepDiveBriefingDto briefing)
    {
        var title = briefing.Hero.Title;
        if (string.IsNullOrWhiteSpace(title))
            title = briefing.Topic.Query;

        var suffix = briefing.Hero.Confidence?.ToLowerInvariant() switch
        {
            "high" => "ready.",
            "medium" => "ready. Double-check important details.",
            "low" => "loaded. Verify key details before acting.",
            _ => "loaded."
        };

        return $"{title} — {suffix}";
    }

    /// <summary>
    /// Parses an ISO-8601 timestamp and formats it as a local date/time string.
    /// Returns "Unknown" if input is null or unparseable.
    /// </summary>
    public static string FormatIsoTimestamp(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
            return "Unknown";

        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            return dto.LocalDateTime.ToString("g", CultureInfo.InvariantCulture);
        }

        return iso;
    }

    /// <summary>
    /// Returns trimmed value, or "-" if null/whitespace.
    /// </summary>
    public static string ValueOrFallback(string? value, string fallback = "-")
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>
    /// Collects and deduplicates briefing sources from cards, audit steps, and hero website.
    /// Deduplicationion is by URL (case-insensitive). Returns first source per unique URL.
    /// </summary>
    public static IReadOnlyList<BriefingSourceRefDto> CollectBriefingSources(DeepDiveBriefingDto briefing)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BriefingSourceRefDto>();

        void Add(BriefingSourceRefDto src)
        {
            if (!string.IsNullOrWhiteSpace(src.Url) && seen.Add(src.Url))
                result.Add(src);
        }

        foreach (var card in briefing.Cards)
            foreach (var src in card.Sources)
                Add(src);

        foreach (var audit in briefing.Audit)
            foreach (var src in audit.Sources)
                Add(src);

        if (!string.IsNullOrWhiteSpace(briefing.Hero.Website))
        {
            Add(new BriefingSourceRefDto("Website", briefing.Hero.Website, briefing.Hero.LastCheckedIso));
        }

        return result;
    }

    /// <summary>
    /// Builds a short summary like "Sources: Name1, Name2, Name3" from a source list.
    /// Shows up to 4 distinct source names. Returns empty string if no sources.
    /// </summary>
    public static string BuildSourceSummary(IReadOnlyList<BriefingSourceRefDto> sources)
    {
        var names = sources
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        return names.Count == 0 ? string.Empty : $"Sources: {string.Join(", ", names)}";
    }

    /// <summary>
    /// Builds metadata for a source reference: "{hostname} | checked {timestamp}".
    /// </summary>
    public static string BuildSourceMeta(BriefingSourceRefDto source)
    {
        string host;
        try
        {
            host = new Uri(source.Url).Host;
        }
        catch
        {
            host = source.Url;
        }

        var ts = FormatIsoTimestamp(source.FetchedIso);
        return $"{host} | checked {ts}";
    }

    /// <summary>
    /// Checks whether two briefings represent the same logical briefing.
    /// Uses topic query + hero title as identity.
    /// </summary>
    public static bool SameBriefing(DeepDiveBriefingDto a, DeepDiveBriefingDto b)
    {
        return string.Equals(a.Topic.Query, b.Topic.Query, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Hero.Title, b.Hero.Title, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Manages briefing history for a single chat session.
/// Tracks up to MaxHistory briefing snapshots with deduplication.
/// </summary>
public sealed class BriefingSessionStore
{
    public const int MaxHistory = 24;

    private readonly List<BriefingHistoryEntry> _history = new();

    public IReadOnlyList<BriefingHistoryEntry> History => _history;

    public DeepDiveBriefingDto? ActiveBriefing { get; private set; }

    /// <summary>
    /// Records a briefing into session history. If the same briefing already exists
    /// at position 0, replaces it; otherwise inserts at position 0.
    /// Caps history at MaxHistory entries.
    /// </summary>
    public void Record(DeepDiveBriefingDto briefing)
    {
        ActiveBriefing = briefing;

        var entry = new BriefingHistoryEntry(
            briefing.Hero.Title,
            BriefingFormatter.FormatConfidenceLabel(briefing.Hero.Confidence),
            BriefingFormatter.BuildBriefingStatusMessage(briefing),
            DateTimeOffset.UtcNow,
            briefing);

        if (_history.Count > 0 && BriefingFormatter.SameBriefing(_history[0].Briefing, briefing))
        {
            _history[0] = entry;
        }
        else
        {
            _history.Insert(0, entry);
        }

        while (_history.Count > MaxHistory)
            _history.RemoveAt(_history.Count - 1);
    }
}

/// <summary>
/// Immutable snapshot of a briefing at recording time.
/// </summary>
public sealed record BriefingHistoryEntry(
    string Title,
    string ConfidenceLabel,
    string StatusLine,
    DateTimeOffset RecordedAt,
    DeepDiveBriefingDto Briefing);
