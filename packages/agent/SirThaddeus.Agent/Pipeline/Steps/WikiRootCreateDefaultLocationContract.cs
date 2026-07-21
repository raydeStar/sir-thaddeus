using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Removes the application-owned Wiki library path from the first model-visible
/// root-creation contract when the user requested no custom location. The MCP
/// tool and permission boundary remain unchanged.
/// </summary>
internal static class WikiRootCreateDefaultLocationContract
{
    private const string RootCreateToolName = "wiki_root_create";

    public static IReadOnlyList<ToolDefinition> Project(
        string? userText,
        string? selectedTool,
        IReadOnlyList<ToolDefinition> advertisedTools)
    {
        if (!string.Equals(selectedTool, RootCreateToolName, StringComparison.OrdinalIgnoreCase) ||
            HasExplicitLocationRequest(userText))
        {
            return advertisedTools;
        }

        var projected = advertisedTools.ToArray();
        for (var i = 0; i < projected.Length; i++)
        {
            var tool = projected[i];
            if (tool.Function is not { } function ||
                !string.Equals(function.Name, RootCreateToolName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            projected[i] = tool with
            {
                Function = function with
                {
                    Description =
                        "Create a local Wiki Canvas root in the configured wiki library directory.",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["name"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["description"] = "Display name for the wiki root.",
                            },
                        },
                        ["required"] = new[] { "name" },
                        ["additionalProperties"] = false,
                    },
                },
            };
            return projected;
        }

        return advertisedTools;
    }

    internal static bool HasExplicitLocationRequest(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var lower = userText.ToLowerInvariant();
        if (lower.Contains('/') || lower.Contains('\\'))
            return true;

        return ContainsWord(lower, "path") ||
               ContainsWord(lower, "folder") ||
               ContainsWord(lower, "directory") ||
               ContainsWord(lower, "location") ||
               ContainsWord(lower, "drive") ||
               ContainsWord(lower, "under") ||
               ContainsWord(lower, "inside") ||
               ContainsWord(lower, "within");
    }

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
}
