namespace SirThaddeus.Agent;

/// <summary>
/// Headless-friendly superset of <see cref="IAgentOrchestrator"/>. The CLI
/// and harness need a couple of concrete operations beyond
/// request/response — most importantly, a way to seed prior history for
/// workflow runs and to reset between iterations. Keeping these on an
/// interface means wiring sites (REST endpoints, workflow coordinator,
/// <c>TimeBudgetedAgentOrchestrator</c> decorator) don't depend on a
/// concrete type. Today only
/// <see cref="Pipeline.PipelineBackedAgentOrchestrator"/> implements this —
/// the legacy monolithic orchestrator was retired after pipeline parity
/// reached the harness bar.
///
/// <para>Keep this interface as small as practical — anything the UI
/// runtime doesn't need stays off it.</para>
/// </summary>
public interface IHeadlessAgent : IAgentOrchestrator
{
    /// <summary>
    /// Clears internal conversation history. Used between harness test
    /// runs, between workflow iterations, and after explicit "start new
    /// conversation" commands.
    /// </summary>
    void ResetConversation();

    /// <summary>
    /// Pre-populates the conversation history with prior turns. Used by
    /// workflow runs that replay a transcript before making their next
    /// call. Roles should be one of <c>"user"</c>, <c>"assistant"</c>,
    /// <c>"system"</c>; anything else is silently dropped.
    /// </summary>
    void SeedHistory(IEnumerable<(string Role, string Content)> messages);

    /// <summary>
    /// Reports the number of MCP tools the agent can see. Used by
    /// diagnostic endpoints + harness startup checks to confirm the MCP
    /// server is reachable before running test suites. Non-throwing —
    /// returns 0 if the MCP layer is unreachable.
    /// </summary>
    Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default);
}
