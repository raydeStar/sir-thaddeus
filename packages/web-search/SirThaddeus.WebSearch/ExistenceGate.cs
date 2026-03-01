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
    private static readonly string[] NegativeSignals =
    [
        "cancelled", "canceled", "ended", "concluded", "only two seasons", "no season 3", "never renewed", "series finale"
    ];

    private static readonly string[] PositiveSignals =
    [
        "s03e01", "season 3 episode 1", "air date", "episode list"
    ];

    public static ExistenceGateResult Evaluate(string question, IReadOnlyList<SearchResult> evidence)
    {
        var score = 0;
        var reasons = new List<string>();

        foreach (var result in evidence)
        {
            var text = $"{result.Title} {result.Snippet}".ToLowerInvariant();
            var weight = DomainWeight(result.Source, result.Url);

            foreach (var signal in NegativeSignals)
            {
                if (!text.Contains(signal, StringComparison.Ordinal))
                    continue;

                score -= 20 * weight;
                reasons.Add($"Negative signal '{signal}' from {result.Source}.");
            }

            foreach (var signal in PositiveSignals)
            {
                if (!text.Contains(signal, StringComparison.Ordinal))
                    continue;

                score += 20 * weight;
                reasons.Add($"Positive signal '{signal}' from {result.Source}.");
            }
        }

        // Strongly bias toward non-existence when question asks for higher season episode.
        if (question.Contains("season 3", StringComparison.OrdinalIgnoreCase) &&
            evidence.Any(e => ($"{e.Title} {e.Snippet}").Contains("season 2", StringComparison.OrdinalIgnoreCase)) &&
            evidence.Any(e => ($"{e.Title} {e.Snippet}").Contains("cancel", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 30;
            reasons.Add("Question targets Season 3 while evidence references cancellation after Season 2.");
        }

        var verdict = score switch
        {
            <= -30 => ExistenceVerdict.DoesNotExist,
            >= 30 => ExistenceVerdict.Exists,
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
        value += NegativeSignals.Count(signal => text.Contains(signal, StringComparison.Ordinal)) * -2;
        value += PositiveSignals.Count(signal => text.Contains(signal, StringComparison.Ordinal)) * 2;
        return value;
    }

    private static int DomainWeight(string source, string url)
    {
        var domain = !string.IsNullOrWhiteSpace(source) ? source : ExtractDomain(url);
        if (domain.Contains("wikipedia.org", StringComparison.OrdinalIgnoreCase)) return 3;
        if (domain.Contains("wikidata.org", StringComparison.OrdinalIgnoreCase)) return 3;
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
