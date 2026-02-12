using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

// ─────────────────────────────────────────────────────────────────────────
// Policy Gate — Deterministic Tool Filter
//
// Pure function: RouterOutput → PolicyDecision.
// No LLM, no side effects, no state. The policy table is static code,
// not learned or inferred. If a tool isn't in AllowedTools, it doesn't
// exist in the executor's world.
//
// This is the architectural equivalent of "type safety for agents."
// Bad tool combinations become unrepresentable, not merely discouraged.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// The output of the policy gate. Tells the executor which tools it
/// may use and which permissions are required.
/// </summary>
public sealed record PolicyDecision
{
    /// <summary>
    /// Tool names the executor is allowed to call. Only these tools
    /// are included in the LLM's tool definitions.
    /// </summary>
    public required IReadOnlyList<string> AllowedTools { get; init; }

    /// <summary>
    /// Explicit deny list — tools that MUST NOT be exposed even if
    /// they appear in AllowedTools (safety net for wildcard policies).
    /// </summary>
    public IReadOnlyList<string> ForbiddenTools { get; init; } = [];

    /// <summary>
    /// Capabilities that must have active permission tokens before
    /// the executor runs. If any are missing, the orchestrator should
    /// request permission or deny the action.
    /// </summary>
    public IReadOnlyList<string> RequiredPermissions { get; init; } = [];

    /// <summary>
    /// Whether the executor should use the full tool loop (true) or
    /// a streamlined no-tool path (false). Chat-only intent skips
    /// the tool loop entirely for speed.
    /// </summary>
    public bool UseToolLoop { get; init; } = true;
}

/// <summary>
/// Deterministic policy gate. Maps a <see cref="RouterOutput"/> to a
/// <see cref="PolicyDecision"/> that controls which tools the executor
/// can see. No LLM involved — just a lookup table.
/// </summary>
public static class PolicyGate
{
    // ─────────────────────────────────────────────────────────────────
    // Policy Table
    //
    // Each intent maps to exactly one PolicyDecision. Tools not in
    // AllowedTools are invisible to the model. This prevents the
    // model from "accidentally" calling screen_capture during a web
    // search, or memory_store_facts during a file read.
    //
    // When adding new intents, add them here first, THEN update the
    // router. Policy before classification — always.
    // ─────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, PolicyDecision> PolicyTable = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Chat only: no tools at all ───────────────────────────────
        // Pure conversation. The tool loop is skipped entirely.
        [Intents.ChatOnly] = new PolicyDecision
        {
            AllowedTools        = [],
            UseToolLoop         = false,
            RequiredPermissions = []
        },

        // ── Deterministic utility: inline-only, no web/search ────────
        // This route is used for strict deterministic math/conversion
        // requests. No tools are exposed and the executor stays out of
        // the tool loop entirely.
        [Intents.UtilityDeterministic] = new PolicyDecision
        {
            AllowedTools        = [],
            ForbiddenTools      = ["web_search", "WebSearch", "browser_navigate"],
            UseToolLoop         = false,
            RequiredPermissions = []
        },

        // ── Web search: search tool only ─────────────────────────────
        // Deterministic path — search is called directly, not via the
        // tool loop. This entry exists for validation/audit purposes.
        [Intents.LookupSearch] = new PolicyDecision
        {
            AllowedTools        = ["web_search", "WebSearch"],
            ForbiddenTools      = ["screen_capture", "system_execute",
                                   "memory_store_facts", "memory_update_fact"],
            UseToolLoop         = false,   // deterministic path
            RequiredPermissions = []
        },

        // ── Browse once: fetch a specific URL ────────────────────────
        [Intents.BrowseOnce] = new PolicyDecision
        {
            AllowedTools        = ["browser_navigate"],
            ForbiddenTools      = ["system_execute", "screen_capture"],
            RequiredPermissions = ["BrowserControl"]
        },

        // ── One-shot discovery: research (search + browse) ───────────
        [Intents.OneShotDiscovery] = new PolicyDecision
        {
            AllowedTools        = ["web_search", "WebSearch", "browser_navigate"],
            ForbiddenTools      = ["system_execute", "screen_capture",
                                   "memory_store_facts"],
            RequiredPermissions = ["BrowserControl"]
        },

