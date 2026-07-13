using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Detects a self-contained response-format instruction in the request lead.
/// Only the first instruction block is considered so quoted examples cannot
/// accidentally turn a direct answer into a research or tool task.
/// </summary>
public static class ExplicitResponseContractDetector
{
    private static readonly Regex ContractPattern = new(
        @"\b(?:reply|respond|answer|return)\s+(?:with\s+)?only\b|" +
        @"\bput\s+the\s+final\s+answer\s+on\s+its\s+own\s+line\b|" +
        @"\bfinal\s+answer\s+only\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LabeledFinalAnswerRequestPattern = new(
        @"\bput\s+the\s+final\s+answer\s+on\s+its\s+own\s+line\b" +
        @"[^\r\n]{0,160}\bfinal\s+answer\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LabeledFinalAnswerResponsePattern = new(
        @"^\s*final\s+answer\s*:\s*(?<answer>\S(?:.*\S)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex MultipleChoiceLetterRequestPattern = new(
        @"\b(?:correct|corresponding)\s+(?:option\s+)?letter\s+(?:choice|answer)\b|" +
        @"\b(?:answer|respond|reply)\s+(?:using|with)\s+(?:only\s+)?(?:the\s+)?(?:option\s+)?letter\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] ToolSignals =
    [
        "search",
        "research",
        "browse",
        "look up",
        "verify",
        "latest",
        "current",
        "today",
        "http://",
        "https://"
    ];

    public static bool IsNoToolDirectAnswer(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var instructionBlock = GetInstructionBlock(userText);

        if (!ContractPattern.IsMatch(instructionBlock))
            return false;

        return !ToolSignals.Any(signal =>
            instructionBlock.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true when the request lead explicitly requires a labeled
    /// <c>Final answer: ...</c> line. This deliberately ignores later blocks,
    /// where the same text may appear in examples or quoted material.
    /// </summary>
    public static bool RequiresLabeledFinalAnswerLine(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        return LabeledFinalAnswerRequestPattern.IsMatch(GetInstructionBlock(userText));
    }

    /// <summary>
    /// Checks only the requested response shape. It does not interpret or
    /// verify the answer value.
    /// </summary>
    public static bool HasLabeledFinalAnswerLine(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return false;

        var match = LabeledFinalAnswerResponsePattern.Match(responseText);
        if (!match.Success)
            return false;

        var answer = match.Groups["answer"].Value.Trim();
        return !LooksLikePlaceholder(answer);
    }

    /// <summary>
    /// Returns true when an explicit labeled final-answer contract also asks
    /// for a multiple-choice option letter. The value itself remains unknown;
    /// this only identifies the requested response shape.
    /// </summary>
    public static bool RequiresLabeledMultipleChoiceLetter(string? userText)
    {
        if (!RequiresLabeledFinalAnswerLine(userText))
            return false;

        return MultipleChoiceLetterRequestPattern.IsMatch(GetRequestLead(userText!));
    }

    /// <summary>
    /// Checks whether the labeled answer value is exactly one option letter.
    /// It does not determine whether that letter is correct.
    /// </summary>
    public static bool HasLabeledMultipleChoiceLetter(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return false;

        var match = LabeledFinalAnswerResponsePattern.Match(responseText);
        if (!match.Success)
            return false;

        return Regex.IsMatch(
            match.Groups["answer"].Value.Trim(),
            @"^[A-Z]$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikePlaceholder(string answer)
    {
        if (string.Equals(answer, "answer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "letter", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(
            answer,
            @"^(?:<[^<>]+>|\[[^\[\]]+\])$",
            RegexOptions.CultureInvariant);
    }

    private static string GetInstructionBlock(string userText)
    {
        var requestLead = GetRequestLead(userText);
        var firstBlockEnd = requestLead.IndexOf("\n\n", StringComparison.Ordinal);
        var windowsBlockEnd = requestLead.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (windowsBlockEnd >= 0 &&
            (firstBlockEnd < 0 || windowsBlockEnd < firstBlockEnd))
        {
            firstBlockEnd = windowsBlockEnd;
        }

        return firstBlockEnd >= 0
            ? requestLead[..firstBlockEnd]
            : requestLead;
    }

    private static string GetRequestLead(string userText)
        => userText[..Math.Min(userText.Length, 600)];
}
