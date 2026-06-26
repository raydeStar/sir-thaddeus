using System.Globalization;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Reasoning;

/// <summary>
/// Self-consistency: sample the model several times on a reasoning prompt,
/// extract each attempt's final answer, and take the majority vote. Lifts
/// small-model accuracy on multi-step problems where a single sample is
/// high-variance — without any hardcoding, since the model still does all the
/// reasoning. Only the aggregation lives here.
/// </summary>
public static class SelfConsistency
{
    private static readonly Regex FinalAnswerLine = new(
        @"final\s*answer\s*[:\-]\s*(?<val>.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumberToken = new(
        @"-?\d+(?:\.\d+)?",
        RegexOptions.Compiled);

    // A–J so it covers MMLU-Pro (up to 10 options), not just 4-way A–D.
    private static readonly Regex ChoiceToken = new(
        @"(?<![A-Za-z])([A-J])(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Extracts a normalized numeric answer, preferring the value on a
    /// "Final answer:" line and otherwise the last number in the text.</summary>
    public static string? ExtractNumeric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var finals = FinalAnswerLine.Matches(text);
        if (finals.Count > 0)
        {
            var fromFinal = LastNumber(finals[^1].Groups["val"].Value);
            if (fromFinal is not null)
                return fromFinal;
        }

        return LastNumber(text);
    }

    /// <summary>Extracts a single multiple-choice letter (A–D), preferring the
    /// "Final answer:" line and otherwise the last standalone letter.</summary>
    public static string? ExtractChoice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var finals = FinalAnswerLine.Matches(text);
        if (finals.Count > 0)
        {
            var m = ChoiceToken.Match(finals[^1].Groups["val"].Value);
            if (m.Success)
                return m.Groups[1].Value.ToUpperInvariant();
        }

        var all = ChoiceToken.Matches(text);
        return all.Count > 0 ? all[^1].Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>Majority vote over candidate completions using the given
    /// answer extractor. Ties break toward the first answer seen (stable), so a
    /// single sample degrades gracefully to "return that answer".</summary>
    public static SelfConsistencyResult Vote(IReadOnlyList<string> candidates, Func<string?, string?> extract)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(extract);

        var (counts, firstSeen) = Tally(candidates, extract);
        if (firstSeen.Count == 0)
            return new SelfConsistencyResult(null, 0, candidates.Count);

        var winner = firstSeen[0];
        foreach (var answer in firstSeen)
        {
            if (counts[answer] > counts[winner])
                winner = answer;
        }

        return new SelfConsistencyResult(winner, counts[winner], candidates.Count);
    }

    /// <summary>True when the majority winner can no longer change no matter
    /// what the remaining samples say — i.e. the leader's lead over the runner-up
    /// already exceeds the samples still to come. Lets the caller stop sampling
    /// early once the outcome is locked, cutting cost with no change to the
    /// result. Needs at least one parsed answer; returns false otherwise.</summary>
    public static bool MajorityLocked(
        IReadOnlyList<string> candidatesSoFar, Func<string?, string?> extract, int maxSamples)
    {
        ArgumentNullException.ThrowIfNull(candidatesSoFar);
        ArgumentNullException.ThrowIfNull(extract);

        var (counts, _) = Tally(candidatesSoFar, extract);
        if (counts.Count == 0)
            return false;

        var ordered = counts.Values.OrderByDescending(v => v).ToList();
        var leader = ordered[0];
        var runnerUp = ordered.Count > 1 ? ordered[1] : 0;
        var remaining = Math.Max(0, maxSamples - candidatesSoFar.Count);
        return leader - runnerUp > remaining;
    }

    private static (Dictionary<string, int> Counts, List<string> FirstSeen) Tally(
        IReadOnlyList<string> candidates, Func<string?, string?> extract)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstSeen = new List<string>();

        foreach (var candidate in candidates)
        {
            var answer = extract(candidate);
            if (string.IsNullOrWhiteSpace(answer))
                continue;
            if (!counts.ContainsKey(answer))
            {
                counts[answer] = 0;
                firstSeen.Add(answer);
            }
            counts[answer]++;
        }

        return (counts, firstSeen);
    }

    private static string? LastNumber(string segment)
    {
        var matches = NumberToken.Matches(segment.Replace(",", string.Empty));
        if (matches.Count == 0)
            return null;

        var raw = matches[^1].Value;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            if (Math.Abs(value - Math.Round(value)) < 1e-9 && Math.Abs(value) < 1e15)
                return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
            return value.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        return raw;
    }
}

/// <summary>Outcome of a self-consistency vote: the winning answer (null if no
/// sample produced a parseable answer), how many samples agreed, and the total
/// number of samples.</summary>
public sealed record SelfConsistencyResult(string? Answer, int Votes, int Samples);
