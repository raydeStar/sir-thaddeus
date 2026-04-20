namespace Thaddeus.SharedTypes;

/// <summary>
/// Detailed payload of a <c>runtime.state</c> event. Mirrors
/// <c>packages/shared-schemas/runtime-state-event.schema.json</c>. All optional
/// substate fields default to null and are only populated when relevant.
/// </summary>
public sealed record RuntimeStateEvent
{
    /// <summary>The new top-level state.</summary>
    public required RuntimeState State { get; init; }

    /// <summary>ULID of the conversation turn, if any.</summary>
    public string? TurnId { get; init; }

    /// <summary>ULID of the thread, if any.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Currently executing tool, when in <see cref="RuntimeState.ExecutingTools"/>.</summary>
    public ActiveToolCallInfo? ActiveToolCall { get; init; }

    /// <summary>Pending permission, when in <see cref="RuntimeState.AwaitingPermission"/>.</summary>
    public PendingPermissionInfo? PendingPermission { get; init; }

    /// <summary>Number of TTS items waiting in the queue.</summary>
    public int? TtsQueueDepth { get; init; }

    /// <summary>Source of the input that produced the current turn.</summary>
    public InputSource? InputSource { get; init; }

    /// <summary>Last error, if state is <see cref="RuntimeState.Error"/>.</summary>
    public LastErrorInfo? LastError { get; init; }

    /// <summary>UTC timestamp of the state change.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>Substate detail describing a currently-executing tool call.</summary>
public sealed record ActiveToolCallInfo
{
    /// <summary>Stable identifier for the tool (matches a registered tool id).</summary>
    public required string ToolId { get; init; }

    /// <summary>When the tool call began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Plain-English summary suitable for activity surfaces.</summary>
    public required string HumanSummary { get; init; }
}

/// <summary>Substate detail describing a pending permission request.</summary>
public sealed record PendingPermissionInfo
{
    /// <summary>ULID of the permission request.</summary>
    public required string RequestId { get; init; }

    /// <summary>Capability being requested (e.g. <c>fs.read</c>).</summary>
    public required string Capability { get; init; }

    /// <summary>Plain-English explanation shown to the user.</summary>
    public required string HumanSummary { get; init; }
}

/// <summary>Substate detail describing the most recent surfaced error.</summary>
public sealed record LastErrorInfo
{
    /// <summary>Stable error code (see spec §29).</summary>
    public required string Code { get; init; }

    /// <summary>Plain-English summary suitable for the user.</summary>
    public required string HumanSummary { get; init; }

    /// <summary>Correlation ID for log lookup.</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>Origin of the input that triggered the current turn.</summary>
public enum InputSource
{
    /// <summary>Typed text.</summary>
    Text,

    /// <summary>Spoken voice.</summary>
    Voice,
}
