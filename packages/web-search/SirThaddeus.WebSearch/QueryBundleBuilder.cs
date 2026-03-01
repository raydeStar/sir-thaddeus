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
