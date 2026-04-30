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

    Task<WikiRoot?> RemoveRootAsync(
        string rootId,
        CancellationToken cancellationToken);

    Task<WikiRootExport?> ExportRootAsync(
        string rootId,
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

    Task<bool> DeleteFolderAsync(
        string rootId,
        string folderId,
        CancellationToken cancellationToken);

    Task<bool> RestoreFolderAsync(
        string rootId,
        string folderId,
        CancellationToken cancellationToken);

    Task<bool> PurgeFolderAsync(
        string rootId,
        string folderId,
        CancellationToken cancellationToken);

    Task<WikiPageDocument> CreatePageAsync(
        string rootId,
        string? folderId,
        string title,
        string markdown,
        CancellationToken cancellationToken);

    Task<WikiPageDocument?> GetPageAsync(string pageId, CancellationToken cancellationToken);

    Task<WikiPageGraph?> GetPageGraphAsync(string pageId, CancellationToken cancellationToken);

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

    Task<bool> DeletePageAsync(
        string pageId,
        CancellationToken cancellationToken);

    Task<WikiPageDocument?> RestorePageAsync(
        string pageId,
        CancellationToken cancellationToken);

    Task<bool> PurgePageAsync(
        string pageId,
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

    Task<IReadOnlyList<WikiTrashItem>> ListTrashAsync(
        string rootId,
        CancellationToken cancellationToken);

    Task<WikiIndexRebuildResult?> RebuildIndexAsync(
        string rootId,
        CancellationToken cancellationToken);

    Task<WikiImportPreview?> PreviewImportAsync(
        string rootId,
        byte[] zipBytes,
        CancellationToken cancellationToken);

    Task<WikiImportResult?> ImportRootAsync(
        string rootId,
        byte[] zipBytes,
        WikiImportOptions options,
        CancellationToken cancellationToken);
}