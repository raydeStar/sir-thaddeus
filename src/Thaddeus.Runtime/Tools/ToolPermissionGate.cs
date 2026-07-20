using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SirThaddeus.Agent;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tools;

/// <summary>
/// Decision returned by <see cref="ToolPermissionGate"/> for a single tool call.
/// </summary>
public enum ToolPermissionDecision
{
    Allow,
    Deny,
}

/// <summary>
/// User-chosen response from the permission modal. Each one maps to a
/// different persistence scope:
/// <list type="bullet">
///   <item><see cref="Once"/> — allow this call only; no cache, no settings change.</item>
///   <item><see cref="Session"/> — allow this call + cache "allow" for the group until process restart.</item>
///   <item><see cref="Always"/> — allow + upgrade the group's policy in settings to <c>always</c>.</item>
///   <item><see cref="Deny"/> — reject this call. No cache (so the user can change their mind next time).</item>
/// </list>
/// </summary>
public enum ToolPermissionResponse
{
    Deny,
    Once,
    Session,
    Always,
}

/// <summary>
/// Gates MCP tool calls through the user-configured permission policy.
/// Three outcomes per call:
/// <list type="bullet">
///   <item>If the tool is in the <see cref="ToolGroup.Safe"/> group, or the
///         group's policy is <c>always</c>, or the group was already approved
///         for this session, allow immediately.</item>
///   <item>If policy is <c>off</c>, deny immediately.</item>
///   <item>If policy is <c>ask</c>, publish a <c>permission.request</c> event
///         and await the user's response via <see cref="Respond"/>.</item>
/// </list>
///
/// Session approvals are process-scoped (not persisted). Only
/// <see cref="ToolPermissionResponse.Always"/> writes back to settings.
/// </summary>
public sealed class ToolPermissionGate
{
    private readonly ISettingsStore _settings;
    private readonly IEventBus _bus;
    private readonly ILogger<ToolPermissionGate> _logger;

    // Per-group session cache. Entries live until process restart OR until
    // the group is explicitly denied / Alwaysed via the modal.
    private readonly ConcurrentDictionary<ToolGroup, bool> _sessionAllow = new();

    // Per-tool session cache, keyed by canonical (snake_case) tool name.
    // Populated when a user answers a tool-scoped "ask" prompt with Session
    // (or Always). Independent of the group cache: an explicit per-tool "ask"
    // must keep prompting even when the whole group was session-granted.
    private readonly ConcurrentDictionary<string, bool> _sessionAllowTool =
        new(StringComparer.OrdinalIgnoreCase);

    // Pending ask requests keyed by request id. The UI pulls from GET
    // /api/permissions/pending and resolves with POST /api/permissions/respond.
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();

    public ToolPermissionGate(
        ISettingsStore settings,
        IEventBus bus,
        ILogger<ToolPermissionGate> logger)
    {
        _settings = settings;
        _bus = bus;
        _logger = logger;
    }

    /// <summary>Snapshot of currently-outstanding ask prompts. Used by the UI on refresh.</summary>
    public IReadOnlyList<PendingPermission> ListPending()
    {
        return _pending.Values
            .OrderBy(p => p.CreatedAt)
            .Select(p => p.Snapshot)
            .ToArray();
    }

    /// <summary>
    /// Drops every cached "Session" approval — both group- and tool-scoped.
    /// Used by the harness reset endpoint between tests so a prior test's
    /// granted group or tool never auto-approves the next test's tool calls.
    /// </summary>
    public void ClearSessionGrants()
    {
        _sessionAllow.Clear();
        _sessionAllowTool.Clear();
    }

