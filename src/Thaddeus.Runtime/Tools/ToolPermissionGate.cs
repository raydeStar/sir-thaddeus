using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
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

    // Per-thread explicit tool allowlists. Used by automation runs so a
    // pre-approved set of tools skips the modal entirely for that run,
    // while unexpected tool calls still trigger the normal prompt flow.
    // Keyed by threadId; value is a case-insensitive set of tool names.
    private readonly ConcurrentDictionary<string, HashSet<string>> _threadAllowlists = new();

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
    /// Returns true if the given thread is executing inside an automation run
    /// (i.e. a <see cref="RegisterThreadAllowlist"/> scope is active on it).
    /// Callers use this to suppress chat-only virtual tools (e.g.
    /// <c>propose_automation</c>) whose UI makes no sense during a run.
    /// </summary>
    public bool IsAutomationRunThread(string threadId)
        => !string.IsNullOrEmpty(threadId) && _threadAllowlists.ContainsKey(threadId);

    /// <summary>
    /// Registers an explicit allowlist of tool names for all tool calls made
    /// within the given thread. Used by the automation runner to pre-approve
    /// the tools the user selected when creating the automation. Overwrites
    /// any prior allowlist on the same thread.
    /// Returns an <see cref="IDisposable"/> that clears the allowlist — the
    /// runner disposes it in a <c>finally</c>.
    /// </summary>
    public IDisposable RegisterThreadAllowlist(string threadId, IEnumerable<string> toolNames)
    {
        var set = new HashSet<string>(toolNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _threadAllowlists[threadId] = set;
        return new ThreadAllowlistHandle(this, threadId);
    }

    private void ClearThreadAllowlist(string threadId)
    {
        _threadAllowlists.TryRemove(threadId, out _);
    }

    private sealed class ThreadAllowlistHandle : IDisposable
    {
        private readonly ToolPermissionGate _gate;
        private readonly string _threadId;
        private bool _disposed;
        public ThreadAllowlistHandle(ToolPermissionGate gate, string threadId)
        {
            _gate = gate;
            _threadId = threadId;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.ClearThreadAllowlist(_threadId);
        }
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

        // Automation-run allowlist: if this thread belongs to an automation
        // run and the tool is in its pre-approved list, allow without prompt.
        // Tools NOT in the list still fall through to the normal policy so
        // surprise tool calls surface a modal.
        if (_threadAllowlists.TryGetValue(threadId, out var allowlist) &&
            allowlist.Contains(toolName))
        {
            return ToolPermissionDecision.Allow;
        }

        // Session shortcut: if the user approved the group earlier this
        // process, skip the prompt.
        if (_sessionAllow.TryGetValue(group, out var cached) && cached)
            return ToolPermissionDecision.Allow;

        var doc = await _settings.GetAsync(ct).ConfigureAwait(false);
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

        // Policy == "ask" — open a prompt and wait.
        return await AskUserAsync(toolName, argumentsJson, threadId, turnId, group, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Called by the REST endpoint when the user answers a prompt. Returns
    /// true if the id matched a live request.
    /// </summary>
    public bool Respond(string id, ToolPermissionResponse decision)
    {
        if (!_pending.TryRemove(id, out var pending)) return false;

        switch (decision)
        {
            case ToolPermissionResponse.Deny:
                pending.Completion.TrySetResult(ToolPermissionDecision.Deny);
                break;
            case ToolPermissionResponse.Once:
                pending.Completion.TrySetResult(ToolPermissionDecision.Allow);
                break;
            case ToolPermissionResponse.Session:
                _sessionAllow[pending.Group] = true;
                pending.Completion.TrySetResult(ToolPermissionDecision.Allow);
                break;
            case ToolPermissionResponse.Always:
                _sessionAllow[pending.Group] = true;
                _ = PersistAlwaysAsync(pending.Group); // fire-and-forget
                pending.Completion.TrySetResult(ToolPermissionDecision.Allow);
                break;
        }

        // Let subscribers know the prompt is no longer pending.
        _ = _bus.PublishAsync("permission.resolved", new { id, decision = decision.ToString().ToLowerInvariant() });
        return true;
    }

    private async Task<ToolPermissionDecision> AskUserAsync(
        string toolName,
        string argumentsJson,
        string threadId,
        string turnId,
        ToolGroup group,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolPermissionDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(
            Id: id,
            Group: group,
            Completion: tcs,
            Snapshot: new PendingPermission(
                Id: id,
                Tool: toolName,
                Group: group.ToString(),
                ArgsJson: Trim(argumentsJson, 2_000),
                ThreadId: threadId,
                TurnId: turnId,
                CreatedAt: DateTimeOffset.UtcNow),
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
    DateTimeOffset CreatedAt);
