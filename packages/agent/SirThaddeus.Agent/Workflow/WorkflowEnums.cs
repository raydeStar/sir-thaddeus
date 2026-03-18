namespace SirThaddeus.Agent.Workflow;

public enum TaskComplexity
{
    Trivial,
    SimpleLookup,
    MultiStepResearch,
    ActionWorkflow,
    HighRiskApprovalGated
}

public enum ChecklistItemState
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped,
    Blocked
}

public enum TaskLifecycleState
{
    Received,
    Planning,
    ReadyToRun,
    Running,
    WaitingOnTool,
    Retrying,
    PartialReady,
    Finalizing,
    Completed,
    Failed,
    TimedOut,
    Cancelled
}

public enum ProgressTrigger
{
    TaskStarted,
    ChecklistInitialized,
    MilestoneReached,
    ContradictionDetected,
    RetryStarted,
    PartialAnswerReady,
    Finalizing,
    Completed,
    Failed,
    TimedOut,
    Cancelled
}

public enum CompletionReason
{
    SuccessHighConfidence,
    SuccessMediumConfidence,
    Timeout,
    ToolBudgetExhausted,
    RetryBudgetExhausted,
    BlockedByPolicy,
    Cancelled,
    Failed
}