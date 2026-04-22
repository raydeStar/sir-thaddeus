namespace Thaddeus.SharedTypes;

/// <summary>Role of a participant in a chat conversation.</summary>
public enum ChatRole
{
    /// <summary>The end-user.</summary>
    User,
    /// <summary>The assistant (LLM or stubbed reply).</summary>
    Assistant,
    /// <summary>System / orchestration message (rare).</summary>
    System,
}

/// <summary>A single message within a chat thread.</summary>
/// <param name="Id">Stable opaque message id (ULID-style string).</param>
/// <param name="Role">Who authored the message.</param>
/// <param name="Text">Plain-text content. Markdown is allowed; rendering is the UI's job.</param>
/// <param name="CreatedAt">UTC timestamp.</param>
public sealed record ChatMessage(
    string Id,
    ChatRole Role,
    string Text,
    DateTimeOffset CreatedAt);

/// <summary>A chat conversation comprising an ordered list of messages.</summary>
/// <param name="Id">Stable opaque thread id.</param>
/// <param name="Title">Human-readable title; auto-derived from first user turn if not set.</param>
/// <param name="CreatedAt">UTC timestamp of thread creation.</param>
/// <param name="UpdatedAt">UTC timestamp of last message append.</param>
/// <param name="Messages">Ordered messages, oldest first.</param>
/// <param name="Pinned">When true, the History UI surfaces the thread above unpinned ones.</param>
public sealed record ChatThread(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ChatMessage> Messages,
    bool Pinned = false);
