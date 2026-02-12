using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

/// <summary>
/// Deterministic mapping between tool names and logical capabilities.
/// Unmapped tools are considered hidden by default.
/// </summary>
public static class ToolCapabilityRegistry
{
    private static readonly IReadOnlyDictionary<string, ToolCapability> CapabilityByToolName =
        new Dictionary<string, ToolCapability>(StringComparer.OrdinalIgnoreCase)
        {
            // Memory read
            ["memory_retrieve"] = ToolCapability.MemoryRead,
            ["MemoryRetrieve"] = ToolCapability.MemoryRead,
            ["memory_list_facts"] = ToolCapability.MemoryRead,
            ["MemoryListFacts"] = ToolCapability.MemoryRead,

            // Memory write
            ["memory_store_facts"] = ToolCapability.MemoryWrite,
            ["MemoryStoreFacts"] = ToolCapability.MemoryWrite,
            ["memory_update_fact"] = ToolCapability.MemoryWrite,
            ["MemoryUpdateFact"] = ToolCapability.MemoryWrite,
            ["memory_delete_fact"] = ToolCapability.MemoryWrite,
            ["MemoryDeleteFact"] = ToolCapability.MemoryWrite,

            // Search/web reading
            ["web_search"] = ToolCapability.WebSearch,
            ["WebSearch"] = ToolCapability.WebSearch,
            ["browser_navigate"] = ToolCapability.BrowserNavigate,
            ["BrowserNavigate"] = ToolCapability.BrowserNavigate,
            ["weather_geocode"] = ToolCapability.WebSearch,
            ["WeatherGeocode"] = ToolCapability.WebSearch,
            ["weather_forecast"] = ToolCapability.WebSearch,
            ["WeatherForecast"] = ToolCapability.WebSearch,
            ["resolve_timezone"] = ToolCapability.WebSearch,
            ["ResolveTimezone"] = ToolCapability.WebSearch,
            ["holidays_get"] = ToolCapability.WebSearch,
            ["HolidaysGet"] = ToolCapability.WebSearch,
            ["holidays_next"] = ToolCapability.WebSearch,
            ["HolidaysNext"] = ToolCapability.WebSearch,
            ["holidays_is_today"] = ToolCapability.WebSearch,
            ["HolidaysIsToday"] = ToolCapability.WebSearch,
            ["feed_fetch"] = ToolCapability.WebSearch,
            ["FeedFetch"] = ToolCapability.WebSearch,
            ["status_check_url"] = ToolCapability.WebSearch,
            ["StatusCheckUrl"] = ToolCapability.WebSearch,

            // File tools
            ["file_read"] = ToolCapability.FileRead,
            ["FileRead"] = ToolCapability.FileRead,
            ["file_list"] = ToolCapability.FileRead,
            ["FileList"] = ToolCapability.FileRead,

            // System
            ["system_execute"] = ToolCapability.SystemExecute,
            ["SystemExecute"] = ToolCapability.SystemExecute,

            // Screen
            ["screen_capture"] = ToolCapability.ScreenCapture,
            ["ScreenCapture"] = ToolCapability.ScreenCapture,
            ["get_active_window"] = ToolCapability.ScreenCapture,
            ["GetActiveWindow"] = ToolCapability.ScreenCapture,

            // Meta/health
            ["tool_ping"] = ToolCapability.Meta,
            ["ToolPing"] = ToolCapability.Meta,
            ["tool_list_capabilities"] = ToolCapability.Meta,
            ["ToolListCapabilities"] = ToolCapability.Meta,

            // Time
            ["time_now"] = ToolCapability.TimeRead,
            ["TimeNow"] = ToolCapability.TimeRead
        };

    public static bool TryResolveCapability(string toolName, out ToolCapability capability)
        => CapabilityByToolName.TryGetValue(toolName, out capability);

    public static ToolCapability? ResolveCapability(string toolName)
        => CapabilityByToolName.TryGetValue(toolName, out var capability)
            ? capability
            : null;

    public static IReadOnlyDictionary<string, ToolCapability> GetMappings()
        => CapabilityByToolName;

    /// <summary>
    /// Resolves discovered tools to names whose mapped capability is allowed.
    /// Unmapped tools are excluded by default.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> ResolveTools(
        IReadOnlyList<ToolDefinition> allTools,
        IReadOnlyCollection<ToolCapability> allowedCapabilities,
        IReadOnlyCollection<ToolCapability>? forbiddenCapabilities = null)
    {
        if (allTools.Count == 0 || allowedCapabilities.Count == 0)
            return [];

        var allowed = new HashSet<ToolCapability>(allowedCapabilities);
        var forbidden = forbiddenCapabilities is null
            ? new HashSet<ToolCapability>()
            : new HashSet<ToolCapability>(forbiddenCapabilities);

        return allTools
            .Where(t => TryResolveCapability(t.Function.Name, out var capability)
                        && allowed.Contains(capability)
                        && !forbidden.Contains(capability))
            .ToList();
    }
}

