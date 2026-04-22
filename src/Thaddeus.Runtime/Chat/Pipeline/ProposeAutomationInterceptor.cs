using SirThaddeus.Agent.Pipeline;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Tools;

namespace Thaddeus.Runtime.Chat.Pipeline;

/// <summary>
/// Runtime interceptor that claims <c>propose_automation</c> calls from
/// <see cref="ToolLoopStep"/> and turns them into UI events instead of
/// actual MCP invocations. The underlying work is already implemented by
/// <see cref="ProposeAutomationTool.HandleAsync"/>; this adapter is the
/// seam that wires it into the new pipeline without leaking
/// runtime-specific types (<see cref="ChatTurnPublisher"/>,
/// <see cref="ToolPermissionGate"/>) into the agent package.
///
/// <para>Defense-in-depth: the virtual tool is supposed to be filtered
/// out of the advertised list during an automation run (the facade
/// passes <c>includeProposeAutomation: false</c> in that case), but small
/// local models have been observed to emit the call from memory anyway.
/// We reject it here with a deterministic error so the UI never shows a
/// proposal card mid-run.</para>
/// </summary>
public sealed class ProposeAutomationInterceptor : IToolCallInterceptor
{
    private readonly ChatTurnPublisher _publisher;
    private readonly ToolPermissionGate _gate;

    public ProposeAutomationInterceptor(ChatTurnPublisher publisher, ToolPermissionGate gate)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public async Task<ToolCallOutcome?> TryInterceptAsync(
        TurnContext context,
        string toolName,
        string argumentsJson,
        string activityId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(toolName, ProposeAutomationTool.ToolName, StringComparison.OrdinalIgnoreCase))
            return null;

        if (_gate.IsAutomationRunThread(context.ThreadId))
        {
            return new ToolCallOutcome(
                ResultText: "Error: propose_automation is not available while an automation is running.",
                Ok: false,
                Error: "propose_automation_blocked_in_run");
        }

        var (summary, error) = await ProposeAutomationTool
            .HandleAsync(argumentsJson, context.ThreadId, context.MessageId, activityId,
                         _publisher, context.UserText, cancellationToken)
            .ConfigureAwait(false);

        return new ToolCallOutcome(summary, Ok: error is null, Error: error);
    }
}
