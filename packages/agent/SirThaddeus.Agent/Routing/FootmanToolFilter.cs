using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Narrows a tool-definition list to the set the footman thinks is relevant
/// for the current turn. Pure function — all behavior is decided by the
/// <see cref="RoutingDecision"/> and <see cref="ToolCapabilityRegistry"/>.
/// Unit-tested so the runtime (<c>LmStudioAssistant</c>) can delegate to it
/// without pulling runtime dependencies into the test project.
///
/// Fail-open semantics:
/// <list type="bullet">
///   <item>A non-authoritative decision (abstain or sub-threshold confidence)
///         returns the input unchanged — the primary model gets the full
///         tool menu.</item>
///   <item>If every tool got filtered out (e.g. the registry is out of date
///         vs. the MCP server), we return the input so the primary model
///         isn't paralyzed.</item>
///   <item>Tools that aren't in the capability registry pass through —
///         newly added MCP tools shouldn't be accidentally hidden by a
///         stale map.</item>
/// </list>
/// </summary>
public static class FootmanToolFilter
{
    /// <summary>
    /// Callers may opt certain tool names into an always-allow list (e.g.
    /// the runtime's virtual <c>propose_automation</c>). Matching is
    /// case-insensitive.
    /// </summary>
    /// <param name="toolDefs">Tool definitions advertised to the primary model.</param>
    /// <param name="decision">Footman routing decision for this turn.</param>
    /// <param name="alwaysAllowToolNames">Tools that pass the filter
    /// regardless of the footman decision.</param>
    public static IReadOnlyList<ToolDefinition> Filter(
        IReadOnlyList<ToolDefinition> toolDefs,
        RoutingDecision decision,
        IEnumerable<string>? alwaysAllowToolNames = null)
    {
        ArgumentNullException.ThrowIfNull(toolDefs);
        ArgumentNullException.ThrowIfNull(decision);

        if (toolDefs.Count == 0 || !decision.IsAuthoritative)
            return toolDefs;

        var alwaysAllow = alwaysAllowToolNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(alwaysAllowToolNames, StringComparer.OrdinalIgnoreCase);

        var allowedCapabilities = new HashSet<ToolCapability>(
            ToolFamilyPolicy.ToCapabilities(decision.AllowedToolFamilies));

        var filtered = new List<ToolDefinition>(toolDefs.Count);
        foreach (var def in toolDefs)
        {
            var name = def.Function.Name ?? string.Empty;
            if (alwaysAllow.Contains(name))
            {
                filtered.Add(def);
                continue;
            }

            var capability = ToolCapabilityRegistry.ResolveCapability(name);
            if (capability is null)
            {
                filtered.Add(def);
                continue;
            }

            if (allowedCapabilities.Contains(capability.Value))
                filtered.Add(def);
        }

        return filtered.Count == 0 ? toolDefs : filtered;
    }
}
