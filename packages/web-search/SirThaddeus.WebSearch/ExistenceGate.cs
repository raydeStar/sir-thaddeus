using System.Text.RegularExpressions;

namespace SirThaddeus.WebSearch;

public enum ExistenceVerdict
{
    Exists,
    DoesNotExist,
    Unclear
}

public sealed record ExistenceEvidence(string Title, string Snippet, string Url, string Domain);

public sealed record ExistenceGateResult(
    ExistenceVerdict Verdict,
    int Score,
    IReadOnlyList<ExistenceEvidence> Evidence,
    IReadOnlyList<string> Reasons);

public static class ExistenceGate
{
    private static readonly (string Signal, int Weight)[] NegativeSignals =
    [
        ("does not exist", 5),
        ("doesn't exist", 5),
        ("not real", 4),
        ("fictional", 3),
        ("hoax", 3),
        ("fake", 3),
        ("never released", 5),
        ("no release", 5),
        ("unreleased", 4),
        ("not released", 4),
        ("not announced", 3),
        ("no official release", 4),
        ("cancelled", 5),
        ("canceled", 5),
        ("never renewed", 4),
        ("ended", 3),
        ("concluded", 3),
        ("series finale", 3),
        ("discontinued", 4),
        ("no such", 4),
        ("no record of", 4),
        ("not found", 3),
        ("debunked", 4),
        ("myth", 3)
    ];

    private static readonly (string Signal, int Weight)[] PositiveSignals =
    [
        ("confirmed", 3),
        ("confirms", 3),
        ("announced", 3),
        ("available now", 3),
        ("air date", 3),
        ("episode list", 3),
        ("track listing", 3),
        ("product page", 3),
        ("documentation", 2),
        ("release notes", 2),
        ("specifications", 2)
    ];

    public static ExistenceGateResult Evaluate(string question, IReadOnlyList<SearchResult> evidence)
    {
        var score = 0;
        var reasons = new List<string>();

        var requestedInstallment = TryParseRequestedSeasonEpisode(question);

        foreach (var result in evidence)
        {
            var text = $"{result.Title} {result.Snippet}".ToLowerInvariant();
            var weight = DomainWeight(result.Source, result.Url);

            foreach (var (signal, signalWeight) in NegativeSignals)
            {
                if (!text.Contains(signal, StringComparison.Ordinal))
                    continue;

                score -= signalWeight * weight;
                reasons.Add($"Negative signal '{signal}' from {result.Source}.");
            }

            foreach (var (signal, signalWeight) in PositiveSignals)
            {
                if (!text.Contains(signal, StringComparison.Ordinal))
                    continue;

                score += signalWeight * weight;
                reasons.Add($"Positive signal '{signal}' from {result.Source}.");
            }

            if (requestedInstallment is { } installment &&
                ContainsRequestedInstallment(text, installment.Season, installment.Episode))
            {
                score += 4 * weight;
                reasons.Add($"Positive signal for requested season/episode from {result.Source}.");
            }
        }

        if (requestedInstallment is { Season: > 1 } requested &&
            EvidenceReferencesEarlierSeasonCancellation(evidence, requested.Season, out var referencedSeason))
        {
            score -= 10;
            reasons.Add($"Question targets Season {requested.Season} while evidence references cancellation after Season {referencedSeason}.");
        }

        var verdict = score switch
        {
            <= -12 => ExistenceVerdict.DoesNotExist,
            >= 12 => ExistenceVerdict.Exists,
            _ => ExistenceVerdict.Unclear
        };

        var topEvidence = evidence
            .OrderByDescending(e => Math.Abs(SignalStrength(e)))
            .Take(6)
            .Select(e => new ExistenceEvidence(e.Title, e.Snippet, e.Url, e.Source))
            .ToList();

        return new ExistenceGateResult(verdict, score, topEvidence, reasons.Take(8).ToList());
    }

    private static int SignalStrength(SearchResult result)
    {
        var text = $"{result.Title} {result.Snippet}".ToLowerInvariant();
        var value = 0;
        value += NegativeSignals.Count(signal => text.Contains(signal.Signal, StringComparison.Ordinal)) * -2;
        value += PositiveSignals.Count(signal => text.Contains(signal.Signal, StringComparison.Ordinal)) * 2;
        return value;
    }

    private static (int Season, int Episode)? TryParseRequestedSeasonEpisode(string question)
    {
        var seasonMatch = Regex.Match(question ?? string.Empty, @"\b(?:season\s+|s)(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var episodeMatch = Regex.Match(question ?? string.Empty, @"\b(?:episode\s+|ep\s*|e)(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!seasonMatch.Success || !episodeMatch.Success)
            return null;

        if (!int.TryParse(seasonMatch.Groups[1].Value, out var season) ||
            !int.TryParse(episodeMatch.Groups[1].Value, out var episode))
        {
            return null;
        }

        return (season, episode);
    }

    private static bool ContainsRequestedInstallment(string lowerText, int season, int episode)
    {
        if (lowerText.Contains($"season {season}", StringComparison.Ordinal) &&
            lowerText.Contains($"episode {episode}", StringComparison.Ordinal))
        {
            return true;
        }

        return Regex.IsMatch(
            lowerText,
            $@"\bs0*{season}\s*e0*{episode}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool EvidenceReferencesEarlierSeasonCancellation(
        IReadOnlyList<SearchResult> evidence,
        int requestedSeason,
        out int referencedSeason)
    {
        referencedSeason = 0;
        foreach (var result in evidence)
        {
            var text = $"{result.Title} {result.Snippet}";
            if (!text.Contains("cancel", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("ended", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("never renewed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(text, @"\bseason\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (int.TryParse(match.Groups[1].Value, out var season) &&
                    season > referencedSeason &&
                    season < requestedSeason)
                {
                    referencedSeason = season;
                }
            }
        }

        return referencedSeason > 0;
    }

    private static int DomainWeight(string source, string url)
    {
        var domain = !string.IsNullOrWhiteSpace(source) ? source : ExtractDomain(url);
        if (domain.Contains("wikipedia.org", StringComparison.OrdinalIgnoreCase)) return 3;
        if (domain.Contains("wikidata.org", StringComparison.OrdinalIgnoreCase)) return 3;
        if (domain.Contains(".gov", StringComparison.OrdinalIgnoreCase)) return 3;
        if (domain.Contains(".edu", StringComparison.OrdinalIgnoreCase)) return 2;
        if (domain.Contains("github.com", StringComparison.OrdinalIgnoreCase)) return 2;
        if (domain.Contains("docs.", StringComparison.OrdinalIgnoreCase)) return 2;
        if (domain.Contains("imdb.com", StringComparison.OrdinalIgnoreCase)) return 2;
        if (domain.Contains("tvguide.com", StringComparison.OrdinalIgnoreCase)) return 2;
        if (domain.Contains("fandom.com", StringComparison.OrdinalIgnoreCase)) return 1;
        if (domain.Contains("reddit.com", StringComparison.OrdinalIgnoreCase)) return 1;
        if (domain.Contains("fanfiction", StringComparison.OrdinalIgnoreCase)) return 0;
        return 1;
    }

    private static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host;
        return string.Empty;
    }
}