    public async Task<ToolPermissionDecision> DecideAsync(
        string toolName,
        string argumentsJson,
        string threadId,
        string turnId,
        CancellationToken ct)
    {
        var group = ToolGroupClassifier.Classify(toolName);
        if (group == ToolGroup.Safe) return ToolPermissionDecision.Allow;

        var canonical = AuditedMcpToolClient.Canonicalize(toolName);

        var doc = await _settings.GetAsync(ct).ConfigureAwait(false);

        // Safety mode beats every static override, including a per-tool
        // "always": offline mode hard-denies the Web group.
        if (doc.Privacy.OfflineMode && group == ToolGroup.Web)
        {
            _logger.LogInformation(
                "tool.permission.denied_by_offline_mode tool={Tool} group={Group}",
                toolName, group);
            return ToolPermissionDecision.Deny;
        }

        // Per-tool override layer (most-specific wins). Consulted before the
        // group session cache and the group policy.
        var toolOverride = ToolGroupPolicy.ResolveToolOverride(doc.Permissions?.ToolOverrides, canonical);
        if (string.Equals(toolOverride, "off", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "tool.permission.denied_by_tool_override tool={Tool} group={Group}",
                canonical, group);
            return ToolPermissionDecision.Deny;
        }

        // Wiki mutations always require a fresh, call-scoped confirmation.
        // This is capability-based rather than language-based: a mistaken model
        // call cannot ride a prior group session grant, group Always policy,
        // per-tool Always override, or developer Always override. Explicit Off
        // policies above still fail closed without prompting.
        if (ToolCapabilityRegistry.ResolveCapability(canonical) == ToolCapability.WikiWrite)
        {
            if (toolOverride is null &&
                string.Equals(ResolveEffectivePolicy(doc.Permissions, group), "off", StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "tool.permission.denied_by_policy tool={Tool} group={Group}",
                    canonical, group);
                return ToolPermissionDecision.Deny;
            }

            return await AskUserAsync(
                toolName, canonical, argumentsJson, threadId, turnId, group, "call", ct)
                .ConfigureAwait(false);
        }

        switch (toolOverride)
        {
            case "always":
                return ToolPermissionDecision.Allow;
            case "ask":
                // Explicit per-tool "ask" must prompt even when the group was
                // session-granted — skip the group cache entirely and consult
                // only the per-tool session cache.
                if (_sessionAllowTool.TryGetValue(canonical, out var toolCached) && toolCached)
                    return ToolPermissionDecision.Allow;
                return await AskUserAsync(
                    toolName, canonical, argumentsJson, threadId, turnId, group, "tool", ct)
                    .ConfigureAwait(false);
        }

        // No per-tool override — fall back to group-level resolution.
        // Session shortcut: if the user approved the group earlier this
        // process, skip the prompt. Offline mode is checked above so it
        // always wins over stale session grants.
        if (_sessionAllow.TryGetValue(group, out var cached) && cached)
            return ToolPermissionDecision.Allow;

        var effective = ResolveEffectivePolicy(doc.Permissions, group);

        switch (effective)
        {
            case "always":
                return ToolPermissionDecision.Allow;
            case "off":
                _logger.LogInformation(
                    "tool.permission.denied_by_policy tool={Tool} group={Group}",
                    toolName, group);
                return ToolPermissionDecision.Deny;
        }

        // Policy == "ask" — open a group-scoped prompt and wait.
        return await AskUserAsync(
            toolName, canonical, argumentsJson, threadId, turnId, group, "group", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Called by the REST endpoint when the user answers a prompt. Returns
    /// true if the id matched a live request.
    ///
    /// <para><paramref name="scope"/> selects where a Session/Always grant is
    /// recorded: <c>"group"</c> (default, back-compat) upgrades the whole
    /// capability group exactly as before; <c>"tool"</c> records the decision
    /// against the single canonical tool the prompt was raised for. The scope
    /// carried by the response is honored regardless of the prompt's own
    /// <see cref="PendingPermission.Scope"/> — a user may answer a per-tool
    /// prompt with a group-wide grant, or vice versa. A server-issued
    /// <c>call</c> scope is authoritative: every response applies only to the
    /// current mutation even if an older client submits Session or Always.
    /// Deny/Once never persist and never cache, for any scope.</para>
    /// </summary>
    public bool Respond(string id, ToolPermissionResponse decision, string scope = "group")
    {
        if (!_pending.TryRemove(id, out var pending)) return false;

        var toolScoped = string.Equals(scope, "tool", StringComparison.OrdinalIgnoreCase);
        var callScoped = string.Equals(
            pending.Snapshot.Scope,
            "call",
            StringComparison.OrdinalIgnoreCase);

        switch (decision)
        {
            case ToolPermissionResponse.Deny:
                pending.Completion.TrySetResult(ToolPermissionDecision.Deny);
                break;
            case ToolPermissionResponse.Once:
                pending.Completion.TrySetResult(ToolPermissionDecision.Allow);
                break;
            case ToolPermissionResponse.Session:
                if (!callScoped)
                {
                    if (toolScoped) _sessionAllowTool[pending.Canonical] = true;
                    else _sessionAllow[pending.Group] = true;
                }
                pending.Completion.TrySetResult(ToolPermissionDecision.Allow);
                break;
            case ToolPermissionResponse.Always:
                if (callScoped)
                {
                    // A stale or non-UI client may still submit Always. Honor
                    // the current explicit approval but never persist or cache
                    // it for a call-scoped mutation.
                }
                else if (toolScoped)
                {
                    _sessionAllowTool[pending.Canonical] = true;
                    _ = PersistToolAlwaysAsync(pending.Canonical); // fire-and-forget
                }
                else
                {
                    _sessionAllow[pending.Group] = true;
                    _ = PersistAlwaysAsync(pending.Group); // fire-and-forget
                }
                pending.Completion.TrySetResult(ToolPermissionDecision.Allow);
                break;
        }

        // Let subscribers know the prompt is no longer pending.
        _ = _bus.PublishAsync("permission.resolved", new { id, decision = decision.ToString().ToLowerInvariant() });
        return true;
    }

    private async Task<ToolPermissionDecision> AskUserAsync(
        string toolName,
        string canonical,
        string argumentsJson,
        string threadId,
        string turnId,
        ToolGroup group,
        string scope,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolPermissionDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(
            Id: id,
            Group: group,
            Canonical: canonical,
            Completion: tcs,
            Snapshot: new PendingPermission(
                Id: id,
                Tool: toolName,
                Group: group.ToString(),
                ArgsJson: Trim(argumentsJson, 2_000),
                ThreadId: threadId,
                TurnId: turnId,
                CreatedAt: DateTimeOffset.UtcNow,
                Scope: scope),
            CreatedAt: DateTimeOffset.UtcNow);
        _pending[id] = pending;

        await _bus.PublishAsync("permission.request", pending.Snapshot, correlationId: turnId, ct: ct)
            .ConfigureAwait(false);

        // Register cancellation so if the turn is aborted mid-prompt, we
        // cleanly reject and unpin the pending request.
        using var _ = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var p))
            {
                p.Completion.TrySetResult(ToolPermissionDecision.Deny);
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task PersistAlwaysAsync(ToolGroup group)
    {
        try
        {
            var current = await _settings.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var existing = current.Permissions ?? new PermissionsSettings(
                DeveloperOverride: "none",
                Screen: "ask", Files: "ask", System: "ask", Web: "ask",
                MemoryRead: "always", MemoryWrite: "ask");
            var updated = group switch
            {
                ToolGroup.Screen => existing with { Screen = "always" },
                ToolGroup.Files => existing with { Files = "always" },
                ToolGroup.System => existing with { System = "always" },
                ToolGroup.Web => existing with { Web = "always" },
                ToolGroup.MemoryRead => existing with { MemoryRead = "always" },
                ToolGroup.MemoryWrite => existing with { MemoryWrite = "always" },
                _ => existing,
            };
            if (!ReferenceEquals(existing, updated))
            {
                var next = current with { Permissions = updated };
                await _settings.ReplaceAsync(next, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tool.permission.persist_failed group={Group}", group);
        }
    }

    /// <summary>
    /// Persists a per-tool "always" override for the given canonical tool
    /// name. Copy-on-write: builds a brand-new override dictionary so the
    /// cached settings instance is never mutated in place.
    /// </summary>
    private async Task PersistToolAlwaysAsync(string canonical)
    {
        try
        {
            var current = await _settings.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var existing = current.Permissions ?? new PermissionsSettings(
                DeveloperOverride: "none",
                Screen: "ask", Files: "ask", System: "ask", Web: "ask",
                MemoryRead: "always", MemoryWrite: "ask");

            // Copy-on-write a fresh dictionary; never touch the cached one.
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (existing.ToolOverrides is not null)
            {
                foreach (var kvp in existing.ToolOverrides)
                    merged[kvp.Key] = kvp.Value;
            }

            if (merged.TryGetValue(canonical, out var alreadySet) &&
                string.Equals(alreadySet, "always", StringComparison.OrdinalIgnoreCase))
            {
                return; // no change needed
            }

            merged[canonical] = "always";

            var next = current with { Permissions = existing with { ToolOverrides = merged } };
            await _settings.ReplaceAsync(next, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tool.permission.persist_tool_failed tool={Tool}", canonical);
        }
    }

    /// <summary>
    /// Builds the static permission catalog for GET /api/permissions/catalog.
    /// Enumerates every mapped MCP tool, canonicalizes + de-duplicates to
    /// snake_case names, classifies each with the runtime's
    /// <see cref="ToolGroupClassifier"/> (the enforcement truth), excludes the
    /// Safe/meta group (those never prompt), and reports each tool's explicit
    /// override plus its statically-resolved effective policy. Dynamic safety
    /// modes (offline / panic / safe) are intentionally NOT factored into
    /// <c>effective</c>.
    /// </summary>
    public PermissionCatalog BuildCatalog(SettingsDocument doc)
    {
        var permissions = doc.Permissions;
        var developerOverride = (permissions?.DeveloperOverride ?? "none").Trim().ToLowerInvariant();

        // Canonical snake_case tool name → its enforced group. De-dupe so each
        // canonical name appears once even though the registry holds both
        // snake_case and PascalCase aliases.
        var canonicalToGroup = new Dictionary<string, ToolGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolName in ToolCapabilityRegistry.GetMappings().Keys)
        {
            var canonical = AuditedMcpToolClient.Canonicalize(toolName);
            var group = ToolGroupClassifier.Classify(canonical);
            if (group == ToolGroup.Safe) continue; // never prompts — excluded
            canonicalToGroup[canonical] = group;
        }

        var groups = new List<PermissionCatalogGroup>(CatalogGroupOrder.Count);
        foreach (var (group, key) in CatalogGroupOrder)
        {
            var policy = ResolveGroupPolicy(permissions, group);

            var tools = canonicalToGroup
                .Where(kvp => kvp.Value == group)
                .Select(kvp => kvp.Key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name =>
                {
                    var over = ToolGroupPolicy.ResolveToolOverride(permissions?.ToolOverrides, name);
                    var effective = over ?? ResolveEffectivePolicy(permissions, group);
                    return new PermissionCatalogTool(name, over, effective);
                })
                .ToArray();

            groups.Add(new PermissionCatalogGroup(key, policy, tools));
        }

        return new PermissionCatalog(developerOverride, groups);
    }

    // Fixed group order + camelCase keys for stable catalog output.
    private static readonly IReadOnlyList<(ToolGroup Group, string Key)> CatalogGroupOrder =
        new (ToolGroup, string)[]
        {
            (ToolGroup.Screen, "screen"),
            (ToolGroup.Files, "files"),
            (ToolGroup.System, "system"),
            (ToolGroup.Web, "web"),
            (ToolGroup.MemoryRead, "memoryRead"),
            (ToolGroup.MemoryWrite, "memoryWrite"),
        };

    /// <summary>Raw per-group configured policy from settings (no override applied).</summary>
    private static string ResolveGroupPolicy(PermissionsSettings? settings, ToolGroup group)
    {
        if (settings is null) return "ask";
        return group switch
        {
            ToolGroup.Screen => settings.Screen,
            ToolGroup.Files => settings.Files,
            ToolGroup.System => settings.System,
            ToolGroup.Web => settings.Web,
            ToolGroup.MemoryRead => settings.MemoryRead,
            ToolGroup.MemoryWrite => settings.MemoryWrite,
            _ => "ask",
        };
    }

    private static string ResolveEffectivePolicy(PermissionsSettings? settings, ToolGroup group)
    {
        if (settings is null) return "ask";

        // Developer override applies to the dangerous groups only; memory
        // groups keep their explicit value.
        var overrideValue = (settings.DeveloperOverride ?? "none").Trim().ToLowerInvariant();
        var isDangerous = group is ToolGroup.Screen or ToolGroup.Files
            or ToolGroup.System or ToolGroup.Web;
        if (isDangerous && overrideValue is "off" or "ask" or "always")
            return overrideValue;

        return group switch
        {
            ToolGroup.Screen => settings.Screen,
            ToolGroup.Files => settings.Files,
            ToolGroup.System => settings.System,
            ToolGroup.Web => settings.Web,
            ToolGroup.MemoryRead => settings.MemoryRead,
            ToolGroup.MemoryWrite => settings.MemoryWrite,
            _ => "ask",
        };
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max] + "…";
    }

    private sealed record PendingRequest(
        string Id,
        ToolGroup Group,
        string Canonical,
        TaskCompletionSource<ToolPermissionDecision> Completion,
        PendingPermission Snapshot,
        DateTimeOffset CreatedAt);
}

/// <summary>Wire format for pending permission prompts — used by the WS event and the list endpoint.</summary>
public sealed record PendingPermission(
    string Id,
    string Tool,
    string Group,
    string ArgsJson,
    string ThreadId,
    string TurnId,
    DateTimeOffset CreatedAt,
    // "group" (the prompt was raised for the whole capability group),
    // "tool" (an explicit per-tool "ask" override), or "call" (the action
    // must be confirmed every time). The UI uses this to constrain choices.
    string Scope = "group");

/// <summary>Wire format for GET /api/permissions/catalog.</summary>
public sealed record PermissionCatalog(
    string DeveloperOverride,
    IReadOnlyList<PermissionCatalogGroup> Groups);

/// <summary>A capability group and its per-tool rows in the catalog.</summary>
public sealed record PermissionCatalogGroup(
    string Key,
    string Policy,
    IReadOnlyList<PermissionCatalogTool> Tools);

/// <summary>A single tool row in the catalog: its explicit override (or null) and static effective policy.</summary>
public sealed record PermissionCatalogTool(
    string Name,
    // Force serialization even when null so the wire shape always carries
    // "override": null (the pinned contract the web client builds against),
    // overriding the context-level WhenWritingNull.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Override,
    string Effective);
