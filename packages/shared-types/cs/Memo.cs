namespace Thaddeus.SharedTypes;

/// <summary>
/// A user-curated memo the agent should remember (Phase 7.1).
/// Memos are simple notes — title, markdown body, optional tags,
/// optional pinning. They do not have versioning or revisions yet.
/// </summary>
/// <param name="Id">Stable identifier, prefixed <c>mem_</c>.</param>
/// <param name="Title">Short label for the memo.</param>
/// <param name="Body">Markdown body. May be empty.</param>
/// <param name="Tags">Free-form tag list, lowercased on persist.</param>
/// <param name="Pinned">Whether the memo should sort to the top of lists.</param>
/// <param name="CreatedAt">When the memo was first created.</param>
/// <param name="UpdatedAt">When the memo was last modified.</param>
public sealed record Memo(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<string> Tags,
    bool Pinned,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
