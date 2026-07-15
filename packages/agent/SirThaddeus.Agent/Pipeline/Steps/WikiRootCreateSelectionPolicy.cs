using System.Text.RegularExpressions;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Selects the advertised Wiki root-creation contract for an explicit,
/// unambiguous root-creation request. Existing page mutations intentionally
/// remain free to discover state before choosing a write contract.
/// </summary>
internal static partial class WikiRootCreateSelectionPolicy
{
    private const string RootCreate = "wiki_root_create";

    public static string? TrySelect(string? userText, IReadOnlyList<ToolDefinition> advertisedTools)
    {
        if (string.IsNullOrWhiteSpace(userText) || advertisedTools.Count == 0)
            return null;

        var lower = userText.Trim().ToLowerInvariant();
        if (IsNonActionRequest(lower) || ContainsWord(lower, "page"))
            return null;

        if (!ExplicitRootCreateRegex().IsMatch(lower) && !DirectNeedForRootRegex().IsMatch(lower))
            return null;

        return advertisedTools.Any(tool =>
            string.Equals(tool.Function?.Name, RootCreate, StringComparison.OrdinalIgnoreCase))
            ? RootCreate
            : null;
    }

    private static bool IsNonActionRequest(string lower) =>
        lower.StartsWith("how ", StringComparison.Ordinal) ||
        lower.StartsWith("explain ", StringComparison.Ordinal) ||
        lower.StartsWith("describe ", StringComparison.Ordinal) ||
        lower.StartsWith("what would ", StringComparison.Ordinal) ||
        ContainsWord(lower, "maybe") ||
        ContainsWord(lower, "hypothetically") ||
        lower.Contains("do not ", StringComparison.Ordinal) ||
        lower.Contains("don't ", StringComparison.Ordinal) ||
        lower.Contains(" later", StringComparison.Ordinal);

    private static bool ContainsWord(string text, string word)
    {
        var start = 0;
        while ((start = text.IndexOf(word, start, StringComparison.Ordinal)) >= 0)
        {
            var beforeIsWord = start > 0 && char.IsLetterOrDigit(text[start - 1]);
            var afterIndex = start + word.Length;
            var afterIsWord = afterIndex < text.Length && char.IsLetterOrDigit(text[afterIndex]);
            if (!beforeIsWord && !afterIsWord)
                return true;
            start = afterIndex;
        }

        return false;
    }

    [GeneratedRegex(
        @"^(?:(?:please|kindly)\s*,?\s*|(?:can|could|would|will)\s+you\s+(?:please\s+)?|(?:i\s+(?:want|need|would\s+like)\s+you\s+to)\s+|(?:i(?:'d|\s+would)\s+like\s+you\s+to)\s+|(?:go\s+ahead\s+and)\s+)?(?:create|add|make|start|open|establish|initialize|prepare|provision|build|set\s+up|spin\s+up)\b.{0,96}\bwiki(?:\s+canvas)?\s+root\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitRootCreateRegex();

    [GeneratedRegex(
        @"^i\s+need\s+(?:a\s+)?(?:new\s+)?wiki(?:\s+canvas)?\s+root\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectNeedForRootRegex();
}
