using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using SirThaddeus.McpShared;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Meta / Health Tools
//
// Lightweight, read-only diagnostic tools for the MCP server itself.
// No permission required, no side effects, bounded output.
//
//   tool_ping             — health check (version, uptime, status, pid)
//   tool_list_capabilities — full manifest of all tools
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class MetaTools
{
    /// <summary>Tracks server uptime from first tool invocation.</summary>
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    /// <summary>Hardcoded server version. Update on release.</summary>
    private const string ServerVersion = "0.2.0";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    [McpServerTool, Description(
        "Health check. Returns server version, uptime, status, " +
        "hostname, process ID, and count of registered tools.")]
    public static string ToolPing()
    {
        return JsonSerializer.Serialize(new
        {
            version    = ServerVersion,
            uptime_ms  = Uptime.ElapsedMilliseconds,
            status     = "ok",
            host       = Environment.MachineName,
            pid        = Environment.ProcessId,
            tool_count = ToolManifest.All.Count
        }, JsonOpts);
    }

    [McpServerTool, Description(
        "Returns the full tool manifest: every tool's name, aliases, " +
        "category, read/write classification, permission requirement, " +
        "and limits. Deterministic output.")]
    public static string ToolListCapabilities()
    {
        return ToolManifest.ToJson();
    }
}
