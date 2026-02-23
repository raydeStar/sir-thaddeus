using System.Text.RegularExpressions;

namespace SirThaddeus.PersonalityEngine.Formatting;

public sealed partial class ResponseKindClassifier
{
    public ResponseKind Classify(string text, bool hasToolEvidence)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ResponseKind.Normal;

        if (hasToolEvidence)
            return ResponseKind.ToolResult;

        if (LooksLikeReasoning(text))
            return ResponseKind.Reasoning;

        if (LooksLikeSafetyRefusal(text))
            return ResponseKind.SafetyRefusal;

        if (LooksLikeCodeHeavy(text))
            return ResponseKind.CodeHeavy;

        if (LooksLikeNumericHeavy(text))
            return ResponseKind.NumericHeavy;

        return ResponseKind.Normal;
    }

    private static bool LooksLikeReasoning(string text)
    {
        var lower = text.ToLowerInvariant();
        
        // New structure with tags
        if (lower.Contains("<think>", StringComparison.Ordinal))
            return true;

        // Old legacy structure
        return lower.Contains("facts:", StringComparison.Ordinal) &&
               lower.Contains("goal:", StringComparison.Ordinal) &&
               lower.Contains("basic checks:", StringComparison.Ordinal);
    }

    private static bool LooksLikeSafetyRefusal(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        return lower.Contains("i can't", StringComparison.Ordinal) ||
               lower.Contains("i cannot", StringComparison.Ordinal) ||
               lower.Contains("i won't", StringComparison.Ordinal) ||
               lower.Contains("i will not", StringComparison.Ordinal) ||
               lower.Contains("i'm unable", StringComparison.Ordinal) ||
               lower.Contains("not able to help with that", StringComparison.Ordinal) ||
               lower.Contains("let's keep it respectful", StringComparison.Ordinal);
    }

    private static bool LooksLikeCodeHeavy(string text)
    {
        if (text.Contains("```", StringComparison.Ordinal))
            return true;

        var lines = text.Split('\n');
        var codeLikeLines = lines.Count(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                return false;

            return trimmed.Contains(';', StringComparison.Ordinal) ||
                   trimmed.Contains("=>", StringComparison.Ordinal) ||
                   trimmed.Contains('{', StringComparison.Ordinal) ||
                   trimmed.Contains('}', StringComparison.Ordinal) ||
                   trimmed.StartsWith("public ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("private ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("class ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("def ", StringComparison.Ordinal);
        });

        return codeLikeLines >= 3;
    }

    private static bool LooksLikeNumericHeavy(string text)
    {
        var matches = NumberRegex().Matches(text);
        if (matches.Count < 4)
            return false;

        var letters = text.Count(char.IsLetter);
        var digits = text.Count(char.IsDigit);
        if (letters == 0)
            return true;

        var ratio = digits / (double)Math.Max(1, letters + digits);
        return ratio >= 0.25d;
    }

    [GeneratedRegex(@"-?\d+(\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();
}
