namespace SirThaddeus.DesktopRuntime.ViewModels;

/// <summary>
/// Lightweight snapshot of a completed chat session for history display.
/// </summary>
public sealed class ChatSessionSnapshot
{
    public string Title { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public List<ChatSessionMessage> Messages { get; set; } = [];

    public string TimestampDisplay => Timestamp.ToString("MMM d, h:mm tt");
    public int MessageCount => Messages?.Count ?? 0;

    public ChatSessionSnapshot() { }

    public ChatSessionSnapshot(string title, DateTime timestamp, List<ChatSessionMessage> messages)
    {
        Title = title;
        Timestamp = timestamp;
        Messages = messages;
    }
}

/// <summary>
/// Serializable message pair — role name + content text.
/// </summary>
public sealed class ChatSessionMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";

    public ChatSessionMessage() { }

    public ChatSessionMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }
}
