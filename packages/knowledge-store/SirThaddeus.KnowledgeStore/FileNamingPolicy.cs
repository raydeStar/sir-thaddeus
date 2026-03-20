using System.Text.RegularExpressions;

namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Enforces file naming conventions per domain.
/// Consistent naming enables reliable retrieval.
/// </summary>
public sealed partial class FileNamingPolicy
{
    public string GenerateFileName(string domain, FileCreationIntent intent)
    {
        return domain switch
        {
            "journal" => $"{intent.Date:yyyy-MM-dd}.md",

            "health" when intent.SubType == "bloodwork"
                => $"bloodwork-{intent.Date:yyyy-MM-dd}.md",

            "health" when intent.SubType == "sleep"
                => "sleep-log.md",

            _ => SanitizeToKebabCase(intent.ProposedName) + ".md"
        };
    }

    public static string SanitizeToKebabCase(string input)
    {
        var cleaned = NonAlphanumericRegex().Replace(
            input.ToLowerInvariant(), "");
        cleaned = WhitespaceRegex().Replace(cleaned.Trim(), "-");
        cleaned = MultiDashRegex().Replace(cleaned, "-");
        return cleaned.Trim('-');
    }

    [GeneratedRegex(@"[^a-z0-9\s-]")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"-+")]
    private static partial Regex MultiDashRegex();
}
