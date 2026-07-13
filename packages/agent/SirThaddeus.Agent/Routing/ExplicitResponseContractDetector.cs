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

        var requestLead = userText[..Math.Min(userText.Length, 600)];
        var firstBlockEnd = requestLead.IndexOf("\n\n", StringComparison.Ordinal);
        var instructionBlock = firstBlockEnd >= 0
            ? requestLead[..firstBlockEnd]
            : requestLead;

        if (!ContractPattern.IsMatch(instructionBlock))
            return false;

        return !ToolSignals.Any(signal =>
            instructionBlock.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }
}
