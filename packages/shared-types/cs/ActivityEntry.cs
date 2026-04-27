namespace Thaddeus.SharedTypes;

/// <summary>The kind of activity captured in the per-turn run log.</summary>
public enum ActivityKind
{
    /// <summary>A typed chat turn (user → assistant).</summary>
    ChatTurn,
    /// <summary>A voice turn (push-to-talk → STT → assistant → TTS).</summary>
    VoiceTurn,
    /// <summary>A user-invoked routine run (checklist walkthrough).</summary>
    Routine,
    /// <summary>System-level event (startup, shutdown, error).</summary>
    System,
}

/// <summary>Lifecycle state of an activity entry.</summary>
public enum ActivityStatus
{
    /// <summary>The activity is in flight.</summary>
    Running,
    /// <summary>Completed successfully.</summary>
    Ok,
    /// <summary>Cancelled by the user or shutdown.</summary>
    Cancelled,
    /// <summary>Terminated by an error.</summary>
    Failed,
}

/// <summary>
/// One entry in the activity log. The activity log is a per-runtime ring buffer
/// of recent turns and system events surfaced in the Activity UI.
/// </summary>
/// <param name="Id">Stable opaque entry id.</param>
/// <param name="Kind">Activity category — chat turn, voice turn, automation, system.</param>
/// <param name="Summary">Short human-readable summary (≤140 chars).</param>
/// <param name="Status">Lifecycle state. Updated as the activity progresses.</param>
/// <param name="StartedAt">UTC timestamp when the activity began.</param>
/// <param name="CompletedAt">UTC timestamp when the activity reached a terminal state. Null while running.</param>
/// <param name="ThreadId">Optional chat thread id this entry belongs to.</param>
/// <param name="Detail">Optional longer detail (error message, transcript, JSON blob).</param>
public sealed record ActivityEntry(
    string Id,
    ActivityKind Kind,
    string Summary,
    ActivityStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ThreadId,
    string? Detail);
