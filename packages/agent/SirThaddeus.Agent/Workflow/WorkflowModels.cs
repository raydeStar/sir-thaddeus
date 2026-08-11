namespace SirThaddeus.Agent.Workflow;

public sealed class TaskEnvelope
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public string UserRequest { get; init; } = string.Empty;
    public string Intent { get; init; } = string.Empty;
    public TaskComplexity Complexity { get; init; }
    public bool NeedsTools { get; init; }
    public bool ShowChecklist { get; init; }
    // 300s covers a 4B-class local model through a typical multi-step tool
    // loop (gatekeeper + primary + ~3 tool calls) including slow final
    // drafts. 2B models finish in 10-20s; leaving the ceiling this high
    // hurts nothing and stops premature "Cancelled" responses when the
    // model is just thinking slowly (a single 4B response can take 40s+).
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(300);
    public int MaxRetries { get; init; } = 1;
    public int MaxToolCalls { get; init; } = 8;
}

public sealed class ChecklistItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = string.Empty;
    public string? Description { get; set; }
    public ChecklistItemState State { get; set; } = ChecklistItemState.Pending;
    public int Order { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? StatusNote { get; set; }
}

public sealed class UserVisibleChecklist
{
    public string TaskId { get; init; } = string.Empty;
    public List<ChecklistItem> Items { get; init; } = [];
    public string CurrentPhase { get; set; } = "Planning";
}

public sealed class PlannedAction
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string StepId { get; init; } = string.Empty;
    public string ActionType { get; init; } = string.Empty;
    public string Instruction { get; init; } = string.Empty;
    public bool UserVisible { get; init; }
    public string? RetryStrategy { get; init; }
}

public sealed class EvidenceRecord
{
    public string SourceType { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string Summary { get; init; } = string.Empty;
    public double TrustScore { get; init; }
    public double RelevanceScore { get; init; }
    public bool SupportsCandidateAnswer { get; init; }
    public bool ContradictsCandidateAnswer { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed class ConfidenceSnapshot
{
    public double Score { get; init; }
    public string Band { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public bool ShouldRetry { get; init; }
    public string? RetryReason { get; init; }
}

public sealed class RetryGateDecision
{
    public bool IsAllowed { get; init; }
    public string ReasonCode { get; init; } = string.Empty;
    public string ReasonMessage { get; init; } = string.Empty;
    public int RemainingRetries { get; init; }
    public int RemainingToolCalls { get; init; }
    public int RemainingTimeMs { get; init; }
}

public sealed class ProgressEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string EventType { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool UserVisible { get; init; }
    public string? RelatedStepId { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class ToolExecutionResult
{
    public bool Success { get; init; }
    public string? OutputSummary { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class TaskRunState
{
    public TaskEnvelope Envelope { get; init; } = default!;
    public UserVisibleChecklist Checklist { get; init; } = new();
    public List<ProgressEvent> Events { get; init; } = [];
    public List<EvidenceRecord> Evidence { get; init; } = [];
    public int ToolCallsUsed { get; set; }
    /// <summary>
    /// True once the current attempt has successfully executed a tool whose
    /// declared effect may mutate external state. Cross-attempt retries must
    /// not replay that work automatically.
    /// </summary>
    public bool HasSuccessfulMutation { get; set; }
    public int RetriesUsed { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public ConfidenceSnapshot? LatestConfidence { get; set; }
    public string? DraftAnswer { get; set; }
    public TaskLifecycleState RuntimeState { get; set; } = TaskLifecycleState.Received;
    public CompletionReason? CompletionReason { get; set; }
    public RetryGateDecision? LastRetryGateDecision { get; set; }
    public string? LastPublishedChecklistStamp { get; set; }
    public string? LastPublishedNarration { get; set; }
}

public sealed class FinalTaskResult
{
    public string TaskId { get; init; } = string.Empty;
    public string FinalText { get; init; } = string.Empty;
    public bool Success { get; init; }
    public CompletionReason CompletionReason { get; init; }
    public ConfidenceSnapshot? Confidence { get; init; }
    public IReadOnlyList<ChecklistItem> ChecklistItems { get; init; } = [];
    public IReadOnlyList<ProgressEvent> Events { get; init; } = [];
}
