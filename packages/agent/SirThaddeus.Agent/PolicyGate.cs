using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

// ─────────────────────────────────────────────────────────────────────────
// Policy Gate — Deterministic Tool Filter
//
// Pure function: RouterOutput → PolicyDecision.
// No LLM, no side effects, no state. The policy table is static code,
// not learned or inferred. If a tool's capability isn't allowed, it doesn't
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
    /// Capability allowlist for this route. Tool exposure is resolved
    /// from capabilities via <see cref="ToolCapabilityRegistry"/>.
    /// </summary>
    public required IReadOnlyList<ToolCapability> AllowedCapabilities { get; init; }

    /// <summary>
    /// Capability denylist for this route.
    /// Applied after AllowedCapabilities.
    /// </summary>
    public IReadOnlyList<ToolCapability> ForbiddenCapabilities { get; init; } = [];

    /// <summary>
    /// Optional explicit tool allowlist exceptions, used sparingly.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Optional explicit tool denylist exceptions.
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
    // Each intent maps to exactly one PolicyDecision. Tools whose
    // capabilities are not allowed are invisible to the model. This prevents the
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
            AllowedCapabilities = [],
            UseToolLoop         = false,
            RequiredPermissions = []
        },

        // ── Deterministic utility: inline-only, no web/search ────────
        // This route is used for strict deterministic math/conversion
        // requests. No tools are exposed and the executor stays out of
        // the tool loop entirely.
        [Intents.UtilityDeterministic] = new PolicyDecision
        {
            AllowedCapabilities = [],
            ForbiddenCapabilities = [ToolCapability.WebSearch, ToolCapability.BrowserNavigate],
            UseToolLoop         = false,
            RequiredPermissions = []
        },

        // ── Web search: search tool only ─────────────────────────────
        // Deterministic path — search is called directly, not via the
        // tool loop. This entry exists for validation/audit purposes.
        [Intents.LookupSearch] = new PolicyDecision
        {
            AllowedCapabilities = [ToolCapability.WebSearch],
            ForbiddenCapabilities = [ToolCapability.ScreenCapture, ToolCapability.SystemExecute, ToolCapability.MemoryWrite],
            UseToolLoop         = false,   // deterministic path
            RequiredPermissions = []
        },

        // ── Browse once: fetch a specific URL ────────────────────────
        [Intents.BrowseOnce] = new PolicyDecision
        {
            AllowedCapabilities = [ToolCapability.BrowserNavigate],
            ForbiddenCapabilities = [ToolCapability.SystemExecute, ToolCapability.ScreenCapture],
            RequiredPermissions = ["BrowserControl"]
        },

        // ── One-shot discovery: research (search + browse) ───────────
        [Intents.OneShotDiscovery] = new PolicyDecision
        {
            AllowedCapabilities = [ToolCapability.WebSearch, ToolCapability.BrowserNavigate],
            ForbiddenCapabilities = [ToolCapability.SystemExecute, ToolCapability.ScreenCapture, ToolCapability.MemoryWrite],
            RequiredPermissions = ["BrowserControl"]
        },

        // ── Screen observe: what's on the user's screen ──────────────
        [Intents.ScreenObserve] = new PolicyDecision
        {
            AllowedCapabilities = [ToolCapability.ScreenCapture],
            ForbiddenCapabilities = [ToolCapability.SystemExecute, ToolCapability.WebSearch, ToolCapability.FileRead, ToolCapability.MemoryWrite],
            RequiredPermissions = ["ScreenRead"]
        },

        // ── File task: read/list files ───────────────────────────────
        [Intents.FileTask] = new PolicyDecision
        {
            AllowedCapabilities = [ToolCapability.FileRead],
            ForbiddenCapabilities = [ToolCapability.SystemExecute, ToolCapability.ScreenCapture, ToolCapability.WebSearch],
            RequiredPermissions = ["FileAccess"]
        },

        // ── System task: execute a command ────────────────────────────
        [Intents.SystemTask] = new PolicyDecision
        {
            AllowedCapabilities = [ToolCapability.SystemExecute],
            ForbiddenCapabilities = [ToolCapability.ScreenCapture, ToolCapability.WebSearch, ToolCapability.FileRead, ToolCapability.MemoryWrite],
            RequiredPermissions = ["SystemExecute"]
        },

        // ── Memory read: recall facts (handled via pre-fetch) ────────
        // Memory retrieval is a pre-fetch step, not a tool loop call.
        // This intent exists so the router can signal "this is a
        // memory question" without exposing any tools.
        [Intents.MemoryRead] = new PolicyDecision
        {
            AllowedCapabilities = [],
            UseToolLoop         = false,
            RequiredPermissions = []
        },

        // ── Memory write: user explicitly asked to remember ──────────
        [Intents.MemoryWrite] = new PolicyDecision
        {
            AllowedCapabilities = [ToolCapability.MemoryWrite],
            ForbiddenCapabilities = [ToolCapability.SystemExecute, ToolCapability.ScreenCapture, ToolCapability.WebSearch, ToolCapability.FileRead],
            RequiredPermissions = []
        },

        // ── General tool: fallback when intent is unclear ────────────
        // Gets everything. This is the old ChatIntent.Tooling behavior.
        // As sub-intent detection improves, fewer messages land here.
        [Intents.GeneralTool] = new PolicyDecision
        {
            AllowedCapabilities = [ToolCapability.MemoryRead, ToolCapability.Meta],
            ForbiddenCapabilities = [ToolCapability.SystemExecute, ToolCapability.ScreenCapture, ToolCapability.FileWrite, ToolCapability.MemoryWrite, ToolCapability.TimeRead],
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
        var decision = PolicyTable.TryGetValue(router.Intent, out var byIntent)
            ? byIntent
            : PolicyTable[Intents.GeneralTool];

        var allowedCapabilities = new HashSet<ToolCapability>(decision.AllowedCapabilities);

        // Minimal fallback may include web only when the route explicitly
        // asks for current information.
        if (IsGeneralToolIntent(router.Intent) && (router.NeedsWeb || router.NeedsSearch))
            allowedCapabilities.Add(ToolCapability.WebSearch);

        // Router emits required capabilities. Gate intersects those with
        // policy to ensure only explicitly allowed capabilities survive.
        if (router.RequiredCapabilities.Count > 0)
            allowedCapabilities.IntersectWith(router.RequiredCapabilities);

        return decision with { AllowedCapabilities = allowedCapabilities.ToList() };
    }

    // ─────────────────────────────────────────────────────────────────
    // Filter Tools
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Filters a full tool list down to only what the policy allows.
    /// Capability mapping is strict: unmapped tools are hidden by default.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> FilterTools(
        IReadOnlyList<ToolDefinition> allTools,
        PolicyDecision policy)
    {
        if (allTools.Count == 0 || policy.AllowedCapabilities.Count == 0)
            return [];

        var filtered = ToolCapabilityRegistry.ResolveTools(
            allTools,
            policy.AllowedCapabilities,
            policy.ForbiddenCapabilities);

        if (policy.AllowedTools.Count > 0)
        {
            var explicitAllowed = new HashSet<string>(policy.AllowedTools, StringComparer.OrdinalIgnoreCase);
            filtered = filtered
                .Concat(allTools.Where(t => explicitAllowed.Contains(t.Function.Name)))
                .GroupBy(t => t.Function.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        if (policy.ForbiddenTools.Count == 0)
            return filtered;

        var explicitForbidden = new HashSet<string>(policy.ForbiddenTools, StringComparer.OrdinalIgnoreCase);
        return filtered
            .Where(t => !explicitForbidden.Contains(t.Function.Name))
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

    private static bool IsGeneralToolIntent(string intent)
        => intent.Equals(Intents.GeneralTool, StringComparison.OrdinalIgnoreCase)
           || !PolicyTable.ContainsKey(intent);
}
