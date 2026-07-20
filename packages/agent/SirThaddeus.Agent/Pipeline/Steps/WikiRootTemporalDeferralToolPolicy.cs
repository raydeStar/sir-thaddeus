using System.Text.RegularExpressions;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Withholds root creation when the user schedules or conditions the mutation
/// for a later time. Explicit immediate language wins over incidental temporal
/// context such as a future meeting or the root's eventual use.
/// </summary>
internal static partial class WikiRootTemporalDeferralToolPolicy
{
    private const string RootCreateToolName = "wiki_root_create";

    public static IReadOnlyList<ToolDefinition> Project(
        string? userText,
        string? forcedTool,
        IReadOnlyList<ToolDefinition> advertisedTools)
    {
        if (string.Equals(forcedTool, RootCreateToolName, StringComparison.OrdinalIgnoreCase) ||
            !IsDeferredRootCreateRequest(userText))
        {
            return advertisedTools;
        }

        var projected = advertisedTools
            .Where(tool => !string.Equals(
                tool.Function?.Name,
                RootCreateToolName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return projected.Length == advertisedTools.Count ? advertisedTools : projected;
    }

    public static bool IsDeferredRootCreateRequest(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var lower = userText.Trim().ToLowerInvariant();
        if (!RootCreateIntentRegex().IsMatch(lower))
            return false;

        if (ExplicitNotNowRegex().IsMatch(lower) ||
            LeadingConditionRegex().IsMatch(lower))
        {
            return true;
        }

        if (ImmediateExecutionRegex().IsMatch(lower) ||
            FuturePurposeRegex().IsMatch(lower))
        {
            return false;
        }

        return ScheduledFutureRegex().IsMatch(lower);
    }

    [GeneratedRegex(
        @"\b(?:create|add|make|start|open|establish|initialize|prepare|provision|build|set\s+up|spin\s+up)\b.{0,128}\bwiki(?:\s+canvas)?\s+root\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RootCreateIntentRegex();

    [GeneratedRegex(
        @"\b(?:not\s+now|do\s+nothing\s+yet|make\s+no\s+changes\s+(?:now|today)|leave\s+(?:it|the\s+wiki)\s+(?:unchanged|untouched)\s+(?:for\s+now|today)|wait\s+(?:for|until)|not\s+during\s+this\s+session)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitNotNowRegex();

    [GeneratedRegex(
        @"^(?:(?:please|kindly)\s*,?\s*)?(?:when|once|after|only\s+after|if)\b.{0,192}\b(?:create|add|make|start|open|establish|initialize|prepare|provision|build|set\s+up|spin\s+up)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingConditionRegex();

    [GeneratedRegex(
        @"\b(?:now|right\s+now|immediately|today)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImmediateExecutionRegex();

    [GeneratedRegex(
        @"\bfor\s+(?:tomorrow|next\s+(?:week|month|quarter|year))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FuturePurposeRegex();

    [GeneratedRegex(
        @"\b(?:tomorrow|next\s+(?:week|month|quarter|year)|someday|eventually)\b\s*(?:[,.;!?]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScheduledFutureRegex();
}
