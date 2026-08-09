namespace SirThaddeus.Agent;

/// <summary>
/// Optional capability for an orchestrator whose per-turn pipeline can make
/// deadline-aware decisions. The deadline limits workflow work; the caller's
/// cancellation token remains authoritative for explicit cancellation.
/// </summary>
public interface IWorkflowDeadlineAwareAgent
{
    /// <summary>Sets the absolute UTC deadline for the next serialized turn.</summary>
    void SetWorkflowDeadline(DateTimeOffset? deadlineUtc);
}
