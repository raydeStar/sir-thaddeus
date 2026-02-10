using System.ComponentModel;
using ModelContextProtocol.Server;
using SirThaddeus.McpShared;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Time Tool
//
// Returns the current local time in a structured format useful for the
// agent's temporal reasoning. No permission required, no side effects.
//
// Payload:
//   iso      — ISO 8601 local time with offset (e.g. "2026-02-09T10:30:00-05:00")
//   unix_ms  — Unix timestamp in milliseconds
//   timezone — Windows timezone ID (e.g. "Eastern Standard Time")
//   offset   — UTC offset (e.g. "-05:00")
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class TimeTools
{
    [McpServerTool, Description(
        "Returns the current local time as ISO 8601, Unix milliseconds, " +
        "Windows timezone ID, and UTC offset. No parameters required.")]
    public static string TimeNow()
    {
        var now = DateTimeOffset.Now;
        var tz  = TimeZoneInfo.Local.Id;
        return TimeHelper.BuildTimePayload(now, tz);
    }
}
