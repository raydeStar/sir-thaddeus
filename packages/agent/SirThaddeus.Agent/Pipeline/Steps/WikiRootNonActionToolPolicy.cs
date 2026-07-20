using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Withholds the Wiki root mutation contract when the existing deterministic
/// selector has already classified the user's request as informational,
/// hypothetical, negated, or deferred. Read-only Wiki capabilities and the
/// runtime permission boundary remain unchanged.
/// </summary>
internal static class WikiRootNonActionToolPolicy
{
    private const string RootCreateToolName = "wiki_root_create";

    public static IReadOnlyList<ToolDefinition> Project(
        string? userText,
        string? forcedTool,
        IReadOnlyList<ToolDefinition> advertisedTools)
    {
        if (string.Equals(forcedTool, RootCreateToolName, StringComparison.OrdinalIgnoreCase) ||
            !WikiRootCreateSelectionPolicy.IsExplicitNonActionRequest(userText))
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
}
