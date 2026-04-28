namespace SirThaddeus.Wiki;

public sealed record WikiRoot(
    string Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WikiFolder(
    string Id,
    string RootId,
    string? ParentFolderId,
    string Name,
    string Slug,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WikiPage(
    string Id,
    string RootId,
    string? FolderId,
    string Title,
    string Slug,
    string RelativePath,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Excerpt,
    int WordCount);

public sealed record WikiPageDocument(WikiPage Page, string Markdown);

public sealed record WikiRevision(
    string Id,
    string PageId,
    long Version,
    string Source,
    DateTimeOffset CreatedAt,
    string? Summary,
    string Markdown);

public sealed record WikiTree(
    WikiRoot Root,
    IReadOnlyList<WikiFolder> Folders,
    IReadOnlyList<WikiPage> Pages);

public sealed record WikiSearchResult(
    string RootId,
    string PageId,
    string Title,
    string Excerpt,
    string RelativePath,
    long Version);

public sealed record WikiIndexRebuildResult(
    string RootId,
    int PageCount,
    DateTimeOffset RebuiltAt);