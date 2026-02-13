using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Dialogue;

/// <summary>
/// Guards location context against non-geographic tokens that can be
/// misclassified by LLM slot extraction.
///
/// Two layers of defence:
///   1. Known non-place tokens (market indices, abstract concepts).
///   2. Structural heuristics — a geographic name is short, has no
///      question marks, starts with a proper noun, and doesn't look
///      like a sentence fragment the LLM accidentally echoed.
/// </summary>
internal static class LocationContextHeuristics
{
    // ── Layer 1: known non-place tokens ─────────────────────────────
    private static readonly string[] NonPlaceTokens =
    [
        "dow jones", "djia",
        "s&p", "s and p", "sp500", "s&p 500",
        "nasdaq", "russell 2000",
        "stock market", "market index", "index level"
    ];

    // ── Layer 2: structural red-flag patterns ───────────────────────

    /// <summary>
    /// Real place names are rarely longer than ~50 characters.
    /// "San Luis Obispo, California, United States" = 43 chars.
    /// </summary>
    private const int MaxPlausiblePlaceLength = 60;

    /// <summary>
    /// Real place names are rarely more than 5 tokens.
    /// "Salt Lake City, Utah" = 4 words.
    /// </summary>
    private const int MaxPlausibleWordCount = 6;

    /// <summary>
    /// Words that never start a geographic place name.
    /// Covers interrogatives, pronouns, and common sentence-start
    /// tokens that small LLMs echo into locationText by mistake.
    /// NOTE: "the" is intentionally absent — legitimate place names
    /// use it (The Bronx, The Hague, The Netherlands).
    /// </summary>
    private static readonly string[] NonPlaceLeadWords =
    [
        "why", "how", "what", "who", "when", "where", "which",
        "is", "are", "do", "does", "did", "can", "could",
        "will", "would", "should", "shall", "may", "might",
        "you", "i", "he", "she", "we", "they", "it",
        "my", "your", "his", "her", "our", "their",
        "hello", "hi", "hey", "please", "tell", "give",
        "if", "but", "and", "or", "so", "because", "since"
    ];

    private static readonly Regex WordSplitRegex = new(
        @"[\s,]+",
        RegexOptions.Compiled);

    public static bool IsClearlyNonPlace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var lower   = trimmed.ToLowerInvariant();

        // Layer 1 — known non-place tokens.
        if (NonPlaceTokens.Any(t => lower.Contains(t, StringComparison.Ordinal)))
            return true;

        // Layer 2 — structural guards.

        // Contains question/exclamation marks → not a place name.
        if (trimmed.Contains('?') || trimmed.Contains('!'))
            return true;

        // Contains sentence-interior period (e.g. "you. Why is Dante").
        // Periods only appear in place names as abbreviations ("St.", "U.S.")
        // — never followed by a space + uppercase letter that starts a new sentence.
        if (HasSentenceInteriorPeriod(trimmed))
            return true;

        // Absolute length guard.
        if (trimmed.Length > MaxPlausiblePlaceLength)
            return true;

        // Word-count guard.
        var words = WordSplitRegex.Split(lower)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToArray();

        if (words.Length > MaxPlausibleWordCount)
            return true;

        // Leading-word guard — geographic names don't start with
        // pronouns, interrogatives, or conversational verbs.
        if (words.Length > 0 &&
            NonPlaceLeadWords.Contains(words[0], StringComparer.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Detects ". X" patterns where X is an uppercase letter — a strong
    /// signal the value is a sentence fragment, not a place abbreviation.
    /// Skips common abbreviations: "St.", "Ft.", "Mt.", "U.S.", "D.C."
    /// </summary>
    private static bool HasSentenceInteriorPeriod(string value)
    {
        for (var i = 0; i < value.Length - 2; i++)
        {
            if (value[i] != '.')
                continue;

            // Skip chained-initial patterns like "U.S." or "D.C."
            if (i >= 1 && char.IsUpper(value[i - 1]) &&
                i + 1 < value.Length && value[i + 1] == '.')
                continue;

            if (i + 2 < value.Length &&
                value[i + 1] == ' ' &&
                char.IsUpper(value[i + 2]))
            {
                // Skip common geographic abbreviations: "St. Louis", "Ft. Worth", "Mt. Rainier"
                if (i >= 2)
                {
                    var pre = value[(i - 2)..i]; // two chars before the period
                    if (pre is "St" or "Ft" or "Mt" or "Dr" or "Pt")
                        continue;
                }

                return true;
            }
        }

        return false;
    }
}
