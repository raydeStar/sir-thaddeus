using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

/// <summary>
/// Read-only diagnostic tools for the MCP server: health check,
/// capability listing, version, and uptime tracking.
/// </summary>
[McpServerToolType]
public static class MetaTools
{
    /// <summary>Tracks server uptime from first tool invocation.</summary>
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    /// <summary>Hardcoded server version. Update on release.</summary>
    private const string ServerVersion = "0.3.0";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    [McpServerTool(
        Name = "tool_ping",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description(
        "Health check. Returns server version, uptime, status (\"ok\" " +
        "when HEALTHY), hostname, process ID, and count of registered " +
        "tools. When summarizing the result, use the word \"healthy\" " +
        "if status is \"ok\" — users read \"ok\" as terse and \"healthy\" " +
        "as reassuring.")]
    public static string ToolPing()
    {
        return JsonSerializer.Serialize(new
        {
            protocol_version = McpContract.ProtocolVersion,
            contract_version = McpContract.ServerContractVersion,
            version = ServerVersion,
            uptime_ms = Uptime.ElapsedMilliseconds,
            status = "ok",
            host = Environment.MachineName,
            pid = Environment.ProcessId,
            tool_count = ToolManifest.All.Count,
            manifest_hash = ToolManifest.ManifestHashSha256
        }, JsonOpts);
    }

    [McpServerTool(
        Name = "tool_list_capabilities",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description(
        "Returns the full tool manifest: every tool's name, aliases, " +
        "category, read/write classification, permission requirement, " +
        "and limits. Deterministic output.")]
    public static string ToolListCapabilities()
    {
        return ToolManifest.ToJson();
    }

    [McpServerTool(
        Name = "health.check",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Read-only control-plane health check for runtime diagnostics.")]
    public static string HealthCheck()
    {
        var settingsPath = ResolveSettingsPath();
        var auditPath = ResolveAuditPath();
        var memoryPath = Environment.GetEnvironmentVariable("ST_MEMORY_DB_PATH") ?? "";

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            protocol_version = McpContract.ProtocolVersion,
            contract_version = McpContract.ServerContractVersion,
            server_version = ServerVersion,
            uptime_ms = Uptime.ElapsedMilliseconds,
            tool_count = ToolManifest.All.Count,
            manifest_hash = ToolManifest.ManifestHashSha256,
            dependencies = new
            {
                settings_file = !string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath),
                audit_log = !string.IsNullOrWhiteSpace(auditPath) && File.Exists(auditPath),
                memory_store = !string.IsNullOrWhiteSpace(memoryPath) && File.Exists(memoryPath)
            }
        }, JsonOpts);
    }

    [McpServerTool(
        Name = "capabilities.describe",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Deterministic tool metadata with risk tiers and preview/apply support.")]
    public static string CapabilitiesDescribe()
    {
        var names = new HashSet<string>(
            ToolManifest.All.Select(t => t.Name),
            StringComparer.OrdinalIgnoreCase);

        var tools = ToolManifest.All
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new
            {
                name = t.Name,
                aliases = t.Aliases,
                category = t.Category,
                read_write = t.ReadWrite,
                permission = t.Permission,
                risk_tier = ToolManifest.ResolveRiskTier(t.Name),
                supports_preview_apply =
                    names.Contains($"{t.Name}_preview") ||
                    names.Contains($"{t.Name}_apply") ||
                    t.Name.EndsWith("_preview", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.EndsWith("_apply", StringComparison.OrdinalIgnoreCase),
                redaction_rule = ResolveRedactionRule(t.Name, t.Category),
                default_limits = t.Limits
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            generated_at_utc = DateTimeOffset.UtcNow.ToString("O"),
            manifest_hash = ToolManifest.ManifestHashSha256,
            tools
        }, JsonOpts);
    }

    [McpServerTool(
        Name = "policy.get_state",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Read-only runtime policy surface: panic/safe mode, budgets, and tool groups.")]
    public static string PolicyGetState()
    {
        try
        {
            var settingsPath = ResolveSettingsPath();
            if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "settings_file_not_found"
                }, JsonOpts);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = doc.RootElement;
            var runtime = root.TryGetProperty("runtimeSafety", out var runtimeEl)
                ? runtimeEl
                : default;
            var budgets = root.TryGetProperty("toolBudgets", out var legacyBudgetEl)
                ? legacyBudgetEl
                : root.TryGetProperty("limits", out var currentLimitsEl)
                    ? currentLimitsEl
                    : default;
            var perms = root.TryGetProperty("mcp", out var mcpEl) &&
                        mcpEl.TryGetProperty("permissions", out var legacyPermEl)
                ? legacyPermEl
                : root.TryGetProperty("permissions", out var currentPermEl)
                    ? currentPermEl
                    : default;

            return JsonSerializer.Serialize(new
            {
                ok = true,
                panic_mode = TryGetBool(runtime, "panicMode"),
                safe_mode = TryGetBool(runtime, "safeMode"),
                safe_mode_reason = TryGetString(runtime, "safeModeReason"),
                budgets = new
                {
                    enabled = TryGetBool(budgets, "enabled", fallback: true),
                    max_tool_calls_per_turn = TryGetInt(budgets, "maxToolCallsPerTurn"),
                    max_tool_calls_per_session = TryGetInt(budgets, "maxToolCallsPerSession"),
                    max_web_pulls_per_turn = TryGetInt(budgets, "maxWebPullsPerTurn"),
                    max_file_ops_per_minute = TryGetInt(budgets, "maxFileOpsPerMinute")
                },
                enabled_tool_groups = new
                {
                    screen = TryGetString(perms, "screen"),
                    files = TryGetString(perms, "files"),
                    system = TryGetString(perms, "system"),
                    web = TryGetString(perms, "web"),
                    memory_read = TryGetString(perms, "memoryRead"),
                    memory_write = TryGetString(perms, "memoryWrite")
                }
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"policy_state_read_failed: {ex.Message}"
            }, JsonOpts);
        }
    }

    [McpServerTool(
        Name = "policy.set_panic_mode",
        ReadOnly = false,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Sets persistent panic mode in settings. Requires explicit confirm=true.")]
    public static string PolicySetPanicMode(
        [Description("True to enable panic mode; false to disable.")]
        bool enabled,
        [Description("Explicit confirmation gate. Must be true to apply.")]
        bool confirm = false,
        [Description("Operator reason for this control-plane action.")]
        string reason = "")
    {
        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "confirm_required",
                message = "Set confirm=true to apply policy.set_panic_mode."
            }, JsonOpts);
        }

        var settingsPath = ResolveSettingsPath();
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "settings_path_not_available"
            }, JsonOpts);
        }

