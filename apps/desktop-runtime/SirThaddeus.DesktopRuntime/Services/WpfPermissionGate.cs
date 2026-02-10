using System.Collections.Concurrent;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Invocation;
using SirThaddeus.PermissionBroker;

namespace SirThaddeus.DesktopRuntime.Services;

// ─────────────────────────────────────────────────────────────────────────
// WPF Permission Gate — Runtime Enforcement Point
//
// Implements IToolPermissionGate using a per-group policy model.
// All deterministic logic (group resolution, effective policy, redaction)
// is delegated to ToolGroupPolicy in the agent package for testability.
//
// This class handles:
//   - Immutable snapshot swap (thread-safe settings updates)
//   - Session grant cache + epoch-based invalidation
//   - WPF permission prompt delegation
//   - Audit logging of blocked/denied calls
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Runtime-side implementation of <see cref="IToolPermissionGate"/>.
/// Single enforcement point for all MCP tool permissions.
/// </summary>
public sealed class WpfPermissionGate : IToolPermissionGate
{
    private readonly IPermissionBroker   _broker;
    private readonly IPermissionPrompter _prompter;
    private readonly IAuditLogger        _audit;

    /// <summary>
    /// Immutable policy snapshot. Swapped atomically via
    /// <see cref="UpdateSettings"/> when the user saves settings.
    /// </summary>
    private volatile PolicySnapshot _snapshot;

    /// <summary>
    /// Session grants keyed by (group, epoch). Cleared on New Chat.
    /// The epoch increments each time <see cref="ClearSessionGrants"/>
    /// is called, invalidating all prior grants without a race.
    /// </summary>
    private readonly ConcurrentDictionary<(string group, int epoch), bool> _sessionGrants = new();
    private volatile int _conversationEpoch;

    /// <summary>Default TTL for permission tokens issued by the broker.</summary>
    private static readonly TimeSpan TokenTtl = TimeSpan.FromSeconds(60);

    // ─────────────────────────────────────────────────────────────────
    // Construction
    // ─────────────────────────────────────────────────────────────────

    public WpfPermissionGate(
        IPermissionBroker   broker,
        IPermissionPrompter prompter,
        IAuditLogger        audit,
        AppSettings         initialSettings)
    {
        _broker   = broker   ?? throw new ArgumentNullException(nameof(broker));
        _prompter = prompter ?? throw new ArgumentNullException(nameof(prompter));
        _audit    = audit    ?? throw new ArgumentNullException(nameof(audit));

        _snapshot = ToolGroupPolicy.BuildSnapshot(initialSettings, IsDebugBuild);
    }

    // ─────────────────────────────────────────────────────────────────
    // Settings update (thread-safe swap)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Swaps the immutable policy snapshot. Called from the UI thread
    /// when the user saves settings. Thread-safe via volatile write.
    /// </summary>
    public void UpdateSettings(AppSettings settings)
    {
        _snapshot = ToolGroupPolicy.BuildSnapshot(settings, IsDebugBuild);
    }

    // ─────────────────────────────────────────────────────────────────
    // "Allow always" persistence callback
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the user clicks "Allow always" in a permission prompt.
    /// The subscriber (App.xaml.cs) persists the group policy change to
    /// settings.json and refreshes the SettingsViewModel.
    /// Argument is the group key (e.g. "screen", "web", "files").
    /// </summary>
    public event Action<string>? PersistGroupAsAlways;

    // ─────────────────────────────────────────────────────────────────
    // Session grant management
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clears all session-scoped grants. Call on "New Chat".
    /// Bumps the epoch so concurrent in-flight calls see the change
    /// without a dictionary race.
    /// </summary>
    public void ClearSessionGrants()
    {
        Interlocked.Increment(ref _conversationEpoch);
        _sessionGrants.Clear();
    }

    // ─────────────────────────────────────────────────────────────────
    // IToolPermissionGate.CheckAsync
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ToolPermissionResult> CheckAsync(
        string toolName, string argumentsJson, CancellationToken ct)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var snapshot  = _snapshot; // capture once for consistency
        var group     = ToolGroupPolicy.ResolveGroup(canonical);
        var effective = ToolGroupPolicy.ResolveEffectivePolicy(group, snapshot);

        // ── Off → hard block, no prompt ──────────────────────────
        if (effective == "off")
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "gate",
                Action = "MCP_PERMISSION_BLOCKED",
                Result = "disabled_in_settings",
                Details = new Dictionary<string, object>
                {
                    ["tool"]  = canonical,
                    ["group"] = group
                }
            });
            return ToolPermissionResult.Deny("Disabled in Settings");
        }

        // ── Always → auto-approve ────────────────────────────────
        if (effective == "always")
            return ToolPermissionResult.NotRequired();

        // ── Ask → check session grant, then prompt ───────────────
        var epoch = _conversationEpoch;
        if (_sessionGrants.ContainsKey((group, epoch)))
            return ToolPermissionResult.NotRequired();

        var purpose = ToolGroupPolicy.BuildRedactedPurpose(canonical, argumentsJson);
        var request = new PermissionRequest
        {
            Capability = MapGroupToCapability(group),
            Purpose    = purpose,
            Duration   = TokenTtl,
            Requester  = "agent"
        };

        PermissionDecision decision;
        try
        {
            decision = await _prompter.PromptAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            return ToolPermissionResult.Deny("Permission prompt cancelled");
        }

        if (!decision.Approved)
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "user",
                Action = "MCP_PERMISSION_DENIED",
                Result = decision.DenialReason ?? "Denied by user",
                Details = new Dictionary<string, object>
                {
                    ["tool"]  = canonical,
                    ["group"] = group
                }
            });
            return ToolPermissionResult.Deny(
                decision.DenialReason ?? "Denied by user");
        }

        // ── Persist as "always" if requested ──────────────────────
        if (decision.PersistAsAlways)
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "user",
                Action = "MCP_PERMISSION_ALLOW_ALWAYS",
                Result = "persisted",
                Details = new Dictionary<string, object>
                {
                    ["tool"]  = canonical,
                    ["group"] = group
                }
            });
            PersistGroupAsAlways?.Invoke(group);
        }
        // ── Cache session grant if requested ─────────────────────
        else if (decision.RememberForSession)
        {
            _sessionGrants[(group, epoch)] = true;
        }

        // ── Issue broker token for audit trail ───────────────────
        var token = _broker.IssueToken(request);
        return ToolPermissionResult.Grant(token.Id);
    }

    // ─────────────────────────────────────────────────────────────────
    // Capability mapping (for legacy broker compatibility)
    // ─────────────────────────────────────────────────────────────────

    private static Capability MapGroupToCapability(string group) => group switch
    {
        "screen"      => Capability.ScreenRead,
        "files"       => Capability.FileAccess,
        "system"      => Capability.SystemExecute,
        "web"         => Capability.WebAccess,
        "memoryRead"  => Capability.MemoryRead,
        "memoryWrite" => Capability.MemoryWrite,
        _             => Capability.SystemExecute // safe fallback: prompts
    };

    // ─────────────────────────────────────────────────────────────────
    // Build configuration detection
    // ─────────────────────────────────────────────────────────────────

    private static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif
}