        // ── Screen observe: what's on the user's screen ──────────────
        [Intents.ScreenObserve] = new PolicyDecision
        {
            AllowedTools        = ["screen_capture", "get_active_window"],
            ForbiddenTools      = ["system_execute", "web_search",
                                   "file_read", "memory_store_facts"],
            RequiredPermissions = ["ScreenRead"]
        },

        // ── File task: read/list files ───────────────────────────────
        [Intents.FileTask] = new PolicyDecision
        {
            AllowedTools        = ["file_read", "file_list"],
            ForbiddenTools      = ["system_execute", "screen_capture",
                                   "web_search"],
            RequiredPermissions = ["FileAccess"]
        },

        // ── System task: execute a command ────────────────────────────
        [Intents.SystemTask] = new PolicyDecision
        {
            AllowedTools        = ["system_execute"],
            ForbiddenTools      = ["screen_capture", "web_search",
                                   "file_read", "memory_store_facts"],
            RequiredPermissions = ["SystemExecute"]
        },

        // ── Memory read: recall facts (handled via pre-fetch) ────────
        // Memory retrieval is a pre-fetch step, not a tool loop call.
        // This intent exists so the router can signal "this is a
        // memory question" without exposing any tools.
        [Intents.MemoryRead] = new PolicyDecision
        {
            AllowedTools        = [],
            UseToolLoop         = false,
            RequiredPermissions = []
        },

        // ── Memory write: user explicitly asked to remember ──────────
        [Intents.MemoryWrite] = new PolicyDecision
        {
            AllowedTools        = ["memory_store_facts", "memory_update_fact"],
            ForbiddenTools      = ["system_execute", "screen_capture",
                                   "web_search", "file_read"],
            RequiredPermissions = []
        },

        // ── General tool: fallback when intent is unclear ────────────
        // Gets everything. This is the old ChatIntent.Tooling behavior.
        // As sub-intent detection improves, fewer messages land here.
        [Intents.GeneralTool] = new PolicyDecision
        {
            AllowedTools        = ["*"],   // wildcard: all tools
            RequiredPermissions = []
        },
    };

    // ─────────────────────────────────────────────────────────────────
    // Evaluate
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks up the policy for a given router output. Returns the
    /// general_tool fallback if the intent is unknown.
    /// </summary>
    public static PolicyDecision Evaluate(RouterOutput router)
    {
        return PolicyTable.TryGetValue(router.Intent, out var decision)
            ? decision
            : PolicyTable[Intents.GeneralTool];
    }

    // ─────────────────────────────────────────────────────────────────
    // Filter Tools
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Filters a full tool list down to only what the policy allows.
    /// Wildcard ("*") in AllowedTools means all tools pass, minus
    /// any in ForbiddenTools.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> FilterTools(
        IReadOnlyList<ToolDefinition> allTools,
        PolicyDecision policy)
    {
        if (allTools.Count == 0 || policy.AllowedTools.Count == 0)
            return [];

        var forbidden = new HashSet<string>(
            policy.ForbiddenTools, StringComparer.OrdinalIgnoreCase);

        // Wildcard: allow everything except forbidden
        if (policy.AllowedTools.Count == 1 && policy.AllowedTools[0] == "*")
        {
            return allTools
                .Where(t => !forbidden.Contains(t.Function.Name))
                .ToList();
        }

        // Explicit allow list: only named tools, minus forbidden
        var allowed = new HashSet<string>(
            policy.AllowedTools, StringComparer.OrdinalIgnoreCase);

        return allTools
            .Where(t => allowed.Contains(t.Function.Name)
                     && !forbidden.Contains(t.Function.Name))
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────
    // Introspection (for tests and debugging)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all known intent strings in the policy table.
    /// </summary>
    public static IReadOnlyList<string> KnownIntents =>
        PolicyTable.Keys.ToList();

    /// <summary>
    /// Returns true if the given intent has a policy entry.
    /// </summary>
    public static bool HasPolicy(string intent) =>
        PolicyTable.ContainsKey(intent);
}
