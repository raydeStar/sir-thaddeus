using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    private static IReadOnlyList<ToolDefinition> ApplyCapabilityManifestToolPolicy(
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<ToolDefinition> allTools,
        RouterOutput route,
        string lowerIncoming)
    {
        if (!LooksLikeCapabilityManifestRequest(lowerIncoming))
            return tools;

        if (route.Intent.Equals(Intents.GeneralTool, StringComparison.OrdinalIgnoreCase) &&
            !tools.Any(t => IsCapabilityManifestToolName(t.Function.Name)))
        {
            var metaTool = allTools.FirstOrDefault(t =>
                IsCapabilityManifestToolName(t.Function.Name));

            if (metaTool is not null && !string.IsNullOrWhiteSpace(metaTool.Function.Name))
                return [.. tools, metaTool];
        }

        return tools;
    }

    private static bool IsCapabilityManifestToolName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        return toolName.Equals("tool_list_capabilities", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("ToolListCapabilities", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCapabilityManifestRequest(string lowerIncoming)
    {
        if (string.IsNullOrWhiteSpace(lowerIncoming))
            return false;

        return lowerIncoming.Contains("tool_list_capabilities", StringComparison.Ordinal) ||
               lowerIncoming.Contains("tool capabilities", StringComparison.Ordinal) ||
               lowerIncoming.Contains("capability groups", StringComparison.Ordinal) ||
               lowerIncoming.Contains("list tools", StringComparison.Ordinal) ||
               lowerIncoming.Contains("what tools", StringComparison.Ordinal) ||
               lowerIncoming.Contains("available tools", StringComparison.Ordinal);
    }
}
