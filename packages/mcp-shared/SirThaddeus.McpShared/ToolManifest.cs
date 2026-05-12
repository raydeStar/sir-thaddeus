using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace SirThaddeus.McpShared;

// ─────────────────────────────────────────────────────────────────────────
// Tool Manifest — Canonical Reference for All MCP Tools
//
// A bounded, deterministic manifest describing every tool the MCP server
// exposes. Used by:
//   - tool_list_capabilities (returns this manifest to the agent)
//   - Documentation generation
//   - Tests (verify manifest completeness and consistency)
//
// This is a cross-platform (net10.0) package so tests can reference it
// without depending on the Windows-only MCP server project.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Static manifest of all MCP tool capabilities. Deterministic and bounded.
/// </summary>
public static class ToolManifest
{
    /// <summary>
    /// All known tools with their metadata. Updated when tools are
    /// added or modified. Order is stable (alphabetical by name).
    /// </summary>
    public static IReadOnlyList<ToolDescriptor> All { get; } = BuildManifest();

    /// <summary>
    /// Deterministic manifest hash used for startup compatibility checks.
    /// </summary>
    public static string ManifestHashSha256 { get; } = ComputeManifestHash();

    private static readonly IReadOnlyDictionary<string, ToolDescriptor> ByName =
        BuildLookup(All);

    /// <summary>
    /// Serializes the manifest to a bounded JSON string.
    /// </summary>
    public static string ToJson()
        => JsonSerializer.Serialize(All, JsonOpts);

    /// <summary>
    /// Finds a tool descriptor by canonical name or alias.
    /// </summary>
    public static bool TryGetTool(string toolName, out ToolDescriptor descriptor)
    {
        var canonical = Canonicalize(toolName);
        if (ByName.TryGetValue(canonical, out descriptor!))
            return true;

        descriptor = default!;
        return false;
    }

