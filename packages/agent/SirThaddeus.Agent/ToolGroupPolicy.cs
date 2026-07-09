using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Config;

namespace SirThaddeus.Agent;

// ─────────────────────────────────────────────────────────────────────────
// Tool Group Policy — Deterministic Permission Resolution
//
// Pure logic for mapping tools to groups, resolving effective policies,
// building redacted purpose strings, and managing session grants.
//
// Extracted from WpfPermissionGate so it can be tested without WPF.
// The runtime gate delegates to this class for all deterministic decisions.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Immutable snapshot of the current permission policies.
/// Built from <see cref="AppSettings"/> and swapped atomically.
/// </summary>
public sealed record PolicySnapshot
{
    public required IReadOnlyDictionary<string, string> GroupPolicies  { get; init; }
    public required string DeveloperOverride  { get; init; }
    public required bool   MemoryEnabled      { get; init; }
    public required bool   PanicModeEnabled   { get; init; }
    public required bool   SafeModeEnabled    { get; init; }
    public required string UnknownToolDefault  { get; init; }

    /// <summary>
    /// Optional per-tool overrides keyed by canonical (snake_case) tool name.
    /// Values are normalized to "off" / "ask" / "always"; a tool absent from
    /// the map inherits its group's effective policy. Null when unset.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ToolOverrides { get; init; }
}

/// <summary>
/// Static helpers for deterministic tool-group resolution and
/// effective policy computation. No I/O, no prompts, no state.
/// </summary>
public static class ToolGroupPolicy
{
    // ─────────────────────────────────────────────────────────────────
    // Tool → Group Mapping
    // ─────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> ToolGroupMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Screen
        ["screen_capture"]       = "screen",
        ["get_active_window"]    = "screen",

        // Files
        ["file_read"]            = "files",
        ["document_read"]        = "files",
        ["file_list"]            = "files",
        ["file_read_preview"]    = "files",
        ["file_read_apply"]      = "files",
        ["file_list_preview"]    = "files",
        ["file_list_apply"]      = "files",
        ["wiki_roots_list"] = "files",
        ["wiki_tree_get"] = "files",
        ["wiki_page_read"] = "files",
        ["wiki_search"] = "files",
        ["wiki_root_create"] = "files",
        ["wiki_root_rename"] = "files",
        ["wiki_root_remove"] = "files",
        ["wiki_folder_create"] = "files",
        ["wiki_folder_rename"] = "files",
        ["wiki_folder_move"] = "files",
        ["wiki_folder_delete"] = "files",
        ["wiki_page_create"] = "files",
        ["wiki_page_update"] = "files",
        ["wiki_page_rename"] = "files",
        ["wiki_page_move"] = "files",
        ["wiki_page_delete"] = "files",
        ["wiki_page_patch_selection"] = "files",
        ["wiki_page_revisions_list"] = "files",
        ["wiki_page_revision_restore"] = "files",

        // System
        ["system_execute"]       = "system",
        ["system_execute_preview"] = "system",
        ["system_execute_apply"] = "system",
        ["clipboard_read"]       = "sensitiveRead",
        ["clipboard_write"]      = "system",

        // Web
        ["web_search"]           = "web",
        ["browser_navigate"]     = "web",
        ["places_discover"]      = "web",
        ["places_lookup"]        = "web",
        ["weather_geocode"]      = "web",
        ["weather_forecast"]     = "web",
        ["resolve_timezone"]     = "web",
        ["holidays_get"]         = "web",
        ["holidays_next"]        = "web",
        ["holidays_is_today"]    = "web",
        ["feed_fetch"]           = "web",
        ["status_check_url"]     = "web",

        // Memory Read
        ["memory_retrieve"]      = "memoryRead",
        ["memory_list_facts"]    = "memoryRead",

        // Memory Write
        ["memory_store_facts"]   = "memoryWrite",
        ["memory_update_fact"]   = "memoryWrite",
        ["memory_delete_fact"]   = "memoryWrite",

        // Meta / Time — always allowed
        ["tool_ping"]              = "meta",
        ["tool_list_capabilities"] = "meta",
        ["health.check"]           = "meta",
        ["health_check"]           = "meta",
        ["capabilities.describe"]  = "meta",
        ["capabilities_describe"]  = "meta",
        ["policy.get_state"]       = "meta",
        ["policy_get_state"]       = "meta",
        ["time_now"]               = "meta",

        // Math — pure deterministic computation, no I/O or side effects, so
        // it shares time_now's always-allowed class rather than needing its
        // own permission group.
        ["calculator"]             = "meta",

        // Sandboxed compute — Docker container with no network, no host
        // mounts, read-only rootfs, and hard resource caps, so like the
        // calculator it is pure computation with no reachable side effects.
        ["python_eval"]            = "meta",

