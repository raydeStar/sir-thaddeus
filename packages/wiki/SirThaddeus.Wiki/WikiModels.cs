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
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt = null);

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
    int WordCount,
    DateTimeOffset? DeletedAt = null);

public sealed record WikiPageDocument(WikiPage Page, string Markdown);

public sealed record WikiPageReference(
    string PageId,
    string Title,
    string RelativePath);

public sealed record WikiPageGraph(
    IReadOnlyList<WikiPageReference> Links,
    IReadOnlyList<WikiPageReference> Backlinks,
    IReadOnlyList<string> Tags);

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

public sealed record WikiRootExport(
    string RootId,
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record WikiSearchResult(
    string RootId,
    string PageId,
    string Title,
    string Excerpt,
    string RelativePath,
    long Version);

public sealed record WikiTrashItem(
    string Id,
    string RootId,
    string Type,
    string Name,
    string RelativePath,
    DateTimeOffset DeletedAt,
    int FolderCount,
    int PageCount);

public sealed record WikiIndexRebuildResult(
    string RootId,
    int PageCount,
    DateTimeOffset RebuiltAt);

public sealed record WikiImportEntry(
    string SourcePath,
    string TargetRelativePath,
    string Title,
    string Status,
    string? Reason = null);

public sealed record WikiImportPreview(
    string RootId,
    int TotalMarkdownFiles,
    int NewCount,
    int ConflictCount,
    int InvalidCount,
    IReadOnlyList<WikiImportEntry> Entries);

public sealed record WikiImportOptions(string CollisionPolicy);

public sealed record WikiImportResult(
    string RootId,
    int CreatedCount,
    int OverwrittenCount,
    int SkippedCount,
    int InvalidCount,
    IReadOnlyList<WikiImportEntry> Entries);