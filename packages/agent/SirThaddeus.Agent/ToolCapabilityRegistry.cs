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
            ["places_discover"] = ToolCapability.WebSearch,
            ["PlacesDiscover"] = ToolCapability.WebSearch,
            ["places_lookup"] = ToolCapability.WebSearch,
            ["PlacesLookup"] = ToolCapability.WebSearch,
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
            ["file_read_preview"] = ToolCapability.FileRead,
            ["FileReadPreview"] = ToolCapability.FileRead,
            ["file_read_apply"] = ToolCapability.FileRead,
            ["FileReadApply"] = ToolCapability.FileRead,
            ["file_list_preview"] = ToolCapability.FileRead,
            ["FileListPreview"] = ToolCapability.FileRead,
            ["file_list_apply"] = ToolCapability.FileRead,
            ["FileListApply"] = ToolCapability.FileRead,
            ["document_read"] = ToolCapability.FileRead,
            ["DocumentRead"] = ToolCapability.FileRead,
            ["wiki_roots_list"] = ToolCapability.WikiRead,
            ["WikiRootsList"] = ToolCapability.WikiRead,
            ["wiki_tree_get"] = ToolCapability.WikiRead,
            ["WikiTreeGet"] = ToolCapability.WikiRead,
            ["wiki_page_read"] = ToolCapability.WikiRead,
            ["WikiPageRead"] = ToolCapability.WikiRead,
            ["wiki_search"] = ToolCapability.WikiRead,
            ["WikiSearch"] = ToolCapability.WikiRead,
            ["wiki_root_create"] = ToolCapability.WikiWrite,
            ["WikiRootCreate"] = ToolCapability.WikiWrite,
            ["wiki_root_rename"] = ToolCapability.WikiWrite,
            ["WikiRootRename"] = ToolCapability.WikiWrite,
            ["wiki_root_remove"] = ToolCapability.WikiWrite,
            ["WikiRootRemove"] = ToolCapability.WikiWrite,
            ["wiki_folder_create"] = ToolCapability.WikiWrite,
            ["WikiFolderCreate"] = ToolCapability.WikiWrite,
            ["wiki_folder_rename"] = ToolCapability.WikiWrite,
            ["WikiFolderRename"] = ToolCapability.WikiWrite,
            ["wiki_folder_move"] = ToolCapability.WikiWrite,
            ["WikiFolderMove"] = ToolCapability.WikiWrite,
            ["wiki_folder_delete"] = ToolCapability.WikiWrite,
            ["WikiFolderDelete"] = ToolCapability.WikiWrite,
            ["wiki_page_create"] = ToolCapability.WikiWrite,
            ["WikiPageCreate"] = ToolCapability.WikiWrite,
            ["wiki_page_update"] = ToolCapability.WikiWrite,
            ["WikiPageUpdate"] = ToolCapability.WikiWrite,
            ["wiki_page_rename"] = ToolCapability.WikiWrite,
            ["WikiPageRename"] = ToolCapability.WikiWrite,
            ["wiki_page_move"] = ToolCapability.WikiWrite,
            ["WikiPageMove"] = ToolCapability.WikiWrite,
            ["wiki_page_delete"] = ToolCapability.WikiWrite,
            ["WikiPageDelete"] = ToolCapability.WikiWrite,
            ["wiki_page_patch_selection"] = ToolCapability.WikiWrite,
            ["WikiPagePatchSelection"] = ToolCapability.WikiWrite,
            ["wiki_page_revisions_list"] = ToolCapability.WikiRead,
            ["WikiPageRevisionsList"] = ToolCapability.WikiRead,
            ["wiki_page_revision_restore"] = ToolCapability.WikiWrite,
            ["WikiPageRevisionRestore"] = ToolCapability.WikiWrite,

            // System
            // Clipboard is treated as a system capability for routing.
            // Per-call approval still comes from ToolGroupPolicy.
            ["clipboard_read"] = ToolCapability.SystemExecute,
            ["ClipboardRead"] = ToolCapability.SystemExecute,
            ["clipboard_write"] = ToolCapability.SystemExecute,
            ["ClipboardWrite"] = ToolCapability.SystemExecute,
            ["system_execute"] = ToolCapability.SystemExecute,
            ["SystemExecute"] = ToolCapability.SystemExecute,
            ["system_execute_preview"] = ToolCapability.SystemExecute,
            ["SystemExecutePreview"] = ToolCapability.SystemExecute,
            ["system_execute_apply"] = ToolCapability.SystemExecute,
            ["SystemExecuteApply"] = ToolCapability.SystemExecute,

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
            ["health.check"] = ToolCapability.Meta,
            ["HealthCheck"] = ToolCapability.Meta,
            ["health_check"] = ToolCapability.Meta,
            ["capabilities.describe"] = ToolCapability.Meta,
            ["CapabilitiesDescribe"] = ToolCapability.Meta,
            ["capabilities_describe"] = ToolCapability.Meta,
            ["policy.get_state"] = ToolCapability.Meta,
            ["PolicyGetState"] = ToolCapability.Meta,
            ["policy_get_state"] = ToolCapability.Meta,
            ["audit.export_bundle"] = ToolCapability.FileWrite,
            ["AuditExportBundle"] = ToolCapability.FileWrite,
            ["audit_export_bundle"] = ToolCapability.FileWrite,
            ["policy.set_panic_mode"] = ToolCapability.SystemExecute,
            ["PolicySetPanicMode"] = ToolCapability.SystemExecute,
            ["policy_set_panic_mode"] = ToolCapability.SystemExecute,

            // Time
            ["time_now"] = ToolCapability.TimeRead,
            ["TimeNow"] = ToolCapability.TimeRead,

            // Math — pure deterministic computation, no I/O or side effects, so
            // it shares the always-available meta capability class.
            ["calculator"] = ToolCapability.Meta,
            ["Calculator"] = ToolCapability.Meta,

            // Sandboxed compute — network-isolated, resource-capped container;
            // pure computation like the calculator.
            ["python_eval"] = ToolCapability.Meta,
            ["PythonEval"] = ToolCapability.Meta
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

