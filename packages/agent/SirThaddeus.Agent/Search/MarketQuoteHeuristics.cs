namespace SirThaddeus.Agent.Search;

/// <summary>
/// Deterministic market-quote intent and freshness heuristics.
/// Keeps quote-sensitive behavior explicit and testable.
/// </summary>
internal static class MarketQuoteHeuristics
{
    private static readonly string[] MarketSubjectTokens =
    [
        "dow jones",
        "djia",
        "s&p",
        "s and p",
        "sp500",
        "s&p 500",
        "nasdaq",
        "russell 2000",
        "nyse",
        "stock market",
        "index"
    ];

    private static readonly string[] QuoteIntentTokens =
    [
        "quote",
        "price",
        "trading",
        "points",
        "percent",
        "%",
        "up",
        "down",
        "live",
        "right now",
        "currently",
        "current",
        "latest",
        "most recent",
        "most recently",
        "past few hours",
        "last few hours",
        "today",
        "at "
    ];

    private static readonly string[] WeekMarkers =
    [
        "this week",
        "past week",
        "last week",
        "weekly"
    ];

    private static readonly string[] MonthMarkers =
    [
        "this month",
        "past month",
        "last month",
        "monthly"
    ];

    public static bool IsMarketQuoteRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lower = message.Trim().ToLowerInvariant();
        var hasSubject = MarketSubjectTokens.Any(t => lower.Contains(t, StringComparison.Ordinal));
        if (!hasSubject)
            return false;

        var hasQuoteSignal = QuoteIntentTokens.Any(t => lower.Contains(t, StringComparison.Ordinal));
        if (hasQuoteSignal)
            return true;

        return lower.StartsWith("what's", StringComparison.Ordinal) ||
               lower.StartsWith("whats", StringComparison.Ordinal) ||
               lower.StartsWith("what is", StringComparison.Ordinal);
    }

    public static string PreferredRecency(string message)
    {
        var lower = (message ?? "").ToLowerInvariant();
        if (WeekMarkers.Any(t => lower.Contains(t, StringComparison.Ordinal)))
            return "week";
        if (MonthMarkers.Any(t => lower.Contains(t, StringComparison.Ordinal)))
            return "month";
        return "day";
    }

    /// <summary>
    /// Extracts the canonical instrument/index name from a message.
    /// Returns null when no known instrument is detected.
    /// Used to build clean search queries instead of echoing raw user text.
    /// </summary>
    public static string? ExtractCanonicalInstrument(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var lower = message.ToLowerInvariant();

        // Order matters: check more specific tokens first.
        if (lower.Contains("dow jones") || lower.Contains("djia"))
            return "Dow Jones";
        if (lower.Contains("s&p 500") || lower.Contains("sp500") || lower.Contains("s and p 500"))
            return "S&P 500";
        if (lower.Contains("s&p") || lower.Contains("s and p"))
            return "S&P 500";
        if (lower.Contains("russell 2000"))
            return "Russell 2000";
        if (lower.Contains("nasdaq"))
            return "Nasdaq";
        if (lower.Contains("nyse"))
            return "NYSE Composite";
        if (lower.Contains("stock market"))
            return "stock market";

        return null;
    }
}
