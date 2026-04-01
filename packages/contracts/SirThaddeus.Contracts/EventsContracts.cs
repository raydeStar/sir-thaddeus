namespace SirThaddeus.Contracts;

public static class RuntimeEventTypes
{
    public const string TokenDelta = "token.delta";
    public const string RunCompleted = "run.completed";
    public const string RunFailed = "run.failed";
    public const string ToolRequested = "tool.requested";
    public const string ToolApproved = "tool.approved";
    public const string ToolDenied = "tool.denied";
    public const string AuditAppended = "audit.appended";
    public const string ChecklistUpdated = "checklist.updated";
    public const string NarrationUpdated = "narration.updated";
    public const string ProgressEvent = "progress.event";
}

public sealed record RuntimeEventEnvelope(
    string EventType,
    string RunId,
    DateTimeOffset TimestampUtc,
    object Payload);

public sealed record TokenDeltaPayload(
    string Delta,
    int Sequence);

public sealed record ToolRequestedPayload(
    string RequestId,
    string ToolName,
    string Reason,
    string ArgumentsJson);

public sealed record ToolDecisionPayload(
    string RequestId,
    string ToolName,
    bool Approved);

public sealed record AssistantSourceCardPayload(
    string Title,
    string Url,
    string Domain,
    string Excerpt = "",
    string Favicon = "",
    string Thumbnail = "",
    string? PublishedAt = null);

public sealed record RunCompletedPayload(
    string FinalText,
    int ToolLoopIterations,
    int ToolCallsUsed,
    DeepDiveBriefingDto? Briefing = null,
    string? CompletionReason = null,
    string? ConfidenceBand = null,
    bool? RetryGateAllowed = null,
    string? RetryGateReason = null,
    IReadOnlyList<AssistantSourceCardPayload>? SourceCards = null,
    bool SuppressSourceCardsUi = false,
    string? PlanSummary = null);

public sealed record RunFailedPayload(
    string Error,
    bool IsCancelled);

public sealed record ChecklistItemPayload(
    string Id,
    string Title,
    string State,
    int Order,
    string? StatusNote = null);

public sealed record ChecklistUpdatedPayload(
    string TaskId,
    string CurrentPhase,
    IReadOnlyList<ChecklistItemPayload> Items);

public sealed record NarrationUpdatedPayload(
    string Message,
    string? Phase = null);

public sealed record ProgressEventPayload(
    string EventType,
    string Message,
    bool UserVisible,
    string? RelatedStepId = null,
    Dictionary<string, string>? Metadata = null);

