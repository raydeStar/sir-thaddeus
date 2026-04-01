using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Planning;

/// <summary>
/// Structured execution plan that the model must produce before any tool
/// is called. Makes agent intent auditable and gives smaller models a
/// concrete execution target.
/// </summary>
public sealed record TaskPlan
{
    /// <summary>High-level task kind (e.g. "web_lookup", "file_organization").</summary>
    public required string TaskKind { get; init; }

    /// <summary>The classified task lane from the lane router.</summary>
    public required TaskLane Lane { get; init; }

    /// <summary>Tool names the plan requires (must all be in the lane's shortlist).</summary>
    public required IReadOnlyList<string> RequiredTools { get; init; }

    /// <summary>Ordered steps the agent will execute.</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Condition under which execution should stop.</summary>
    public required string StopCondition { get; init; }

    /// <summary>What counts as a successful completion.</summary>
    public required string SuccessCriteria { get; init; }
}
