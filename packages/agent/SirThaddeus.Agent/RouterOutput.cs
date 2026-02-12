namespace SirThaddeus.Agent;

// ─────────────────────────────────────────────────────────────────────────
// Router Output — Structured Intent Classification
//
// Produced by the intent router (LLM + heuristics). Consumed by the
// policy gate to determine which tools are exposed to the executor.
//
// The Intent string drives the policy lookup. The boolean flags are
// derived from the intent for now (heuristics set them); they become
// independently useful when the LLM can produce structured JSON
// directly in a future iteration.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Structured output from the intent router. Describes what the user's
/// message needs so the policy gate can decide which tools to expose.
/// </summary>
public sealed record RouterOutput
{
    /// <summary>
    /// Classified intent. Drives the policy gate lookup.
    /// See <see cref="Intents"/> for the known taxonomy.
    /// </summary>
    public required string Intent { get; init; }

    // ── Capability flags ─────────────────────────────────────────────
    // Derived from the intent today. Will be independently set by the
    // LLM when it can produce structured JSON reliably.

    public bool NeedsWeb               { get; init; }
    public bool NeedsBrowserAutomation  { get; init; }
    public bool NeedsSearch             { get; init; }
    public bool NeedsMemoryRead         { get; init; }
    public bool NeedsMemoryWrite        { get; init; }
    public bool NeedsFileAccess         { get; init; }
    public bool NeedsScreenRead         { get; init; }
    public bool NeedsSystemExecute      { get; init; }

    /// <summary>
    /// Risk assessment: "low", "medium", or "high".
    /// Higher risk → stricter permission requirements.
    /// </summary>
    public string RiskLevel { get; init; } = "low";

    /// <summary>
    /// Classifier confidence (0.0–1.0). Below the threshold, heuristic
    /// fallbacks take priority over the LLM's classification.
    /// </summary>
    public double Confidence { get; init; } = 0.5;
}

/// <summary>
/// Intent taxonomy — the known intent strings that the router can
/// produce and the policy gate can map. Stored as constants to avoid
/// typo-driven bugs.
/// </summary>
public static class Intents
{
    // ── No tools ─────────────────────────────────────────────────────
    public const string ChatOnly = "chat_only";
    public const string UtilityDeterministic = "utility_deterministic";

    // ── Search ───────────────────────────────────────────────────────
    public const string LookupSearch = "lookup_search";

    // ── Browser ──────────────────────────────────────────────────────
    public const string BrowseOnce        = "browse_once";
    public const string OneShotDiscovery  = "one_shot_discovery";

    // ── Screen ───────────────────────────────────────────────────────
    public const string ScreenObserve = "screen_observe";

    // ── Files ────────────────────────────────────────────────────────
    public const string FileTask = "file_task";

    // ── System ───────────────────────────────────────────────────────
    public const string SystemTask = "system_task";

    // ── Memory ───────────────────────────────────────────────────────
    public const string MemoryRead  = "memory_read";
    public const string MemoryWrite = "memory_write";

    // ── Fallback ─────────────────────────────────────────────────────
    /// <summary>
    /// Tooling intent that couldn't be narrowed further. Gets the full
    /// tool set — same as the old <c>ChatIntent.Tooling</c> behavior.
    /// </summary>
    public const string GeneralTool = "general_tool";
}
