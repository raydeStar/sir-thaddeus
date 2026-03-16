using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent.Search;

public sealed partial class SearchOrchestrator
{
    private AgentResponse? TryBuildExistenceGuardedResponse(
        string userMessage,
        IReadOnlyList<SourceItem> initialSources,
        List<ToolCallRecord> toolCallsMade)
    {
        var queryBundle = BuildExistenceQueryBundle(userMessage);
        if (queryBundle.Count <= 1)
            return null;

        var evidence = initialSources
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .ToList();
        const bool addedFollowupEvidence = false;

        if (evidence.Count == 0)
            return null;

        if (!IsLikelyNonexistent(userMessage, evidence, out var nonexistenceScore))
            return null;

        var seasonLabel = TryExtractSeasonLabel(userMessage);
        var seasonPhrase = seasonLabel is null ? "the requested installment" : seasonLabel;
        var text =
            $"Based on available sources, {seasonPhrase} does not exist. " +
            "The evidence indicates it was canceled or never released, so there is no official episode plot to summarize.";

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "EXISTENCE_GUARD_TRIGGERED",
            Result = "does_not_exist",
            Details = new Dictionary<string, object>
            {
                ["query_bundle_count"] = queryBundle.Count,
                ["evidence_count"] = evidence.Count,
                ["nonexistence_score"] = nonexistenceScore,
                ["added_followup_evidence"] = addedFollowupEvidence
            }
        });

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = 0
        };
    }

    internal static IReadOnlyList<string> BuildExistenceQueryBundle(string userQuestion)
    {
        if (string.IsNullOrWhiteSpace(userQuestion))
            return [];

        var normalized = userQuestion.Trim();
        var lower = normalized.ToLowerInvariant();
        var hasSeasonEpisode =
            Regex.IsMatch(lower, @"\bseason\s+\d+\b") &&
            Regex.IsMatch(lower, @"\bepisode\s+\d+\b");
        if (!hasSeasonEpisode)
            return [normalized];

        var parsed = TryParseSeasonEpisode(normalized);
        if (parsed is null)
        {
            return
            [
                normalized,
                $"{normalized} cancelled",
                $"{normalized} number of seasons",
                $"{normalized} episode list"
            ];
        }

        var (entity, season, episode) = parsed.Value;
        if (string.IsNullOrWhiteSpace(entity))
        {
            return
            [
                normalized,
                $"{normalized} cancelled",
                $"{normalized} number of seasons",
                $"{normalized} episode list"
            ];
        }

        return
        [
            $"{entity} season {season} episode {episode} plot",
            $"{entity} season {season} cancelled",
            $"{entity} number of seasons",
            $"{entity} season {season} episode list"
        ];
    }

    internal static bool IsLikelyNonexistent(
        string question,
        IReadOnlyList<SourceItem> evidence,
        out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(question) || evidence.Count == 0)
            return false;

        foreach (var source in evidence)
        {
            var text = $"{source.Title} {source.Snippet}".ToLowerInvariant();

            if (text.Contains("does not exist", StringComparison.Ordinal) ||
                text.Contains("doesn't exist", StringComparison.Ordinal) ||
                text.Contains("never renewed", StringComparison.Ordinal) ||
                text.Contains("never released", StringComparison.Ordinal) ||
                text.Contains("no season", StringComparison.Ordinal) ||
                text.Contains("no episode", StringComparison.Ordinal) ||
                text.Contains("canceled", StringComparison.Ordinal) ||
                text.Contains("cancelled", StringComparison.Ordinal) ||
                text.Contains("ended after season", StringComparison.Ordinal))
            {
                score += 6;
            }

            if (text.Contains("episode list", StringComparison.Ordinal) ||
                text.Contains("air date", StringComparison.Ordinal) ||
                text.Contains("released", StringComparison.Ordinal) ||
                text.Contains("available now", StringComparison.Ordinal))
            {
                score -= 3;
            }
        }

        var seasonLabel = TryExtractSeasonLabel(question);
        if (!string.IsNullOrWhiteSpace(seasonLabel))
        {
            var seasonNumberMatch = Regex.Match(seasonLabel, @"\d+");
            if (seasonNumberMatch.Success &&
                int.TryParse(seasonNumberMatch.Value, out var requestedSeason) &&
                requestedSeason > 1)
            {
                var priorSeasonLabel = $"season {requestedSeason - 1}";
                var hasPriorSeason = evidence.Any(s =>
                    ($"{s.Title} {s.Snippet}")
                    .Contains(priorSeasonLabel, StringComparison.OrdinalIgnoreCase));
                var hasCancelSignal = evidence.Any(s =>
                    ($"{s.Title} {s.Snippet}")
                    .Contains("cancel", StringComparison.OrdinalIgnoreCase));

                if (hasPriorSeason && hasCancelSignal)
                    score += 10;
            }
        }

        return score >= 12;
    }

    private static string? TryExtractSeasonLabel(string userMessage)
    {
        var match = Regex.Match(userMessage ?? "", @"\bseason\s+\d+\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static (string Entity, int Season, int Episode)? TryParseSeasonEpisode(string question)
    {
        var lower = question.ToLowerInvariant();
        var seasonMatch = Regex.Match(lower, @"\bseason\s+(\d+)\b");
        var episodeMatch = Regex.Match(lower, @"\bepisode\s+(\d+)\b");
        if (!seasonMatch.Success || !episodeMatch.Success)
            return null;

        if (!int.TryParse(seasonMatch.Groups[1].Value, out var season) ||
            !int.TryParse(episodeMatch.Groups[1].Value, out var episode))
        {
            return null;
        }

        var marker = lower.IndexOf(" of ", StringComparison.Ordinal);
        if (marker < 0)
            marker = lower.IndexOf(" for ", StringComparison.Ordinal);

        var entity = marker >= 0
            ? question[(marker + 4)..].Trim(' ', '?', '.', '"', '\'')
            : question[..Math.Min(seasonMatch.Index, question.Length)].Trim(' ', '?', '.', '"', '\'');

        return (entity, season, episode);
    }
}
