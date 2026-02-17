namespace SirThaddeus.DesktopRuntime.ViewModels;

/// <summary>
/// Lightweight snapshot of a completed chat session for history display.
/// Messages are stored as role/content pairs to avoid ViewModel coupling.
/// </summary>
public sealed record ChatSessionSnapshot(
    string                           Title,
    DateTime                         Timestamp,
    IReadOnlyList<ChatSessionMessage> Messages)
{
    public string TimestampDisplay => Timestamp.ToString("MMM d, h:mm tt");
    public int    MessageCount     => Messages.Count;
}

/// <summary>
/// Serializable message pair — role name + content text.
/// </summary>
public sealed record ChatSessionMessage(string Role, string Content);
