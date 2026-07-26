namespace Thaddeus.SharedTypes;

public enum WorkPlanCapability
{
    Context,
    Research,
    Compose,
    DurableOutput,
    Verify,
    General,
}

public enum WorkPlanRisk
{
    Low,
    Medium,
    High,
}

public enum WorkPlanStepStatus
{
    Pending,
    Active,
    Done,
    Skipped,
    Blocked,
}

/// <summary>
/// A user-visible, versioned intent plan. Capabilities are deliberately
/// abstract: the approved plan constrains outcomes without bypassing normal
/// tool policy, permission checks, or model/tool validation.
/// </summary>
public sealed record WorkPlan(
    string PlanId,
    long Version,
    string Intent,
    IReadOnlyList<WorkPlanStep> Steps,
    WorkPlanRisk Risk,
    string RiskSummary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WorkPlanStep(
    string StepId,
    string Label,
    WorkPlanCapability Capability,
    WorkPlanRisk Risk,
    bool RequiresPermission,
    WorkPlanStepStatus Status);
