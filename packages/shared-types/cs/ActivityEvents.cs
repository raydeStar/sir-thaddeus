namespace Thaddeus.SharedTypes;

/// <summary>
/// Event-bus / WebSocket type constants for activity-log changes. Subscribers see
/// the full <see cref="ActivityEntry"/> as the payload of a
/// <see cref="RuntimeEvent{T}"/> with one of these <c>type</c> values.
/// </summary>
public static class ActivityEvents
{
    /// <summary>A new entry was appended to the activity log.</summary>
    public const string Appended = "activity.appended";
    /// <summary>An existing entry was updated (status, completion, detail).</summary>
    public const string Updated = "activity.updated";
}