        try
        {
            JsonObject root;
            if (File.Exists(settingsPath))
            {
                root = (JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var runtime = root["runtimeSafety"] as JsonObject ?? new JsonObject();
            runtime["panicMode"] = enabled;
            runtime["safeModeReason"] = (reason ?? "").Trim();
            runtime["panicModeUpdatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
            root["runtimeSafety"] = runtime;

            var saveJson = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(settingsPath, saveJson);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                panic_mode = enabled,
                settings_path = settingsPath
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"panic_mode_update_failed: {ex.Message}"
            }, JsonOpts);
        }
    }

    [McpServerTool(
        Name = "audit.export_bundle",
        ReadOnly = false,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Exports a redacted diagnostics bundle. Requires confirm=true.")]
    public static string AuditExportBundle(
        [Description("Explicit confirmation gate. Must be true to export.")]
        bool confirm = false,
        [Description("Optional operator note for support context.")]
        string reason = "")
    {
        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "confirm_required",
                message = "Set confirm=true to export diagnostics."
            }, JsonOpts);
        }

        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SirThaddeus",
                "diagnostics");
            Directory.CreateDirectory(baseDir);

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var bundleDir = Path.Combine(baseDir, $"bundle-{stamp}");
            Directory.CreateDirectory(bundleDir);

            var auditPath = ResolveAuditPath();
            if (!string.IsNullOrWhiteSpace(auditPath) && File.Exists(auditPath))
            {
                var redactedAudit = RedactSensitiveText(File.ReadAllText(auditPath));
                File.WriteAllText(Path.Combine(bundleDir, "audit-redacted.jsonl"), redactedAudit);
            }

            var settingsPath = ResolveSettingsPath();
            if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
            {
                var redactedSettings = RedactSensitiveText(File.ReadAllText(settingsPath));
                File.WriteAllText(Path.Combine(bundleDir, "settings-redacted.json"), redactedSettings);
            }

            var chatPath = Environment.GetEnvironmentVariable("ST_CHAT_HISTORY_PATH");
            if (!string.IsNullOrWhiteSpace(chatPath) && File.Exists(chatPath))
            {
                var redactedChat = RedactSensitiveText(File.ReadAllText(chatPath));
                File.WriteAllText(Path.Combine(bundleDir, "chat-history-redacted.json"), redactedChat);
            }

            var briefingPath = Environment.GetEnvironmentVariable("ST_BRIEFING_HISTORY_PATH");
            if (!string.IsNullOrWhiteSpace(briefingPath) && File.Exists(briefingPath))
            {
                var redactedBriefing = RedactSensitiveText(File.ReadAllText(briefingPath));
                File.WriteAllText(Path.Combine(bundleDir, "briefing-history-redacted.json"), redactedBriefing);
            }

            File.WriteAllText(Path.Combine(bundleDir, "tool-manifest.json"), ToolManifest.ToJson());
            File.WriteAllText(Path.Combine(bundleDir, "metadata.json"), JsonSerializer.Serialize(new
            {
                generated_at_utc = DateTimeOffset.UtcNow.ToString("O"),
                operator_reason = (reason ?? "").Trim(),
                server_version = ServerVersion,
                protocol_version = McpContract.ProtocolVersion,
                contract_version = McpContract.ServerContractVersion,
                manifest_hash = ToolManifest.ManifestHashSha256
            }, new JsonSerializerOptions { WriteIndented = true }));

            var zipPath = bundleDir + ".zip";
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(bundleDir, zipPath);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                bundle_zip = zipPath
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"export_bundle_failed: {ex.Message}"
            }, JsonOpts);
        }
    }

    private static string ResolveSettingsPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ST_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath.Trim();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "settings.json");
    }

    private static string ResolveAuditPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ST_AUDIT_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath.Trim();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "audit.jsonl");
    }

    private static string ResolveRedactionRule(string name, string category)
    {
        if (name.Contains("screen", StringComparison.OrdinalIgnoreCase))
            return "hash_screen_text";
        if (name.Contains("file", StringComparison.OrdinalIgnoreCase))
            return "hash_file_content";
        if (name.Contains("system", StringComparison.OrdinalIgnoreCase))
            return "hash_command_payload";
        if (category.Equals("web", StringComparison.OrdinalIgnoreCase))
            return "truncate_and_scrub";
        return "pass_through";
    }

    private static string RedactSensitiveText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var text = raw;
        text = Regex.Replace(
            text,
            "(?i)(api[_-]?key|token|password|secret)\"?\\s*[:=]\\s*\"?[^\",\\r\\n]+\"?",
            "$1:\"[REDACTED]\"");
        text = Regex.Replace(
            text,
            "(?i)bearer\\s+[a-z0-9\\-\\._~\\+\\/]+=*",
            "bearer [REDACTED]");
        return text;
    }

    private static bool TryGetBool(JsonElement element, string name, bool fallback = false)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    private static int TryGetInt(JsonElement element, string name, int fallback = 0)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static string TryGetString(JsonElement element, string name, string fallback = "")
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
}
