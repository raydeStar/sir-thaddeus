using System.Collections.Concurrent;
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
    public required string UnknownToolDefault  { get; init; }
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
        ["file_list"]            = "files",

        // System
        ["system_execute"]       = "system",

        // Web
        ["web_search"]           = "web",
        ["browser_navigate"]     = "web",
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
        ["time_now"]               = "meta",
    };

    /// <summary>
    /// Groups subject to the developer override.
    /// Memory groups are excluded by design.
    /// </summary>
    public static readonly HashSet<string> DangerousGroups =
        new(StringComparer.OrdinalIgnoreCase)
        { "screen", "files", "system", "web" };

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
    /// </summary>
    public static string ResolveEffectivePolicy(string group, PolicySnapshot snapshot)
    {
        // Meta / Time → always allowed
        if (group == "meta")
            return "always";

        // Memory master off → treat memory groups as off
        if (!snapshot.MemoryEnabled &&
            (group == "memoryRead" || group == "memoryWrite"))
            return "off";

        // Developer override applies to dangerous groups only
        if (DangerousGroups.Contains(group) && snapshot.DeveloperOverride != "none")
            return snapshot.DeveloperOverride;

        // Per-group policy
        if (snapshot.GroupPolicies.TryGetValue(group, out var policy))
            return policy;

        // Unknown tool default
        return snapshot.UnknownToolDefault;
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
            UnknownToolDefault = isDebugBuild ? "ask" : "off"
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
            "off"    => "off",
            "ask"    => "ask",
            "always" => "always",
            _        => "none"
        };
    }
}
