using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Search;

internal static class SearchResponseFormatter
{
    private static readonly Regex InterWordApostropheRegex = new(
        @"(?<=\p{L})['\u2019`](?=\p{L})",
        RegexOptions.Compiled);

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        var lines = normalized.Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                kept.Add(line);
                continue;
            }

            // Guard against UI-specific leakage into assistant text.
            if (line.Contains("Open the **Briefing** tab", StringComparison.OrdinalIgnoreCase))
                continue;

            kept.Add(NormalizeLine(line));
        }

        return CollapseBlankLines(string.Join("\n", kept)).Trim();
    }

    private static string NormalizeLine(string line)
    {
        // Keep punctuation normalization narrowly scoped to inter-word apostrophes
        // so keyword matching remains stable (e.g. McDonald's -> McDonalds).
        return InterWordApostropheRegex.Replace(line, "");
    }

    private static string CollapseBlankLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);
        var blankRun = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blankRun++;
                if (blankRun > 1)
                    continue;
            }
            else
            {
                blankRun = 0;
            }

            result.Add(line);
        }

        return string.Join("\n", result);
    }
}

