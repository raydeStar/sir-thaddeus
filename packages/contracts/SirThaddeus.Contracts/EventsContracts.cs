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

public sealed record RunCompletedPayload(
    string FinalText,
    int ToolLoopIterations,
    DeepDiveBriefingDto? Briefing = null);

public sealed record RunFailedPayload(
    string Error,
    bool IsCancelled);

