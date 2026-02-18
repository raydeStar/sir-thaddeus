namespace SirThaddeus.PersonalityEngine.Formatting;

public sealed record ReductionFormatOptions
{
    public bool Enabled { get; init; }
    public bool CollapseExactDuplicates { get; init; } = true;
    public bool TrimTrailingFluff { get; init; } = true;
}

/// <summary>
/// Optional reduction pass for normal conversational text.
/// Must never delete arbitrary sentences or alter numeric facts.
/// </summary>
public static class ReductionFormatter
{
    private static readonly string[] KnownTrailingFluff =
    [
        "Need another quick one?",
        "Want me to dig deeper into any item?",
        "Want details on any specific capability?",
        "Anything else weather-related?",
        "Need another unit converted?"
    ];

    public static string Apply(string text, ReductionFormatOptions options)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace('\r', '\n');

        var paragraphs = normalized
            .Split(["\n\n"], StringSplitOptions.None)
            .Select(static p => p.Trim())
            .Where(static p => p.Length > 0)
            .ToList();

        if (paragraphs.Count == 0)
            return text ?? "";

        if (options.CollapseExactDuplicates && paragraphs.Count > 1)
        {
            var collapsed = new List<string>(paragraphs.Count);
            foreach (var paragraph in paragraphs)
            {
                if (collapsed.Count > 0 &&
                    string.Equals(collapsed[^1], paragraph, StringComparison.Ordinal))
                {
                    continue;
                }
                collapsed.Add(paragraph);
            }
            paragraphs = collapsed;
        }

        if (options.TrimTrailingFluff && paragraphs.Count > 1)
        {
            var tail = paragraphs[^1];
            if (KnownTrailingFluff.Any(fluff =>
                    tail.Contains(fluff, StringComparison.OrdinalIgnoreCase)))
            {
                paragraphs.RemoveAt(paragraphs.Count - 1);
            }
        }

        return string.Join("\n\n", paragraphs).Trim();
    }
}
