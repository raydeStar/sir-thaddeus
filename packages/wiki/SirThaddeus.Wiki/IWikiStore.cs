namespace SirThaddeus.Wiki;

public interface IWikiStore
{
    Task<IReadOnlyList<WikiRoot>> ListRootsAsync(CancellationToken cancellationToken);

    Task<WikiRoot> CreateRootAsync(
        string name,
        string? path,
        CancellationToken cancellationToken);

    Task<WikiRoot?> RenameRootAsync(
        string rootId,
        string name,
        CancellationToken cancellationToken);

    Task<WikiTree?> GetTreeAsync(string rootId, CancellationToken cancellationToken);

    Task<WikiFolder> CreateFolderAsync(
        string rootId,
        string name,
        string? parentFolderId,
        CancellationToken cancellationToken);

    Task<WikiFolder?> RenameFolderAsync(
        string rootId,
        string folderId,
        string name,
        CancellationToken cancellationToken);

    Task<WikiFolder?> MoveFolderAsync(
        string rootId,
        string folderId,
        string? parentFolderId,
        CancellationToken cancellationToken);

    Task<WikiPageDocument> CreatePageAsync(
        string rootId,
        string? folderId,
        string title,
        string markdown,
        CancellationToken cancellationToken);

    Task<WikiPageDocument?> GetPageAsync(string pageId, CancellationToken cancellationToken);

    Task<WikiPageDocument?> UpdatePageAsync(
        string pageId,
        string markdown,
        long? expectedVersion,
        string source,
        string? summary,
        CancellationToken cancellationToken);

    Task<WikiPageDocument?> RenamePageAsync(
        string pageId,
        string title,
        long? expectedVersion,
        CancellationToken cancellationToken);

    Task<WikiPageDocument?> MovePageAsync(
        string pageId,
        string? folderId,
        long? expectedVersion,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WikiRevision>> ListRevisionsAsync(
        string pageId,
        CancellationToken cancellationToken);

    Task<WikiPageDocument?> RestoreRevisionAsync(
        string pageId,
        string revisionId,
        long? expectedVersion,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WikiSearchResult>> SearchAsync(
        string? rootId,
        string query,
        CancellationToken cancellationToken);

    Task<WikiIndexRebuildResult?> RebuildIndexAsync(
        string rootId,
        CancellationToken cancellationToken);
}