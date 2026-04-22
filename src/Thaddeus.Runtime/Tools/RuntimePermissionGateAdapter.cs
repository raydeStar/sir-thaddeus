using AgentGate = SirThaddeus.Agent.IToolPermissionGate;
using AgentResult = SirThaddeus.Agent.ToolPermissionResult;
using AgentAuditMode = SirThaddeus.Agent.ToolPermissionAuditMode;

namespace Thaddeus.Runtime.Tools;

/// <summary>
/// Adapts the runtime's <see cref="ToolPermissionGate"/> to the agent
/// package's <see cref="AgentGate"/> port. Pipeline steps (in the agent
/// package) know nothing about the runtime's modal prompt / session cache /
/// thread allowlist mechanics — they just ask "is this tool call allowed?"
/// through the port, and this adapter wires the answer through the
/// runtime's full gating machinery.
///
/// <para>One adapter instance per turn: the adapter captures the current
/// <c>threadId</c> + <c>turnId</c> at construction so the agent-side
/// interface stays context-free. The runtime facade builds one of these
/// before invoking the pipeline for each user message.</para>
/// </summary>
public sealed class RuntimePermissionGateAdapter : AgentGate
{
    private readonly ToolPermissionGate _gate;
    private readonly string _threadId;
    private readonly string _turnId;

    public RuntimePermissionGateAdapter(ToolPermissionGate gate, string threadId, string turnId)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _threadId = !string.IsNullOrWhiteSpace(threadId)
            ? threadId
            : throw new ArgumentException("threadId is required.", nameof(threadId));
        _turnId = !string.IsNullOrWhiteSpace(turnId)
            ? turnId
            : throw new ArgumentException("turnId is required.", nameof(turnId));
    }

    public async Task<AgentResult> CheckAsync(
        string toolName,
        string argumentsJson,
        CancellationToken ct)
    {
        var decision = await _gate
            .DecideAsync(toolName, argumentsJson, _threadId, _turnId, ct)
            .ConfigureAwait(false);

        return decision switch
        {
            // Allow covers all green paths — Safe group, policy=always,
            // session cache hit, automation allowlist hit, or user-approved
            // modal. We can't tell them apart from the enum; downstream
            // steps just need the go/no-go bit, so we surface a generic
            // session-grant audit mode.
            ToolPermissionDecision.Allow => AgentResult.Grant(auditMode: AgentAuditMode.SessionGrant),

            // Deny covers policy=off and user-denied modal. Reason string
            // is intentionally generic — the runtime logs the specific
            // cause; the pipeline just needs a short deny reason.
            ToolPermissionDecision.Deny => AgentResult.Deny("Tool call blocked by permission policy."),

            _ => AgentResult.Deny("Unknown permission gate decision."),
        };
    }
}
