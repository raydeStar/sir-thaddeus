namespace SirThaddeus.PersonalityEngine.Formatting;

public sealed record ReductionFormatOptions
{
    public bool Enabled { get; init; }
    public string Mode { get; init; } = "";
    public bool CollapseExactDuplicates { get; init; } = true;
    public bool TrimTrailingFluff { get; init; } = true;
    public int SimpleQueryMaxChars { get; init; } = 900;
    public int ComplexQueryMinChars { get; init; } = 700;
    public bool PreferShortIfUserAskedSimple { get; init; } = true;
    public string LatestUserMessage { get; init; } = "";
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
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        if (!ShouldApplyReduction(text, options))
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

    private static bool ShouldApplyReduction(string text, ReductionFormatOptions options)
    {
        var mode = (options.Mode ?? "").Trim().ToLowerInvariant();
        return mode switch
        {
            "always" => true,
            "never" => false,
            "adaptive" => ShouldApplyAdaptiveReduction(text, options),
            _ => options.Enabled
        };
    }

    private static bool ShouldApplyAdaptiveReduction(string text, ReductionFormatOptions options)
    {
        var normalizedSimpleMax = options.SimpleQueryMaxChars <= 0 ? 900 : options.SimpleQueryMaxChars;
        var normalizedComplexMin = options.ComplexQueryMinChars <= 0 ? 700 : options.ComplexQueryMinChars;
        var simpleUserQuery = IsSimpleUserQuery(options.LatestUserMessage);

        if (options.PreferShortIfUserAskedSimple &&
            simpleUserQuery &&
            text.Length <= normalizedSimpleMax)
        {
            return true;
        }

        if (!simpleUserQuery && text.Length >= normalizedComplexMin)
            return false;

        if (text.Length <= normalizedSimpleMax)
            return true;

        if (text.Length >= normalizedComplexMin)
            return false;

        return options.Enabled;
    }

    private static bool IsSimpleUserQuery(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.Length > 100 || trimmed.Contains('\n'))
            return false;

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 16)
            return false;

        var lower = trimmed.ToLowerInvariant();
        if (lower.Contains(" and ", StringComparison.Ordinal) ||
            lower.Contains(" or ", StringComparison.Ordinal) ||
            lower.Contains(" because ", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
