using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;

namespace Thaddeus.Runtime.Chat.Pipeline;

/// <summary>
/// Runtime tool-args rewriter that promotes <c>web_search</c> calls
/// issued inside an automation run from <c>recency=any</c> (or missing)
/// to <c>recency=week</c>. Saved automations that check prices,
/// availability, or news tend to surface stale 2024/2025 cached pages
/// otherwise. Passthrough for non-automation turns and non-web-search
/// tools.
/// </summary>
public sealed class AutomationSearchRecencyRewriter : IToolArgsRewriter
{
    public string Rewrite(TurnContext context, string toolName, string argumentsJson)
    {
        if (!context.IsAutomationRun) return argumentsJson;
        if (!string.Equals(toolName, "web_search", StringComparison.OrdinalIgnoreCase))
            return argumentsJson;

        return AutomationToolArgsRewriter.ApplySearchRecencyDefault(argumentsJson);
    }
}
