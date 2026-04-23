namespace SirThaddeus.WebSearch;

public static class QueryBundleBuilder
{
    public static IReadOnlyList<string> Build(string userQuestion)
    {
        if (string.IsNullOrWhiteSpace(userQuestion))
            return [];

        var normalized = userQuestion.Trim();
        var seasonEpisode = TryParseSeasonEpisode(normalized);
        if (seasonEpisode is null)
            return [normalized];

        var (entity, season, episode) = seasonEpisode.Value;
        if (string.IsNullOrWhiteSpace(entity))
            return [normalized];

        return
        [
            $"{entity} season {season} episode {episode} plot",
            $"{entity} season {season} cancelled",
            $"{entity} number of seasons",
            $"{entity} season {season} episode list"
        ];
    }

    /// <summary>
    /// Returns a strictly broader rephrasing of <paramref name="query"/>,
    /// or null if the query can't be meaningfully widened (too short,
    /// already-trivial, or already without the restrictive modifiers we
    /// know how to drop). Used as a fallback when the initial search
    /// returns fewer results than expected — often an over-specific
    /// query phrasing that matches nothing.
    ///
    /// <para>Heuristics are intentionally conservative. We drop things
    /// that commonly over-narrow ("exactly", year tags like "in 2025",
    /// format clauses like "answer in two lines"), preserving the
    /// actual subject/topic so the retry is still on-topic.</para>
    ///
    /// <para>Never returns the same string as input — caller can compare
    /// for null-or-equal to decide whether to retry at all.</para>
    /// </summary>
    public static string? TryBroaden(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var broadened = query.Trim();

        // Drop politeness and format/frame directives. Patterns are
        // deliberately specific — "15" in "iPhone 15 release date" is
        // NOT a format-directive hit, so we never treat bare digits as
        // droppable. Number words only count inside explicit length
        // directives ("in exactly two lines").
        const string CountWord = @"(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten)";
        const string IgnoreCase =
            ""; // (placeholder so the compiled flags line below stays stable)
        _ = IgnoreCase;

        // Length / format directives. Each alternative is anchored to
        // specific framing words so stripping never bites into the topic.
        broadened = System.Text.RegularExpressions.Regex.Replace(
            broadened,
            @"\b(?:" +
                @"(?:in\s+exactly\s+|answer\s+in\s+|in\s+)" + CountWord + @"\s+(?:lines?|sentences?|words?|bullets?)|" +
                @"keep\s+it\s+(?:concise|short|brief|terse)|" +
                @"one\s+sentence\s+of\s+context|" +
                @"step[-\s]by[-\s]step|walk\s+me\s+through|" +
                @"please|kindly|briefly|concisely" +
            @")\b",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // "Line N starts with 'X'" and "and Line M starts with 'Y'" —
        // formatting recipes that over-constrain the search query.
        broadened = System.Text.RegularExpressions.Regex.Replace(
            broadened,
            @"(?:and\s+)?line\s+" + CountWord + @"\s+starts?\s+with\s+'[^']*'",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Date / recency anchors that narrow to a specific timeframe.
        // Only strip when preceded by an "as of" / "in" framing word —
        // bare 4-digit years elsewhere (like "iPhone 2024 rumors") stay.
        broadened = System.Text.RegularExpressions.Regex.Replace(
            broadened,
            @"\b(?:as\s+of\s+\d{4}|as\s+of\s+today|as\s+of\s+now|right\s+now|at\s+(?:this\s+moment|the\s+moment))\b",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Drop all quoted substrings — they're the most common source of
        // over-specificity. If the user literally requires the quote,
        // the first search already failed; a broader retry is our last
        // chance to surface anything.
        broadened = System.Text.RegularExpressions.Regex.Replace(
            broadened, "[\"\u201C\u201D]", "");

        // Trim stray punctuation and collapse whitespace.
        broadened = System.Text.RegularExpressions.Regex.Replace(broadened, @"\s+", " ").Trim();
        broadened = broadened.Trim(' ', '?', '.', '!', ',', ';', ':', '(', ')');

        if (broadened.Length < 3) return null;
        if (string.Equals(broadened, query.Trim(), System.StringComparison.OrdinalIgnoreCase))
            return null;

        return broadened;
    }

    private static (string Entity, int Season, int Episode)? TryParseSeasonEpisode(string question)
    {
        var lower = question.ToLowerInvariant();
        var seasonIdx = lower.IndexOf("season ", StringComparison.Ordinal);
        var episodeIdx = lower.IndexOf("episode ", StringComparison.Ordinal);
        if (seasonIdx < 0 || episodeIdx < 0)
            return null;

        var season = TryReadInteger(lower, seasonIdx + "season ".Length);
        var episode = TryReadInteger(lower, episodeIdx + "episode ".Length);
        if (season is null || episode is null)
            return null;

        var marker = lower.IndexOf(" of ", StringComparison.Ordinal);
        if (marker < 0)
            marker = lower.IndexOf(" for ", StringComparison.Ordinal);

        var entity = marker >= 0
            ? question[(marker + 4)..].Trim(' ', '?', '.', '"', '\'')
            : question[..Math.Min(seasonIdx, question.Length)].Trim(' ', '?', '.', '"', '\'');

        return (entity, season.Value, episode.Value);
    }

    private static int? TryReadInteger(string text, int start)
    {
        var end = start;
        while (end < text.Length && char.IsDigit(text[end]))
            end++;

        if (end == start)
            return null;

        return int.TryParse(text[start..end], out var value) ? value : null;
    }
}