    /// <summary>
    /// Returns true when the tool can mutate local or remote state.
    /// Unknown tools fail closed as side-effecting.
    /// </summary>
    public static bool IsSideEffecting(string toolName)
    {
        var canonical = Canonicalize(toolName);

        if (canonical.EndsWith("_preview", StringComparison.OrdinalIgnoreCase))
            return false;

        if (canonical.EndsWith("_apply", StringComparison.OrdinalIgnoreCase))
            return true;

        if (canonical is "policy.set_panic_mode" or "audit.export_bundle")
            return true;

        if (!TryGetTool(canonical, out var descriptor))
            return true;

        if (descriptor.ReadWrite.Equals("write", StringComparison.OrdinalIgnoreCase))
            return true;

        if (canonical.Equals("system_execute", StringComparison.OrdinalIgnoreCase))
            return true;

        if (canonical.StartsWith("memory_store_", StringComparison.OrdinalIgnoreCase) ||
            canonical.StartsWith("memory_update_", StringComparison.OrdinalIgnoreCase) ||
            canonical.StartsWith("memory_delete_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Outbound web access is treated as side-effecting for panic mode.
        return descriptor.Category.Equals("web", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveRiskTier(string toolName)
    {
        var canonical = Canonicalize(toolName);
        if (!TryGetTool(canonical, out var descriptor))
            return "high";

        if (IsSideEffecting(canonical))
            return "high";

        if (descriptor.Category.Equals("meta", StringComparison.OrdinalIgnoreCase) ||
            descriptor.Category.Equals("time", StringComparison.OrdinalIgnoreCase))
        {
            return "low";
        }

        return "medium";
    }

    private static string ComputeManifestHash()
    {
        var bytes = Encoding.UTF8.GetBytes(ToJson());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, ToolDescriptor> BuildLookup(
        IReadOnlyList<ToolDescriptor> tools)
    {
        var lookup = new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            lookup[Canonicalize(tool.Name)] = tool;
            foreach (var alias in tool.Aliases)
                lookup[Canonicalize(alias)] = tool;
        }

        return lookup;
    }

    private static string Canonicalize(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return "";

        var value = toolName.Trim();
        if (value.Contains('_') || value.Contains('.'))
            return value.ToLowerInvariant();

        var sb = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static List<ToolDescriptor> BuildManifest() =>
    [
        // ── Memory Tools ─────────────────────────────────────────────
        new()
        {
            Name        = "memory_retrieve",
            Aliases     = ["MemoryRetrieve"],
            Category    = "memory",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Retrieves relevant memory context for the current query.",
            Limits      = "Max 5 nuggets (normal), 2 (greet). Read-only."
        },
        new()
        {
            Name        = "memory_store_facts",
            Aliases     = ["MemoryStoreFacts"],
            Category    = "memory",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Stores facts the user asked to remember. Checks duplicates/conflicts.",
            Limits      = "Max 10 facts per call. Upsert (idempotent)."
        },
        new()
        {
            Name        = "memory_update_fact",
            Aliases     = ["MemoryUpdateFact"],
            Category    = "memory",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Updates an existing fact after user confirms a conflict resolution.",
            Limits      = "Single fact per call."
        },
        new()
        {
            Name        = "memory_list_facts",
            Aliases     = ["MemoryListFacts"],
            Category    = "memory",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Lists stored facts with optional filter, pagination.",
            Limits      = "Max 50 facts per page."
        },
        new()
        {
            Name        = "memory_delete_fact",
            Aliases     = ["MemoryDeleteFact"],
            Category    = "memory",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Soft-deletes a memory fact by ID.",
            Limits      = "Single fact per call. Soft-delete (reversible)."
        },

        // ── Web Tools ────────────────────────────────────────────────
        new()
        {
            Name        = "web_search",
            Aliases     = ["WebSearch"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Searches the web and extracts article content from top results.",
            Limits      = "Max 10 results. 8s search timeout, 10s per page. Excerpts <= 1000 chars."
        },
        new()
        {
            Name        = "browser_navigate",
            Aliases     = ["BrowserNavigate"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Fetches and extracts content from a specific URL.",
            Limits      = "20s timeout. Single page. Content <= 4000 chars."
        },
        new()
        {
            Name        = "places_discover",
            Aliases     = ["PlacesDiscover"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Discovers nearby businesses and places using open OSM geocoding and Overpass data.",
            Limits      = "Max 20 results. Radius clamped 500-20000m. Open-data provider with endpoint failover."
        },
        new()
        {
            Name        = "places_lookup",
            Aliases     = ["PlacesLookup"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Looks up place details (hours, reviews, links, map coordinates) for deep-dive briefings.",
            Limits      = "Single place lookup. Timeout via ST_DEEPDIVE_PLACES_TIMEOUT_MS. Reviews clamped 1-5."
        },
        new()
        {
            Name        = "weather_geocode",
            Aliases     = ["WeatherGeocode"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Geocodes a place string to coordinates for weather lookup.",
            Limits      = "Max 5 candidates. Geocode cache enabled."
        },
        new()
        {
            Name        = "weather_forecast",
            Aliases     = ["WeatherForecast"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Returns normalized weather forecast from coordinates (NWS US, Open-Meteo fallback).",
            Limits      = "Max 7 days. Forecast cache 10-30 min."
        },
        new()
        {
            Name        = "resolve_timezone",
            Aliases     = ["ResolveTimezone"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Resolves timezone from coordinates (NWS US, Open-Meteo fallback).",
            Limits      = "Coordinate input only. Timezone cache enabled."
        },
        new()
        {
            Name        = "holidays_get",
            Aliases     = ["HolidaysGet"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Returns public holidays for a country/year using Nager.Date.",
            Limits      = "Year clamped 1900-2100. Max 100 items."
        },
        new()
        {
            Name        = "holidays_next",
            Aliases     = ["HolidaysNext"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Returns upcoming public holidays for a country using Nager.Date.",
            Limits      = "Max 25 items."
        },
        new()
        {
            Name        = "holidays_is_today",
            Aliases     = ["HolidaysIsToday"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Checks if today is a public holiday for a country/region.",
            Limits      = "Bounded single-day response."
        },
        new()
        {
            Name        = "feed_fetch",
            Aliases     = ["FeedFetch"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Fetches and parses RSS/Atom feeds directly from URL.",
            Limits      = "Max 20 items. Feed payload size bounded."
        },
        new()
        {
            Name        = "status_check_url",
            Aliases     = ["StatusCheckUrl"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Checks URL reachability with latency/status metadata.",
            Limits      = "HEAD first, GET fallback. Short cache TTL."
        },

        // ── File Tools ───────────────────────────────────────────────
        new()
        {
            Name        = "file_read",
            Aliases     = ["FileRead"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Reads text content of a file.",
            Limits      = "Max 1 MB file size."
        },
        new()
        {
            Name        = "file_read_preview",
            Aliases     = ["FileReadPreview"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Builds a deterministic preview plan for file_read.",
            Limits      = "Returns preview_id + file metadata only."
        },
        new()
        {
            Name        = "file_read_apply",
            Aliases     = ["FileReadApply"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Executes file_read for a previously created preview_id.",
            Limits      = "Preview must be unexpired and valid."
        },
        new()
        {
            Name        = "document_read",
            Aliases     = ["DocumentRead"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Reads and extracts text from local document formats (PDF, DOCX, XLSX, CSV, RTF, Markdown, plain text).",
            Limits      = "Default max 4000 chars in output; supports maxChars override."
        },
        new()
        {
            Name        = "file_list",
            Aliases     = ["FileList"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Lists files and directories in a folder.",
            Limits      = "Max 100 entries per call."
        },
        new()
        {
            Name        = "file_list_preview",
            Aliases     = ["FileListPreview"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Builds a deterministic preview plan for file_list.",
            Limits      = "Returns preview_id + path metadata only."
        },
        new()
        {
            Name        = "file_list_apply",
            Aliases     = ["FileListApply"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Executes file_list for a previously created preview_id.",
            Limits      = "Preview must be unexpired and valid."
        },
        new()
        {
            Name        = "wiki_roots_list",
            Aliases     = ["WikiRootsList"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Lists local Wiki Canvas roots with ids, names, paths, and timestamps.",
            Limits      = "Bounded to configured local wiki library roots."
        },
        new()
        {
            Name        = "wiki_root_create",
            Aliases     = ["WikiRootCreate"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Creates a local Wiki Canvas root inside the configured wiki library directory.",
            Limits      = "Root path must stay inside the wiki library directory."
        },
        new()
        {
            Name        = "wiki_root_rename",
            Aliases     = ["WikiRootRename"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Renames a local Wiki Canvas root without moving its directory.",
            Limits      = "Root id must exist in the local wiki registry."
        },
        new()
        {
            Name        = "wiki_root_remove",
            Aliases     = ["WikiRootRemove"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Removes a local Wiki Canvas root from the registry without deleting files from disk.",
            Limits      = "Root id must exist in the local wiki registry. Files are preserved on disk."
        },
        new()
        {
            Name        = "wiki_tree_get",
            Aliases     = ["WikiTreeGet"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Gets folders and page metadata for one Wiki Canvas root.",
            Limits      = "Returns metadata only. Folder/page list clamped to 500 each."
        },
        new()
        {
            Name        = "wiki_folder_create",
            Aliases     = ["WikiFolderCreate"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Creates a folder inside a Wiki Canvas root.",
            Limits      = "Folder must belong to the requested wiki root."
        },
        new()
        {
            Name        = "wiki_folder_rename",
            Aliases     = ["WikiFolderRename"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Renames a Wiki Canvas folder and updates descendant page paths.",
            Limits      = "Folder must belong to the requested wiki root."
        },
        new()
        {
            Name        = "wiki_folder_move",
            Aliases     = ["WikiFolderMove"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Moves a Wiki Canvas folder to another parent folder or to the root.",
            Limits      = "Rejects cycles and cross-root parent folders."
        },
        new()
        {
            Name        = "wiki_folder_delete",
            Aliases     = ["WikiFolderDelete"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Deletes a Wiki Canvas folder and all descendant folders, pages, files, and revisions.",
            Limits      = "Folder must belong to the requested wiki root. Destructive."
        },
        new()
        {
            Name        = "wiki_page_create",
            Aliases     = ["WikiPageCreate"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Creates a Markdown page in a Wiki Canvas root or folder.",
            Limits      = "Markdown is persisted as the canonical page body."
        },
        new()
        {
            Name        = "wiki_page_read",
            Aliases     = ["WikiPageRead"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Reads one Wiki Canvas page by id, including bounded Markdown body and current version.",
            Limits      = "Default 24000 chars; max 60000 chars."
        },
        new()
        {
            Name        = "wiki_page_update",
            Aliases     = ["WikiPageUpdate"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Replaces a Wiki Canvas page Markdown body using expected-version concurrency.",
            Limits      = "Requires current version from wiki_page_read. Creates a revision."
        },
        new()
        {
            Name        = "wiki_page_rename",
            Aliases     = ["WikiPageRename"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Renames a Wiki Canvas page using expected-version concurrency.",
            Limits      = "Requires current version from wiki_page_read. Creates a revision."
        },
        new()
        {
            Name        = "wiki_page_move",
            Aliases     = ["WikiPageMove"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Moves a Wiki Canvas page to another folder or to the root.",
            Limits      = "Requires current version from wiki_page_read. Rejects cross-root folders. Creates a revision."
        },
        new()
        {
            Name        = "wiki_page_delete",
            Aliases     = ["WikiPageDelete"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Deletes a Wiki Canvas page, Markdown file, and revisions.",
            Limits      = "Optional expected version prevents deleting stale content. Destructive."
        },
        new()
        {
            Name        = "wiki_page_patch_selection",
            Aliases     = ["WikiPagePatchSelection"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Replaces exactly one selected text passage in a Wiki Canvas page.",
            Limits      = "Requires current version and exact selected text match. Creates a revision."
        },
        new()
        {
            Name        = "wiki_page_revisions_list",
            Aliases     = ["WikiPageRevisionsList"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Lists bounded Wiki Canvas page revisions for inspection before restore.",
            Limits      = "Default 20 revisions; max 100. Revision Markdown bodies are bounded."
        },
        new()
        {
            Name        = "wiki_page_revision_restore",
            Aliases     = ["WikiPageRevisionRestore"],
            Category    = "file",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Restores a Wiki Canvas page revision using expected-version concurrency.",
            Limits      = "Requires current version from wiki_page_read. Creates a restore revision."
        },
        new()
        {
            Name        = "wiki_search",
            Aliases     = ["WikiSearch"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Searches local Wiki Canvas pages by title, excerpt, or Markdown body.",
            Limits      = "Optional root filter. Max 50 results."
        },

        // ── System Tools ─────────────────────────────────────────────
        new()
        {
            Name        = "system_execute",
            Aliases     = ["SystemExecute"],
            Category    = "system",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Executes an allowlisted system command.",
            Limits      = "Strict allowlist. No shell metacharacters. dotnet verb restrictions."
        },
        new()
        {
            Name        = "system_execute_preview",
            Aliases     = ["SystemExecutePreview"],
            Category    = "system",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Builds a deterministic execution preview for system_execute.",
            Limits      = "No process launch. Returns preview_id and validation details."
        },
        new()
        {
            Name        = "system_execute_apply",
            Aliases     = ["SystemExecuteApply"],
            Category    = "system",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Executes a previously previewed command by preview_id.",
            Limits      = "Requires confirm=true and unexpired preview."
        },

        // ── Screen Tools ─────────────────────────────────────────────
        new()
        {
            Name        = "clipboard_read",
            Aliases     = ["ClipboardRead"],
            Category    = "system",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Reads text content from the Windows clipboard.",
            Limits      = "Requires clipboard to contain text."
        },
        new()
        {
            Name        = "clipboard_write",
            Aliases     = ["ClipboardWrite"],
            Category    = "system",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Writes text to the Windows clipboard.",
            Limits      = "Text-only write operation."
        },
        new()
        {
            Name        = "screen_capture",
            Aliases     = ["ScreenCapture"],
            Category    = "screen",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Captures the screen and extracts text via OCR. If the active window is a browser, also fetches the actual page content via HTTP.",
            Limits      = "OCR text <= 8000 chars. Page content <= 6000 chars. Single snapshot."
        },
        new()
        {
            Name        = "get_active_window",
            Aliases     = ["GetActiveWindow"],
            Category    = "screen",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Returns the currently active window title, process, and PID.",
            Limits      = "Lightweight. No screen content."
        },

        // ── Meta / Health Tools ──────────────────────────────────────
        new()
        {
            Name        = "tool_ping",
            Aliases     = ["ToolPing"],
            Category    = "meta",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Health check returning server version, uptime, status, and tool count.",
            Limits      = "Bounded JSON response."
        },
        new()
        {
            Name        = "tool_list_capabilities",
            Aliases     = ["ToolListCapabilities"],
            Category    = "meta",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Returns the full tool manifest (name, aliases, category, permissions, limits).",
            Limits      = "Bounded manifest. Deterministic output."
        },
        new()
        {
            Name        = "health.check",
            Aliases     = ["HealthCheck"],
            Category    = "meta",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Control-plane health check with dependency readiness.",
            Limits      = "Bounded JSON response."
        },
        new()
        {
            Name        = "capabilities.describe",
            Aliases     = ["CapabilitiesDescribe"],
            Category    = "meta",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Expanded capability catalog with risk and preview/apply metadata.",
            Limits      = "Bounded deterministic JSON response."
        },
        new()
        {
            Name        = "policy.get_state",
            Aliases     = ["PolicyGetState"],
            Category    = "meta",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Returns panic/safe mode state, budgets, and policy group settings.",
            Limits      = "Read-only, bounded snapshot."
        },
        new()
        {
            Name        = "audit.export_bundle",
            Aliases     = ["AuditExportBundle"],
            Category    = "meta",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Exports a redacted diagnostics bundle for support.",
            Limits      = "Requires explicit confirm=true."
        },
        new()
        {
            Name        = "policy.set_panic_mode",
            Aliases     = ["PolicySetPanicMode"],
            Category    = "meta",
            ReadWrite   = "write",
            Permission  = "required",
            Description = "Sets persistent panic-mode state with explicit confirmation.",
            Limits      = "Requires explicit confirm=true."
        },

        // ── Time Tool ────────────────────────────────────────────────
        new()
        {
            Name        = "time_now",
            Aliases     = ["TimeNow"],
            Category    = "time",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Returns current time as ISO 8601, Unix ms, Windows timezone ID, and UTC offset.",
            Limits      = "Single bounded JSON object."
        }
    ];

    // ── Display category metadata ────────────────────────────────────

    /// <summary>
    /// Human-readable display names for tool categories shown in the
    /// trust-ledger drawer. Keys are lowercase category strings from
    /// <see cref="ToolDescriptor.Category"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CategoryDisplayNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["web"]    = "Web searches",
            ["file"]   = "File operations",
            ["system"] = "System commands",
            ["screen"] = "Screen capture",
            ["memory"] = "Memory",
            ["meta"]   = "System tools",
            ["time"]   = "System tools",
        };

    /// <summary>
    /// Maps a tool category to a human-readable display name.
    /// Falls back to the raw category value when not mapped.
    /// </summary>
    public static string GetCategoryDisplayName(string category)
        => CategoryDisplayNames.TryGetValue(category, out var name) ? name : category;

    /// <summary>
    /// Returns all tools grouped by their <see cref="ToolDescriptor.Category"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ToolDescriptor>> GetToolsByCategory()
        => All.GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
              .ToDictionary(g => g.Key, g => (IReadOnlyList<ToolDescriptor>)g.ToList(),
                            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps permission group names (from McpPermissionsSettings) to the
    /// tool categories they govern. Used to derive logical MCP connections
    /// from the existing per-group permission model.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> PermissionGroupToCategories { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["screen"]      = ["screen"],
            ["files"]       = ["file"],
            ["system"]      = ["system"],
            ["web"]         = ["web"],
            ["memoryRead"]  = ["memory"],
            ["memoryWrite"] = ["memory"],
        };

    /// <summary>
    /// Human-readable display names for MCP permission groups shown as
    /// logical connections in the trust-ledger drawer.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ConnectionDisplayNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["screen"]      = "Screen Capture",
            ["files"]       = "File System",
            ["system"]      = "System Commands",
            ["web"]         = "Web Access",
            ["memoryRead"]  = "Memory (Read)",
            ["memoryWrite"] = "Memory (Write)",
        };

    /// <summary>
    /// Returns the human-readable connection display name for a permission group.
    /// Falls back to the raw group name when not mapped.
    /// </summary>
    public static string GetConnectionDisplayName(string permissionGroup)
        => ConnectionDisplayNames.TryGetValue(permissionGroup, out var name) ? name : permissionGroup;
}

/// <summary>
/// Describes a single MCP tool's metadata for the manifest.
/// </summary>
public sealed record ToolDescriptor
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("aliases")]
    public IReadOnlyList<string> Aliases { get; init; } = [];

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("read_write")]
    public required string ReadWrite { get; init; }

    [JsonPropertyName("permission")]
    public required string Permission { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("limits")]
    public string? Limits { get; init; }
}
