using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent;

// ─────────────────────────────────────────────────────────────────────────
// Tool Call Redactor
//
// Produces bounded, redacted summaries of tool inputs and outputs for
// the audit log. The audit log must NEVER contain:
//   - Raw OCR text from screen captures
//   - Full file contents
//   - Full web page content or article excerpts
//   - Credentials, tokens, or secrets
//
// Summaries are always bounded to prevent log bloat and accidental
// sensitive data capture. Each tool type gets a purpose-built redactor.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Produces bounded, redacted summaries of tool call inputs and outputs
/// for the audit log. Never leaks sensitive data.
/// </summary>
public static class ToolCallRedactor
{
    private const int DefaultMaxChars = 200;

    /// <summary>
    /// Produces a redacted summary of tool input arguments.
    /// </summary>
    public static string RedactInput(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "(empty)";

        var lower = (toolName ?? "").ToLowerInvariant();
        var summary = lower switch
        {
            // Screen capture: just log the target mode, nothing sensitive
            "screencapture" or "screen_capture"
                => Truncate(argumentsJson, 100),

            // File read: log the path only, not any content
            "fileread" or "file_read"
                => Truncate(argumentsJson, 200),

            // File list: log the path
            "filelist" or "file_list"
                => Truncate(argumentsJson, 200),

            // System execute: never log full command text.
            // Keep only executable + arg count + hash.
            "systemexecute" or "system_execute"
                => SummarizeSystemExecuteInput(argumentsJson),

            // Web search: log query + recency (safe)
            "websearch" or "web_search"
                => Truncate(argumentsJson, 200),

            // Browser navigate: log the URL
            "browsernavigate" or "browser_navigate"
                => Truncate(argumentsJson, 300),

            // Weather tools: safe, structured args (place / coords)
            "weathergeocode" or "weather_geocode"
                => Truncate(argumentsJson, 200),
            "weatherforecast" or "weather_forecast"
                => Truncate(argumentsJson, 220),
            "resolvetimezone" or "resolve_timezone"
                => Truncate(argumentsJson, 180),
            "holidaysget" or "holidays_get"
                => Truncate(argumentsJson, 220),
            "holidaysnext" or "holidays_next"
                => Truncate(argumentsJson, 180),
            "holidaysistoday" or "holidays_is_today"
                => Truncate(argumentsJson, 180),
            "feedfetch" or "feed_fetch"
                => Truncate(argumentsJson, 260),
            "statuscheckurl" or "status_check_url"
                => Truncate(argumentsJson, 220),

            // Memory tools: safe to log (subject/predicate only)
            _ when lower.StartsWith("memory")
                => Truncate(argumentsJson, 200),

            // Default: truncate to a safe length
            _ => Truncate(argumentsJson, DefaultMaxChars)
        };

        // Generic defense-in-depth scrub pass for all tool inputs.
        return AuditSensitiveDataScrubber.ScrubText(summary);
    }

