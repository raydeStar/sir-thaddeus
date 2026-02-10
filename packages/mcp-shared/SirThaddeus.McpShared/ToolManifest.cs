using System.Text.Json;
using System.Text.Json.Serialization;

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
// This is a cross-platform (net8.0) package so tests can reference it
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
    /// Serializes the manifest to a bounded JSON string.
    /// </summary>
    public static string ToJson()
        => JsonSerializer.Serialize(All, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented       = true,
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
            Permission  = "implicit",
            Description = "Stores facts the user asked to remember. Checks duplicates/conflicts.",
            Limits      = "Max 10 facts per call. Upsert (idempotent)."
        },
        new()
        {
            Name        = "memory_update_fact",
            Aliases     = ["MemoryUpdateFact"],
            Category    = "memory",
            ReadWrite   = "write",
            Permission  = "implicit",
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
            Permission  = "implicit",
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
            Permission  = "none",
            Description = "Searches the web and extracts article content from top results.",
            Limits      = "Max 10 results. 8s search timeout, 10s per page. Excerpts <= 1000 chars."
        },
        new()
        {
            Name        = "browser_navigate",
            Aliases     = ["BrowserNavigate"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Fetches and extracts content from a specific URL.",
            Limits      = "20s timeout. Single page. Content <= 4000 chars."
        },
        new()
        {
            Name        = "weather_geocode",
            Aliases     = ["WeatherGeocode"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Geocodes a place string to coordinates for weather lookup.",
            Limits      = "Max 5 candidates. Geocode cache enabled."
        },
        new()
        {
            Name        = "weather_forecast",
            Aliases     = ["WeatherForecast"],
            Category    = "web",
            ReadWrite   = "read",
            Permission  = "none",
            Description = "Returns normalized weather forecast from coordinates (NWS US, Open-Meteo fallback).",
            Limits      = "Max 7 days. Forecast cache 10-30 min."
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
            Name        = "file_list",
            Aliases     = ["FileList"],
            Category    = "file",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Lists files and directories in a folder.",
            Limits      = "Max 100 entries per call."
        },

        // ── System Tools ─────────────────────────────────────────────
        new()
        {
            Name        = "system_execute",
            Aliases     = ["SystemExecute"],
            Category    = "system",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Executes an allowlisted system command.",
            Limits      = "Strict allowlist. No shell metacharacters. dotnet verb restrictions."
        },

        // ── Screen Tools ─────────────────────────────────────────────
        new()
        {
            Name        = "screen_capture",
            Aliases     = ["ScreenCapture"],
            Category    = "screen",
            ReadWrite   = "read",
            Permission  = "required",
            Description = "Captures the screen and extracts text via OCR.",
            Limits      = "OCR text <= 8000 chars. Single snapshot."
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
