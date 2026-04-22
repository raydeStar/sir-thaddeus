namespace Thaddeus.Runtime.Tools;

/// <summary>
/// Policy groups that MCP tools are classified into. The per-group policy
/// in <c>PermissionsSettings</c> decides how calls in each group behave.
/// <see cref="Safe"/> covers tools that are purely local read-only with no
/// side effects (time, timezone, meta tool list, etc.) — those never prompt.
/// </summary>
public enum ToolGroup
{
    /// <summary>Pure local read-only: time, timezone, meta. Never prompts.</summary>
    Safe,
    /// <summary>Screen capture / active-window reads.</summary>
    Screen,
    /// <summary>File system reads and listings.</summary>
    Files,
    /// <summary>System execution: shell, clipboard, OS commands.</summary>
    System,
    /// <summary>Anything that reaches the network: search, browser, weather, places, holidays, feeds.</summary>
    Web,
    /// <summary>Memory retrieval / listing.</summary>
    MemoryRead,
    /// <summary>Memory mutation: store, update, delete.</summary>
    MemoryWrite,
}

/// <summary>
/// Classifies a tool name into its <see cref="ToolGroup"/>. Matching is done
/// on common MCP name prefixes produced by the SirThaddeus.McpServer tools.
/// Unknown tools fall into <see cref="ToolGroup.System"/> — conservative by
/// default so new tools don't silently bypass the policy.
/// </summary>
public static class ToolGroupClassifier
{
    public static ToolGroup Classify(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return ToolGroup.System;
        var n = toolName.Trim().ToLowerInvariant();

        // Safe group: pure local reads with no side effects.
        if (n.StartsWith("time_") || n.StartsWith("timezone_") ||
            n.StartsWith("meta_") || n == "tools_list" || n == "ping" ||
            n.StartsWith("propose_"))
            return ToolGroup.Safe;

        if (n.StartsWith("screen_") || n.StartsWith("get_active_window"))
            return ToolGroup.Screen;

        if (n.StartsWith("file_") || n.StartsWith("knowledge_") ||
            n.StartsWith("doc_") || n.StartsWith("document_"))
            return ToolGroup.Files;

        if (n.StartsWith("system_") || n.StartsWith("clipboard_") ||
            n.StartsWith("process_") || n.StartsWith("shell_"))
            return ToolGroup.System;

        if (n.StartsWith("web_") || n.StartsWith("browser_") ||
            n.StartsWith("weather_") || n.StartsWith("holidays_") ||
            n.StartsWith("holiday_") || n.StartsWith("places_") ||
            n.StartsWith("feed_") || n.StartsWith("status_") ||
            n.StartsWith("fetch_") || n.StartsWith("resolve_timezone"))
            return ToolGroup.Web;

        if (n.StartsWith("memory_retrieve") || n.StartsWith("memory_list") ||
            n.StartsWith("memory_get") || n.StartsWith("memory_search"))
            return ToolGroup.MemoryRead;

        if (n.StartsWith("memory_store") || n.StartsWith("memory_update") ||
            n.StartsWith("memory_delete") || n.StartsWith("memory_remove") ||
            n.StartsWith("memory_forget"))
            return ToolGroup.MemoryWrite;

        // Unknown → conservative default.
        return ToolGroup.System;
    }
}
