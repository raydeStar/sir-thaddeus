namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Recency Helper — Shared Recency Normalization and Cutoff
//
// Centralizes recency logic that was previously duplicated between
// WebSearchTools (string normalization) and GoogleNewsRssProvider
// (DateTime cutoff computation).
// ─────────────────────────────────────────────────────────────────────────

public static class RecencyHelper
{
    /// <summary>
    /// Normalizes a recency string to one of: "day", "week", "month", "any".
    /// Accepts common aliases and variations.
    /// </summary>
    public static string Normalize(string? recency)
    {
        var r = (recency ?? "any").Trim().ToLowerInvariant();
        return r switch
        {
            "day" or "today" or "24h" or "1d"  => "day",
            "week" or "7d" or "this week"      => "week",
            "month" or "30d" or "this month"   => "month",
            "any" or "all" or "none" or ""     => "any",
            _ => "any"
        };
    }

    /// <summary>
    /// Computes the UTC cutoff date for a recency window.
    /// Returns null for "any" (no cutoff).
    /// </summary>
    public static DateTime? ToCutoffUtc(string recency, DateTime nowUtc)
    {
        var r = Normalize(recency);
        return r switch
        {
            "day"   => nowUtc.AddDays(-1),
            "week"  => nowUtc.AddDays(-7),
            "month" => nowUtc.AddDays(-31),
            _       => null
        };
    }

    /// <summary>
    /// Returns a human-readable label for the recency window.
    /// </summary>
    public static string ToLabel(string recency) =>
        Normalize(recency) switch
        {
            "day"   => "past 24 hours",
            "week"  => "past 7 days",
            "month" => "past 31 days",
            _       => "any time"
        };
}
