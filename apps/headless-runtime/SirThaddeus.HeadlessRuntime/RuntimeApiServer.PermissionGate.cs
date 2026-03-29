using System.Collections.Concurrent;
using SirThaddeus.Agent;
using SirThaddeus.Config;
using SirThaddeus.Contracts;

internal sealed class ApiPermissionGate : IToolPermissionGate
{
    private readonly Func<string?> _currentRunIdAccessor;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private volatile PolicySnapshot _snapshot;

    private readonly ConcurrentDictionary<string, bool> _sessionGrants = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _requestGroupMap = new(StringComparer.OrdinalIgnoreCase);

    public ApiPermissionGate(AppSettings initialSettings, Func<string?> currentRunIdAccessor)
    {
        _snapshot = ToolGroupPolicy.BuildSnapshot(initialSettings, isDebugBuild: false);
        _currentRunIdAccessor = currentRunIdAccessor;
    }

    public event Action<string, ToolRequestedPayload>? Requested;
    public event Action<string, ToolDecisionPayload>? Resolved;

    public void UpdateSettings(AppSettings settings)
    {
        _snapshot = ToolGroupPolicy.BuildSnapshot(settings, isDebugBuild: false);
    }

    public void ClearSessionGrants() => _sessionGrants.Clear();

    public string? GetLastResolvedGroup(string requestId)
    {
        _requestGroupMap.TryRemove(requestId, out var group);
        return group;
    }

    public Task<ToolPermissionResult> CheckAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        var policy = ToolGroupPolicy.ResolveEffectivePolicy(group, _snapshot);

        if (policy == "off")
        {
            return Task.FromResult(ToolPermissionResult.Deny("Disabled in settings"));
        }

        if (policy == "always" || group == "meta")
        {
            return Task.FromResult(ToolPermissionResult.NotRequired(
                policy == "always"
                    ? ToolPermissionAuditMode.PolicyAlways
                    : ToolPermissionAuditMode.ToolExempt));
        }

        if (!ToolGroupPolicy.PerCallOnlyGroups.Contains(group) &&
            _sessionGrants.TryGetValue(group, out var granted) && granted)
        {
            return Task.FromResult(ToolPermissionResult.NotRequired(ToolPermissionAuditMode.SessionGrant));
        }

        return WaitForDecisionAsync(canonical, group, argumentsJson, ct);
    }

    public bool TryApplyDecision(string requestId, bool approved, bool rememberForSession = false, bool persistAsAlways = false)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            if (approved && (rememberForSession || persistAsAlways))
            {
                if (_requestGroupMap.TryGetValue(requestId, out var group))
                {
                    if (!ToolGroupPolicy.PerCallOnlyGroups.Contains(group))
                    {
                        _sessionGrants[group] = true;
                    }
                }
            }

            tcs.TrySetResult(approved);
            return true;
        }

        return false;
    }

    private async Task<ToolPermissionResult> WaitForDecisionAsync(
        string canonicalToolName,
        string group,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var runId = _currentRunIdAccessor() ?? "unknown";
        var reason = ToolGroupPolicy.BuildRedactedPurpose(canonicalToolName, argumentsJson);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        _requestGroupMap[requestId] = group;

        Requested?.Invoke(runId, new ToolRequestedPayload(
            RequestId: requestId,
            ToolName: canonicalToolName,
            Reason: reason,
            ArgumentsJson: argumentsJson));

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        bool approved;
        try
        {
            approved = await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(requestId, out _);
            _requestGroupMap.TryRemove(requestId, out _);
            Resolved?.Invoke(runId, new ToolDecisionPayload(requestId, canonicalToolName, false));
            return ToolPermissionResult.Deny("Cancelled");
        }

        Resolved?.Invoke(runId, new ToolDecisionPayload(requestId, canonicalToolName, approved));
        return approved
            ? ToolPermissionResult.Grant(auditMode: ToolPermissionAuditMode.ExplicitApproval)
            : ToolPermissionResult.Deny("Denied by user");
    }
}
