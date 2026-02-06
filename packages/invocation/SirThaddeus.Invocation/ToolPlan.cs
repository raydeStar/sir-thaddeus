using SirThaddeus.ToolRunner;

namespace SirThaddeus.Invocation;

/// <summary>
/// Represents a planned sequence of tool calls to execute.
/// </summary>
public sealed record ToolPlan
{
    /// <summary>
    /// Human-readable description of what this plan will do.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The ordered list of tool calls to execute.
    /// </summary>
    public required IReadOnlyList<ToolCall> Steps { get; init; }

    /// <summary>
    /// Human-readable preview strings for each step.
    /// </summary>
    public required IReadOnlyList<string> StepPreviews { get; init; }

    /// <summary>
    /// Whether this plan requires any tool execution.
    /// Some commands (like spec validation) don't need tools.
    /// </summary>
    public bool RequiresToolExecution => Steps.Count > 0;

    /// <summary>
    /// Creates an empty plan (no tool calls).
    /// </summary>
    public static ToolPlan Empty(string description) => new()
    {
        Description = description,
        Steps = Array.Empty<ToolCall>(),
        StepPreviews = Array.Empty<string>()
    };

    /// <summary>
    /// Creates a single-step plan.
    /// </summary>
    public static ToolPlan SingleStep(ToolCall call, string preview) => new()
    {
        Description = preview,
        Steps = new[] { call },
        StepPreviews = new[] { preview }
    };
}

/// <summary>
/// Result of planning a command.
/// </summary>
public sealed record PlanResult
{
    /// <summary>
    /// Whether planning succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The plan if successful.
    /// </summary>
    public ToolPlan? Plan { get; init; }

    /// <summary>
    /// Error message if planning failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Additional output for non-tool commands (like spec explain).
    /// </summary>
    public string? DirectOutput { get; init; }

    public static PlanResult Ok(ToolPlan plan) => new() { Success = true, Plan = plan };
    public static PlanResult Fail(string error) => new() { Success = false, Error = error };
    public static PlanResult Direct(string output) => new() 
    { 
        Success = true, 
        Plan = ToolPlan.Empty("Direct output"),
        DirectOutput = output 
    };
}