    /// <summary>
    /// Produces a redacted summary of tool output for the audit log.
    /// Sensitive content is replaced with size + hash metadata.
    /// </summary>
    public static string RedactOutput(string toolName, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "(empty)";

        var lower = (toolName ?? "").ToLowerInvariant();
        return lower switch
        {
            // Screen capture: NEVER log OCR text; just char count + hash
            "screencapture" or "screen_capture"
                => $"[OCR output: {output.Length} chars, sha256={ShortHash(output)}]",

            // File read: NEVER log file content; just size + hash
            "fileread" or "file_read"
                => $"[File content: {output.Length} chars, sha256={ShortHash(output)}]",

            // Browser navigate: log title + content length, not full body
            "browsernavigate" or "browser_navigate"
                => SummarizeBrowserOutput(output),

            // Web search: log result count + titles, not full excerpts
            "websearch" or "web_search"
                => SummarizeSearchOutput(output),

            // Weather geocode: log candidate count
            "weathergeocode" or "weather_geocode"
                => SummarizeWeatherGeocodeOutput(output),

            // Weather forecast: log provider + short current snapshot
            "weatherforecast" or "weather_forecast"
                => SummarizeWeatherForecastOutput(output),
            "resolvetimezone" or "resolve_timezone"
                => SummarizeTimezoneOutput(output),
            "holidaysget" or "holidays_get"
                => SummarizeHolidaysOutput(output),
            "holidaysnext" or "holidays_next"
                => SummarizeHolidaysOutput(output),
            "holidaysistoday" or "holidays_is_today"
                => SummarizeHolidayTodayOutput(output),
            "feedfetch" or "feed_fetch"
                => SummarizeFeedOutput(output),
            "statuscheckurl" or "status_check_url"
                => SummarizeStatusOutput(output),

            // Memory tools: safe to log (structured JSON, no secrets)
            _ when lower.StartsWith("memory")
                => Truncate(output, 300),

            // Default: truncate to safe length
            _ => Truncate(output, DefaultMaxChars)
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string SummarizeBrowserOutput(string output)
    {
        // Extract title if present, plus content length
        var titleLine = output.Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("Title:", StringComparison.OrdinalIgnoreCase));

        var title = titleLine != null
            ? titleLine.Trim()
            : "(no title)";

        return $"[Browser: {Truncate(title, 100)}, {output.Length} chars total]";
    }

    private static string SummarizeSearchOutput(string output)
    {
        // Count numbered result lines (format: "1. \"Title\"")
        var resultCount = output.Split('\n')
            .Count(l =>
            {
                var trimmed = l.Trim();
                return trimmed.Length > 3 &&
                       char.IsDigit(trimmed[0]) &&
                       (trimmed[1] == '.' || (char.IsDigit(trimmed[1]) && trimmed[2] == '.'));
            });

        return $"[Search: {resultCount} results, {output.Length} chars total]";
    }

    private static string SummarizeWeatherGeocodeOutput(string output)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var root = doc.RootElement;
            var count = root.TryGetProperty("results", out var r) &&
                        r.ValueKind == System.Text.Json.JsonValueKind.Array
                ? r.GetArrayLength()
                : 0;
            var source = root.TryGetProperty("source", out var s) ? (s.GetString() ?? "unknown") : "unknown";
            return $"[Weather geocode: {count} result(s), source={source}]";
        }
        catch
        {
            return $"[Weather geocode: {output.Length} chars]";
        }
    }

    private static string SummarizeWeatherForecastOutput(string output)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return $"[Weather forecast error: {Truncate(err.GetString() ?? "", 120)}]";
            }

            var provider = root.TryGetProperty("provider", out var p) ? (p.GetString() ?? "unknown") : "unknown";

            string current = "n/a";
            if (root.TryGetProperty("current", out var c) &&
                c.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var temp = c.TryGetProperty("temperature", out var t) ? t.ToString() : "?";
                var unit = c.TryGetProperty("unit", out var u) ? (u.GetString() ?? "") : "";
                var cond = c.TryGetProperty("condition", out var condEl) ? (condEl.GetString() ?? "") : "";
                current = $"{temp}{unit} {cond}".Trim();
            }

            return $"[Weather forecast: provider={provider}, current={Truncate(current, 80)}]";
        }
        catch
        {
            return $"[Weather forecast: {output.Length} chars]";
        }
    }

    private static string SummarizeTimezoneOutput(string output)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                return $"[Timezone lookup error: {Truncate(err.ToString(), 120)}]";

            var tz = root.TryGetProperty("timezone", out var tzEl) ? (tzEl.GetString() ?? "") : "";
            var source = root.TryGetProperty("source", out var srcEl) ? (srcEl.GetString() ?? "unknown") : "unknown";
            return $"[Timezone lookup: timezone={Truncate(tz, 48)}, source={source}]";
        }
        catch
        {
            return $"[Timezone lookup: {output.Length} chars]";
        }
    }

    private static string SummarizeHolidaysOutput(string output)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                return $"[Holiday lookup error: {Truncate(err.ToString(), 120)}]";

            var country = root.TryGetProperty("countryCode", out var ccEl) ? (ccEl.GetString() ?? "") : "";
            var count = root.TryGetProperty("holidays", out var arr) &&
                        arr.ValueKind == System.Text.Json.JsonValueKind.Array
                ? arr.GetArrayLength()
                : 0;
            return $"[Holiday lookup: country={country}, holidays={count}]";
        }
        catch
        {
            return $"[Holiday lookup: {output.Length} chars]";
        }
    }

    private static string SummarizeHolidayTodayOutput(string output)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                return $"[Holiday-today lookup error: {Truncate(err.ToString(), 120)}]";

            var country = root.TryGetProperty("countryCode", out var ccEl) ? (ccEl.GetString() ?? "") : "";
            var isHoliday = root.TryGetProperty("isPublicHoliday", out var hEl) &&
                            hEl.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? hEl.GetBoolean()
                : false;
            return $"[Holiday today: country={country}, isHoliday={isHoliday}]";
        }
        catch
        {
            return $"[Holiday today: {output.Length} chars]";
        }
    }

    private static string SummarizeFeedOutput(string output)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                return $"[Feed fetch error: {Truncate(err.ToString(), 120)}]";

            var title = root.TryGetProperty("feedTitle", out var tEl) ? (tEl.GetString() ?? "") : "";
            var host = root.TryGetProperty("sourceHost", out var hEl) ? (hEl.GetString() ?? "") : "";
            var count = root.TryGetProperty("items", out var items) &&
                        items.ValueKind == System.Text.Json.JsonValueKind.Array
                ? items.GetArrayLength()
                : 0;
            return $"[Feed fetch: host={host}, title={Truncate(title, 60)}, items={count}]";
        }
        catch
        {
            return $"[Feed fetch: {output.Length} chars]";
        }
    }

    private static string SummarizeStatusOutput(string output)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                return $"[Status check error: {Truncate(err.ToString(), 120)}]";

            var reachable = root.TryGetProperty("reachable", out var rEl) &&
                            rEl.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? rEl.GetBoolean()
                : false;
            var code = root.TryGetProperty("httpStatus", out var sEl) ? sEl.ToString() : "?";
            var latency = root.TryGetProperty("latencyMs", out var lEl) ? lEl.ToString() : "?";
            return $"[Status check: reachable={reachable}, status={code}, latencyMs={latency}]";
        }
        catch
        {
            return $"[Status check: {output.Length} chars]";
        }
    }

    private static string SummarizeSystemExecuteInput(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return $"[system_execute: unparsed_args_sha256={ShortHash(argumentsJson)}]";

            var command = GetStringProperty(doc.RootElement, "command");
            if (string.IsNullOrWhiteSpace(command))
                return "[system_execute: command=missing]";

            var tokens = SplitCommandTokens(command);
            var executableToken = tokens.Count > 0 ? tokens[0] : command;
            var executableName = System.IO.Path.GetFileName(executableToken.Trim('"'));
            if (string.IsNullOrWhiteSpace(executableName))
                executableName = "unknown";

            var argsCount = Math.Max(0, tokens.Count - 1);
            return $"[system_execute: executable={executableName}, args_count={argsCount}, command_sha256={ShortHash(command)}]";
        }
        catch
        {
            return $"[system_execute: unparsed_args_sha256={ShortHash(argumentsJson)}]";
        }
    }

    private static string GetStringProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return "";
        return node.GetString() ?? "";
    }

    private static List<string> SplitCommandTokens(string command)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(command))
            return tokens;

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in command)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "\u2026";
    }

    /// <summary>
    /// Produces a short (first 12 hex chars) SHA-256 hash for audit
    /// identification without storing the actual content.
    /// </summary>
    private static string ShortHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
