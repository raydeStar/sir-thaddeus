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
/// <param name="Sources">Optional structured sources the assistant cited
/// for this turn — e.g. web-search results surfaced as rich link cards
/// in the UI. Null on user messages and on assistant turns that didn't
/// call a citation-producing tool. Deserialized from each web_search
/// tool result's trailing <c>&lt;!-- SOURCES_JSON --&gt;</c> block and
/// merged across the turn.</param>
public sealed record ChatMessage(
    string Id,
    ChatRole Role,
    string Text,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChatMessageSource>? Sources = null);

/// <summary>
/// A citation surfaced with an assistant message — rendered as a rich
/// preview card in the chat UI (thumbnail + favicon + title + domain +
/// excerpt). All fields are optional except <see cref="Url"/>; the UI
/// degrades gracefully when e.g. the thumbnail is absent.
/// </summary>
/// <param name="Title">Human-readable title; falls back to the URL host.</param>
/// <param name="Url">Canonical URL the card links to.</param>
/// <param name="Domain">Lower-cased host (e.g. "nytimes.com"). Displayed
/// under the title and used for the favicon lookup.</param>
/// <param name="Excerpt">Short preview text, ≤ ~250 chars.</param>
/// <param name="Favicon">data-URL for the favicon when the extractor
/// captured one; otherwise null (the UI can fall back to a generic icon).</param>
/// <param name="Thumbnail">Absolute URL of a representative image
/// (og:image or inline) when available.</param>
/// <param name="PublishedAt">ISO-8601 timestamp when the source is a
/// dated article; null for undated pages.</param>
public sealed record ChatMessageSource(
    string Url,
    string? Title = null,
    string? Domain = null,
    string? Excerpt = null,
    string? Favicon = null,
    string? Thumbnail = null,
    string? PublishedAt = null);

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
