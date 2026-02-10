using System.Security.Cryptography;
using System.Text;

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
internal static class ToolCallRedactor
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
        return lower switch
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

            // System execute: log the command (bounded)
            "systemexecute" or "system_execute"
                => Truncate(argumentsJson, 200),

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

            // Memory tools: safe to log (subject/predicate only)
            _ when lower.StartsWith("memory")
                => Truncate(argumentsJson, 200),

            // Default: truncate to a safe length
            _ => Truncate(argumentsJson, DefaultMaxChars)
        };
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