        // Control-plane side effects
        ["audit.export_bundle"]    = "files",
        ["audit_export_bundle"]    = "files",
        ["policy.set_panic_mode"]  = "system",
        ["policy_set_panic_mode"]  = "system",
    };

    /// <summary>
    /// Groups subject to the developer override.
    /// Covers all tool groups except meta (which is always allowed).
    /// </summary>
    public static readonly HashSet<string> OverridableGroups =
        new(StringComparer.OrdinalIgnoreCase)
        { "screen", "files", "system", "web", "memoryRead", "memoryWrite" };

    /// <summary>
    /// Groups that always require per-call approval and cannot be persisted
    /// as session/always grants.
    /// </summary>
    public static readonly HashSet<string> PerCallOnlyGroups =
        new(StringComparer.OrdinalIgnoreCase)
        { "sensitiveRead" };

    /// <summary>
    /// Groups that should be blocked in panic mode.
    /// </summary>
    public static readonly HashSet<string> SideEffectGroups =
        new(StringComparer.OrdinalIgnoreCase)
        { "system", "web", "memoryWrite", "sensitiveRead" };

    // ─────────────────────────────────────────────────────────────────
    // Group Resolution
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a canonical (snake_case) tool name to its group key.
    /// Unknown tools return "unknown".
    /// </summary>
    public static string ResolveGroup(string canonicalToolName)
    {
        return ToolGroupMap.TryGetValue(canonicalToolName, out var group)
            ? group
            : "unknown";
    }

    // ─────────────────────────────────────────────────────────────────
    // Effective Policy Resolution
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the effective policy for a group given the current snapshot.
    /// Applies developer override for dangerous groups, memory master off
    /// for memory groups, and the unknown-tool default.
    ///
    /// <para>The optional <paramref name="perToolOverride"/> is the
    /// already-normalized per-tool override ("off" / "ask" / "always") for
    /// the specific tool being resolved, or null to inherit the group. It
    /// cascades over the developer override and group policy, but sits BELOW
    /// the safety force-offs (safe / panic / per-call-only / memory-master)
    /// which always win — a per-tool override can never loosen those.</para>
    /// </summary>
    public static string ResolveEffectivePolicy(
        string group, PolicySnapshot snapshot, string? perToolOverride = null)
    {
        // Safe mode fail-closed: no MCP tools should execute.
        if (snapshot.SafeModeEnabled)
            return "off";

        // Panic mode blocks side-effect groups while preserving
        // read-only troubleshooting pathways.
        if (snapshot.PanicModeEnabled && SideEffectGroups.Contains(group))
            return "off";

        // Meta / Time → always allowed
        if (group == "meta")
            return "always";

        // Sensitive read tools (e.g., clipboard_read) always prompt —
        // a per-tool override cannot loosen or persist these.
        if (PerCallOnlyGroups.Contains(group))
            return "ask";

        // Memory master off → treat memory groups as off
        if (!snapshot.MemoryEnabled &&
            (group == "memoryRead" || group == "memoryWrite"))
            return "off";

        // Per-tool override wins over both developer override and group policy.
        var normalizedToolOverride = NormalizeOverrideValue(perToolOverride);
        if (normalizedToolOverride is not null)
            return normalizedToolOverride;

        // Developer override applies to all overridable groups
        if (OverridableGroups.Contains(group) && snapshot.DeveloperOverride != "none")
            return snapshot.DeveloperOverride;

        // Per-group policy
        if (snapshot.GroupPolicies.TryGetValue(group, out var policy))
            return policy;

        // Unknown tool default
        return snapshot.UnknownToolDefault;
    }

    /// <summary>
    /// Normalizes a raw per-tool override value to one of "off" / "ask" /
    /// "always", or null (meaning "inherit the group"). Unlike group
    /// policies, an unrecognized/empty value is NOT defaulted to "ask" — an
    /// absent or invalid override simply means the tool inherits its group.
    /// This is the single source of truth for valid-override semantics; both
    /// permission gates route through it.
    /// </summary>
    public static string? NormalizeOverrideValue(string? raw)
    {
        return (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "off" => "off",
            "ask" => "ask",
            "always" => "always",
            _ => null,
        };
    }

    /// <summary>
    /// Looks up the normalized per-tool override for a canonical tool name in
    /// the given override map (case-insensitive), or null when absent/invalid.
    /// Shared by the runtime gate, the headless gate, and the catalog builder
    /// so lookup + normalization semantics never diverge.
    /// </summary>
    public static string? ResolveToolOverride(
        IReadOnlyDictionary<string, string>? overrides, string? canonicalToolName)
    {
        if (overrides is null || overrides.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(canonicalToolName)) return null;

        if (overrides.TryGetValue(canonicalToolName, out var raw))
            return NormalizeOverrideValue(raw);

        // Fall back to a case-insensitive scan for maps that were not built
        // with an OrdinalIgnoreCase comparer.
        foreach (var kvp in overrides)
        {
            if (string.Equals(kvp.Key, canonicalToolName, StringComparison.OrdinalIgnoreCase))
                return NormalizeOverrideValue(kvp.Value);
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    // Snapshot Construction
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an immutable policy snapshot from application settings.
    /// </summary>
    /// <param name="settings">Current application settings.</param>
    /// <param name="isDebugBuild">
    /// When true, unknown tools default to "ask".
    /// When false, unknown tools default to "off".
    /// </param>
    public static PolicySnapshot BuildSnapshot(AppSettings settings, bool isDebugBuild)
    {
        var perms = settings.Mcp.Permissions;
        var policies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["screen"]      = NormalizePolicy(perms.Screen),
            ["files"]       = NormalizePolicy(perms.Files),
            ["system"]      = NormalizePolicy(perms.System),
            ["web"]         = NormalizePolicy(perms.Web),
            ["memoryRead"]  = NormalizePolicy(perms.MemoryRead),
            ["memoryWrite"] = NormalizePolicy(perms.MemoryWrite),
        };

        return new PolicySnapshot
        {
            GroupPolicies      = policies,
            DeveloperOverride  = NormalizeDeveloperOverride(perms.DeveloperOverride),
            MemoryEnabled      = settings.Memory.Enabled,
            PanicModeEnabled   = settings.RuntimeSafety.PanicMode,
            SafeModeEnabled    = settings.RuntimeSafety.SafeMode,
            UnknownToolDefault = isDebugBuild ? "ask" : "off",
            ToolOverrides      = NormalizeToolOverrides(perms.ToolOverrides)
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Redacted Purpose String
    // ─────────────────────────────────────────────────────────────────

    private const int MaxPurposeLength = 200;

    private static readonly Regex SecretKeyPattern = new(
        @"(token|key|secret|password|api_key|auth|bearer|credential)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Builds a truncated, redacted purpose string for permission prompts.
    /// Only extracts safe fields (path, url, command, query, fact_id, tag).
    /// </summary>
    public static string BuildRedactedPurpose(string canonical, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return $"Use tool '{canonical}'.";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            var parts = new List<string>();

            TryExtractSafe(root, "path",    parts);
            TryExtractSafe(root, "url",     parts);
            TryExtractSafe(root, "command", parts);
            TryExtractSafe(root, "query",   parts);
            TryExtractSafe(root, "place",   parts);
            TryExtractSafe(root, "latitude", parts);
            TryExtractSafe(root, "longitude", parts);
            TryExtractSafe(root, "countryCode", parts);
            TryExtractSafe(root, "regionCode", parts);
            TryExtractSafe(root, "year", parts);
            TryExtractSafe(root, "maxItems", parts);
            TryExtractSafe(root, "fact_id", parts);
            TryExtractSafe(root, "tag",     parts);

            if (parts.Count == 0)
                return $"Use tool '{canonical}'.";

            var detail = string.Join(", ", parts);
            if (detail.Length > MaxPurposeLength)
                detail = detail[..MaxPurposeLength] + "\u2026";

            return $"Use '{canonical}': {detail}";
        }
        catch
        {
            return $"Use tool '{canonical}'.";
        }
    }

    private static void TryExtractSafe(
        JsonElement root, string fieldName, List<string> parts)
    {
        if (!root.TryGetProperty(fieldName, out var prop))
            return;

        var value = prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (SecretKeyPattern.IsMatch(fieldName) || SecretKeyPattern.IsMatch(value))
        {
            parts.Add($"{fieldName}: [REDACTED]");
            return;
        }

        var truncated = value.Length > 80 ? value[..80] + "\u2026" : value;
        parts.Add($"{fieldName}: {truncated}");
    }

    // ─────────────────────────────────────────────────────────────────
    // Normalization Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string NormalizePolicy(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "off"    => "off",
            "ask"    => "ask",
            "always" => "always",
            _        => "ask"
        };
    }

    private static string NormalizeDeveloperOverride(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "ask"    => "ask",
            "always" => "always",
            _        => "none"   // "off" normalizes to "none" — use per-group settings to disable individual groups
        };
    }

    /// <summary>
    /// Normalizes a raw per-tool override map for the snapshot: trims and
    /// lowercases keys (canonical tool names are snake_case), keeps only
    /// valid {off, ask, always} values, and returns null when nothing
    /// survives so the snapshot carries a clean absent state.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? NormalizeToolOverrides(
        IReadOnlyDictionary<string, string>? raw)
    {
        if (raw is null || raw.Count == 0) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in raw)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
            var value = NormalizeOverrideValue(kvp.Value);
            if (value is null) continue;
            result[kvp.Key.Trim().ToLowerInvariant()] = value;
        }

        return result.Count == 0 ? null : result;
    }
}
