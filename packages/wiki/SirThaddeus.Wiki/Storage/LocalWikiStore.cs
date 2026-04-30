using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SirThaddeus.Wiki.Storage;

public sealed class LocalWikiStore : IWikiStore, IDisposable
{
    private const string RegistryFileName = "wiki-registry.sqlite";
    private const string RootDatabaseFileName = "wiki.sqlite";
    private readonly string _libraryDirectory;
    private readonly string _registryPath;
    private readonly ILogger<LocalWikiStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _rootLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pageLocks = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _disposed;

    public LocalWikiStore(string libraryDirectory, ILogger<LocalWikiStore> logger)
    {
        if (string.IsNullOrWhiteSpace(libraryDirectory))
        {
            throw new ArgumentException("Wiki library directory is required.", nameof(libraryDirectory));
        }

        _libraryDirectory = Path.GetFullPath(libraryDirectory);
        _registryPath = Path.Combine(_libraryDirectory, ".sir-thaddeus", RegistryFileName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string LibraryDirectory => _libraryDirectory;

    public async Task<IReadOnlyList<WikiRoot>> ListRootsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenRegistryAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "select id, name, path, created_at, updated_at from roots order by updated_at desc";
        var roots = new List<WikiRoot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            roots.Add(ReadRoot(reader));
        }
        return roots;
    }

    public async Task<WikiRoot> CreateRootAsync(string name, string? path, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var normalizedName = NormalizeName(name, "Untitled Wiki");
        var rootPath = ResolveRootPath(normalizedName, path);
        var now = DateTimeOffset.UtcNow;
        var root = new WikiRoot(
            NewId("root"),
            normalizedName,
            rootPath,
            now,
            now);

        var gate = LockForRoot(root.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(root.Path);
            await using var connection = await OpenRegistryAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                insert into roots (id, name, path, created_at, updated_at)
                values ($id, $name, $path, $createdAt, $updatedAt)
                """;
            Add(command, "$id", root.Id);
            Add(command, "$name", root.Name);
            Add(command, "$path", root.Path);
            Add(command, "$createdAt", Format(root.CreatedAt));
            Add(command, "$updatedAt", Format(root.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        _logger.LogInformation("wiki.root.create id={RootId} path={RootPath}", root.Id, root.Path);
        return root;
    }

    public async Task<WikiRoot?> RenameRootAsync(string rootId, string name, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var current = await GetRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (current is null) return null;

        var normalizedName = NormalizeName(name, "Untitled Wiki");
        if (string.Equals(normalizedName, current.Name, StringComparison.Ordinal))
        {
            return current;
        }

        var updated = current with
        {
            Name = normalizedName,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var gate = LockForRoot(current.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenRegistryAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                update roots
                set name = $name,
                    updated_at = $updatedAt
                where id = $id
                """;
            Add(command, "$id", updated.Id);
            Add(command, "$name", updated.Name);
            Add(command, "$updatedAt", Format(updated.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        return updated;
    }

    public async Task<WikiRoot?> RemoveRootAsync(string rootId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var current = await GetRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (current is null) return null;

        var gate = LockForRoot(current.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenRegistryAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "delete from roots where id = $id";
            Add(command, "$id", current.Id);
            var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (removed == 0) return null;
        }
        finally
        {
            gate.Release();
        }

        _logger.LogInformation("wiki.root.remove id={RootId} path={RootPath}", current.Id, current.Path);
        return current;
    }

    public async Task<WikiTree?> GetTreeAsync(string rootId, CancellationToken cancellationToken)
    {
        var root = await GetRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (root is null) return null;

        await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
        var folders = await ListFoldersAsync(connection, cancellationToken).ConfigureAwait(false);
        var pages = await ListPagesAsync(connection, cancellationToken).ConfigureAwait(false);
        return new WikiTree(root, folders, pages);
    }

    public async Task<WikiFolder> CreateFolderAsync(string rootId, string name, string? parentFolderId, CancellationToken cancellationToken)
    {
        var root = await RequireRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var normalizedName = NormalizeName(name, "Untitled Folder");
        var gate = LockForRoot(root.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(parentFolderId))
            {
                var parent = await GetFolderAsync(connection, parentFolderId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Folder '{parentFolderId}' not found.");
                if (!string.Equals(parent.RootId, root.Id, StringComparison.Ordinal))
                {
                    throw new WikiPathException("Folder belongs to a different wiki root.");
                }
            }

            var now = DateTimeOffset.UtcNow;
            var slug = await UniqueFolderSlugAsync(connection, parentFolderId, Slugify(normalizedName), cancellationToken).ConfigureAwait(false);
            var sortOrder = await NextFolderSortOrderAsync(connection, parentFolderId, cancellationToken).ConfigureAwait(false);
            var folder = new WikiFolder(
                NewId("folder"),
                root.Id,
                string.IsNullOrWhiteSpace(parentFolderId) ? null : parentFolderId,
                normalizedName,
                slug,
                sortOrder,
                now,
                now);

            using var command = connection.CreateCommand();
            command.CommandText = """
                insert into folders (id, root_id, parent_folder_id, name, slug, sort_order, created_at, updated_at)
                values ($id, $rootId, $parentFolderId, $name, $slug, $sortOrder, $createdAt, $updatedAt)
                """;
            Add(command, "$id", folder.Id);
            Add(command, "$rootId", folder.RootId);
            AddNullable(command, "$parentFolderId", folder.ParentFolderId);
            Add(command, "$name", folder.Name);
            Add(command, "$slug", folder.Slug);
            Add(command, "$sortOrder", folder.SortOrder);
            Add(command, "$createdAt", Format(folder.CreatedAt));
            Add(command, "$updatedAt", Format(folder.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return folder;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WikiFolder?> RenameFolderAsync(
        string rootId,
        string folderId,
        string name,
        CancellationToken cancellationToken)
    {
        var root = await RequireRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var normalizedName = NormalizeName(name, "Untitled Folder");
        var gate = LockForRoot(root.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            var current = await GetFolderAsync(connection, folderId, cancellationToken).ConfigureAwait(false);
            if (current is null) return null;
            if (!string.Equals(current.RootId, root.Id, StringComparison.Ordinal))
            {
                throw new WikiPathException("Folder belongs to a different wiki root.");
            }

            if (string.Equals(normalizedName, current.Name, StringComparison.Ordinal))
            {
                return current;
            }

            var now = DateTimeOffset.UtcNow;
            var existingSlugs = await ExistingSlugsAsync(connection, "folders", "parent_folder_id", current.ParentFolderId, cancellationToken).ConfigureAwait(false);
            existingSlugs.Remove(current.Slug);
            var renamed = current with
            {
                Name = normalizedName,
                Slug = UniqueSlug(Slugify(normalizedName), existingSlugs),
                UpdatedAt = now,
            };

            var folders = await ListFoldersAsync(connection, cancellationToken).ConfigureAwait(false);
            var affectedFolderIds = DescendantFolderIds(folders, current.Id);
            var affectedPages = (await ListPagesAsync(connection, cancellationToken).ConfigureAwait(false))
                .Where(page => page.FolderId is not null && affectedFolderIds.Contains(page.FolderId))
                .ToArray();
            var pageBodies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var page in affectedPages)
            {
                pageBodies[page.Id] = await ReadPageBodyAsync(root, page, cancellationToken).ConfigureAwait(false);
            }

            await UpdateFolderAsync(connection, renamed, cancellationToken).ConfigureAwait(false);
            foreach (var page in affectedPages)
            {
                var updatedPage = page with
                {
                    RelativePath = await BuildPageRelativePathAsync(connection, page.FolderId, page.Slug, cancellationToken).ConfigureAwait(false),
                    UpdatedAt = now,
                };
                var oldPath = ResolvePagePath(root, page.RelativePath);
                var newPath = ResolvePagePath(root, updatedPage.RelativePath);
                await WritePageFileAsync(root, updatedPage, pageBodies[page.Id], cancellationToken).ConfigureAwait(false);
                if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
                {
                    File.Delete(oldPath);
                    DeleteEmptyDirectoriesUpToRoot(root.Path, Path.GetDirectoryName(oldPath));
                }

                await UpdatePageMetadataAsync(connection, updatedPage, cancellationToken).ConfigureAwait(false);
            }

            return renamed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WikiFolder?> MoveFolderAsync(
        string rootId,
        string folderId,
        string? parentFolderId,
        CancellationToken cancellationToken)
    {
        var root = await RequireRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var targetParentFolderId = string.IsNullOrWhiteSpace(parentFolderId) ? null : parentFolderId;
        var gate = LockForRoot(root.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            var current = await GetFolderAsync(connection, folderId, cancellationToken).ConfigureAwait(false);
            if (current is null) return null;
            if (!string.Equals(current.RootId, root.Id, StringComparison.Ordinal))
            {
                throw new WikiPathException("Folder belongs to a different wiki root.");
            }

            if (string.Equals(current.ParentFolderId, targetParentFolderId, StringComparison.Ordinal))
            {
                return current;
            }

            var folders = await ListFoldersAsync(connection, cancellationToken).ConfigureAwait(false);
            var affectedFolderIds = DescendantFolderIds(folders, current.Id);
            if (string.Equals(targetParentFolderId, current.Id, StringComparison.Ordinal) ||
                (targetParentFolderId is not null && affectedFolderIds.Contains(targetParentFolderId)))
            {
                throw new WikiPathException("Folder cannot be moved into itself or one of its descendants.");
            }

            if (!string.IsNullOrWhiteSpace(targetParentFolderId))
            {
                var targetParent = folders.FirstOrDefault(folder => string.Equals(folder.Id, targetParentFolderId, StringComparison.Ordinal))
                    ?? throw new KeyNotFoundException($"Folder '{targetParentFolderId}' not found.");
                if (!string.Equals(targetParent.RootId, root.Id, StringComparison.Ordinal))
                {
                    throw new WikiPathException("Folder belongs to a different wiki root.");
                }
            }

            var now = DateTimeOffset.UtcNow;
            var existingSlugs = await ExistingSlugsAsync(connection, "folders", "parent_folder_id", targetParentFolderId, cancellationToken).ConfigureAwait(false);
            existingSlugs.Remove(current.Slug);
            var moved = current with
            {
                ParentFolderId = targetParentFolderId,
                Slug = UniqueSlug(current.Slug, existingSlugs),
                SortOrder = await NextFolderSortOrderAsync(connection, targetParentFolderId, cancellationToken).ConfigureAwait(false),
                UpdatedAt = now,
            };

            var affectedPages = (await ListPagesAsync(connection, cancellationToken).ConfigureAwait(false))
                .Where(page => page.FolderId is not null && affectedFolderIds.Contains(page.FolderId))
                .ToArray();
            var pageBodies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var page in affectedPages)
            {
                pageBodies[page.Id] = await ReadPageBodyAsync(root, page, cancellationToken).ConfigureAwait(false);
            }

            await UpdateFolderAsync(connection, moved, cancellationToken).ConfigureAwait(false);
            foreach (var page in affectedPages)
            {
                var updatedPage = page with
                {
                    RelativePath = await BuildPageRelativePathAsync(connection, page.FolderId, page.Slug, cancellationToken).ConfigureAwait(false),
                    UpdatedAt = now,
                };
                var oldPath = ResolvePagePath(root, page.RelativePath);
                var newPath = ResolvePagePath(root, updatedPage.RelativePath);
                await WritePageFileAsync(root, updatedPage, pageBodies[page.Id], cancellationToken).ConfigureAwait(false);
                if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
                {
                    File.Delete(oldPath);
                    DeleteEmptyDirectoriesUpToRoot(root.Path, Path.GetDirectoryName(oldPath));
                }

                await UpdatePageMetadataAsync(connection, updatedPage, cancellationToken).ConfigureAwait(false);
            }

            return moved;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteFolderAsync(string rootId, string folderId, CancellationToken cancellationToken)
    {
        var root = await RequireRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var gate = LockForRoot(root.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            var current = await GetFolderAsync(connection, folderId, cancellationToken).ConfigureAwait(false);
            if (current is null) return false;
            if (!string.Equals(current.RootId, root.Id, StringComparison.Ordinal))
            {
                throw new WikiPathException("Folder belongs to a different wiki root.");
            }

            var folders = await ListFoldersAsync(connection, cancellationToken).ConfigureAwait(false);
            var affectedFolderIds = DescendantFolderIds(folders, current.Id);
            var affectedPages = (await ListPagesAsync(connection, cancellationToken).ConfigureAwait(false))
                .Where(page => page.FolderId is not null && affectedFolderIds.Contains(page.FolderId))
                .ToArray();
            var deletedAt = DateTimeOffset.UtcNow;

            foreach (var page in affectedPages)
            {
                await SoftDeletePageAsync(connection, page.Id, deletedAt, cancellationToken).ConfigureAwait(false);
            }

            foreach (var id in affectedFolderIds)
            {
                await SoftDeleteFolderAsync(connection, id, deletedAt, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RestoreFolderAsync(string rootId, string folderId, CancellationToken cancellationToken)
    {
        var root = await RequireRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var gate = LockForRoot(root.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            var current = await GetFolderAsync(connection, folderId, cancellationToken, includeDeleted: true).ConfigureAwait(false);
            if (current is null || current.DeletedAt is null) return false;
            if (!string.Equals(current.RootId, root.Id, StringComparison.Ordinal))
            {
                throw new WikiPathException("Folder belongs to a different wiki root.");
            }

            if (!string.IsNullOrWhiteSpace(current.ParentFolderId))
            {
                var parent = await GetFolderAsync(connection, current.ParentFolderId, cancellationToken, includeDeleted: true).ConfigureAwait(false);
                if (parent?.DeletedAt is not null)
                {
                    throw new WikiPathException("Restore the parent folder before restoring this folder.");
                }
            }

            var folders = await ListFoldersAsync(connection, cancellationToken, includeDeleted: true).ConfigureAwait(false);
            var affectedFolderIds = DescendantFolderIds(folders, current.Id);
            var affectedPages = (await ListPagesAsync(connection, cancellationToken, includeDeleted: true).ConfigureAwait(false))
                .Where(page => page.FolderId is not null && affectedFolderIds.Contains(page.FolderId))
                .ToArray();
            var restoredAt = DateTimeOffset.UtcNow;

            foreach (var id in affectedFolderIds)
            {
                await RestoreFolderRowAsync(connection, id, restoredAt, cancellationToken).ConfigureAwait(false);
            }

            foreach (var page in affectedPages)
            {
                await RestorePageRowAsync(connection, page.Id, restoredAt, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> PurgeFolderAsync(string rootId, string folderId, CancellationToken cancellationToken)
    {
        var root = await RequireRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var gate = LockForRoot(root.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            var current = await GetFolderAsync(connection, folderId, cancellationToken, includeDeleted: true).ConfigureAwait(false);
            if (current is null) return false;
            if (!string.Equals(current.RootId, root.Id, StringComparison.Ordinal))
            {
                throw new WikiPathException("Folder belongs to a different wiki root.");
            }

            var folders = await ListFoldersAsync(connection, cancellationToken, includeDeleted: true).ConfigureAwait(false);
            var affectedFolderIds = DescendantFolderIds(folders, current.Id);
            var affectedPages = (await ListPagesAsync(connection, cancellationToken, includeDeleted: true).ConfigureAwait(false))
                .Where(page => page.FolderId is not null && affectedFolderIds.Contains(page.FolderId))
                .ToArray();

            foreach (var page in affectedPages)
            {
                await PurgePageAsync(connection, root, page, cancellationToken).ConfigureAwait(false);
            }

            foreach (var id in affectedFolderIds)
            {
                using var deleteFolder = connection.CreateCommand();
                deleteFolder.CommandText = "delete from folders where id = $id";
                Add(deleteFolder, "$id", id);
                await deleteFolder.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WikiPageDocument> CreatePageAsync(string rootId, string? folderId, string title, string markdown, CancellationToken cancellationToken)
    {
        var root = await RequireRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var normalizedTitle = NormalizeName(title, "Untitled Page");
        var gate = LockForRoot(root.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(folderId))
            {
                var folder = await GetFolderAsync(connection, folderId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Folder '{folderId}' not found.");
                if (!string.Equals(folder.RootId, root.Id, StringComparison.Ordinal))
                {
                    throw new WikiPathException("Folder belongs to a different wiki root.");
                }
            }

            var now = DateTimeOffset.UtcNow;
            var slug = await UniquePageSlugAsync(connection, folderId, Slugify(normalizedTitle), cancellationToken).ConfigureAwait(false);
            var relativePath = await BuildPageRelativePathAsync(connection, folderId, slug, cancellationToken).ConfigureAwait(false);
            var page = new WikiPage(
                NewId("page"),
                root.Id,
                string.IsNullOrWhiteSpace(folderId) ? null : folderId,
                normalizedTitle,
                slug,
                relativePath,
                Version: 1,
                CreatedAt: now,
                UpdatedAt: now,
                Excerpt: Excerpt(markdown),
                WordCount: CountWords(markdown));

            await WritePageFileAsync(root, page, markdown, cancellationToken).ConfigureAwait(false);
            await InsertPageAsync(connection, page, cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(connection, page.Id, page.Version, "user", now, "Created page", markdown, cancellationToken).ConfigureAwait(false);
            return new WikiPageDocument(page, markdown ?? string.Empty);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WikiPageDocument?> GetPageAsync(string pageId, CancellationToken cancellationToken)
    {
        var located = await FindPageAsync(pageId, cancellationToken).ConfigureAwait(false);
        if (located is null) return null;

        var markdown = await ReadPageBodyAsync(located.Value.Root, located.Value.Page, cancellationToken).ConfigureAwait(false);
        return new WikiPageDocument(located.Value.Page, markdown);
    }

    public async Task<WikiPageDocument?> UpdatePageAsync(
        string pageId,
        string markdown,
        long? expectedVersion,
        string source,
        string? summary,
        CancellationToken cancellationToken)
    {
        var gate = LockForPage(pageId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var located = await FindPageAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (located is null) return null;
            var root = located.Value.Root;
            var current = located.Value.Page;
            if (expectedVersion.HasValue && expectedVersion.Value != current.Version)
            {
                throw new WikiVersionConflictException(pageId, expectedVersion.Value, current.Version);
            }

            var now = DateTimeOffset.UtcNow;
            var updated = current with
            {
                Version = current.Version + 1,
                UpdatedAt = now,
                Excerpt = Excerpt(markdown),
                WordCount = CountWords(markdown),
            };

            await WritePageFileAsync(root, updated, markdown, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            await UpdatePageMetadataAsync(connection, updated, cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection,
                updated.Id,
                updated.Version,
                NormalizeRevisionSource(source),
                now,
                summary,
                markdown,
                cancellationToken).ConfigureAwait(false);
            return new WikiPageDocument(updated, markdown ?? string.Empty);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WikiPageDocument?> RenamePageAsync(
        string pageId,
        string title,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var gate = LockForPage(pageId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var located = await FindPageAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (located is null) return null;
            var root = located.Value.Root;
            var current = located.Value.Page;
            if (expectedVersion.HasValue && expectedVersion.Value != current.Version)
            {
                throw new WikiVersionConflictException(pageId, expectedVersion.Value, current.Version);
            }

            var normalizedTitle = NormalizeName(title, "Untitled Page");
            if (string.Equals(normalizedTitle, current.Title, StringComparison.Ordinal))
            {
                var currentMarkdown = await ReadPageBodyAsync(root, current, cancellationToken).ConfigureAwait(false);
                return new WikiPageDocument(current, currentMarkdown);
            }

            var markdown = await ReadPageBodyAsync(root, current, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            var existingSlugs = await ExistingSlugsAsync(connection, "pages", "folder_id", current.FolderId, cancellationToken).ConfigureAwait(false);
            existingSlugs.Remove(current.Slug);
            var slug = UniqueSlug(Slugify(normalizedTitle), existingSlugs);
            var relativePath = await BuildPageRelativePathAsync(connection, current.FolderId, slug, cancellationToken).ConfigureAwait(false);
            var updated = current with
            {
                Title = normalizedTitle,
                Slug = slug,
                RelativePath = relativePath,
                Version = current.Version + 1,
                UpdatedAt = now,
            };

            var oldPath = ResolvePagePath(root, current.RelativePath);
            var newPath = ResolvePagePath(root, updated.RelativePath);
            await WritePageFileAsync(root, updated, markdown, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            await UpdatePageMetadataAsync(connection, updated, cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection,
                updated.Id,
                updated.Version,
                "user",
                now,
                "Renamed page",
                markdown,
                cancellationToken).ConfigureAwait(false);
            return new WikiPageDocument(updated, markdown);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WikiPageDocument?> MovePageAsync(
        string pageId,
        string? folderId,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var gate = LockForPage(pageId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var located = await FindPageAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (located is null) return null;
            var root = located.Value.Root;
            var current = located.Value.Page;
            if (expectedVersion.HasValue && expectedVersion.Value != current.Version)
            {
                throw new WikiVersionConflictException(pageId, expectedVersion.Value, current.Version);
            }

            var targetFolderId = string.IsNullOrWhiteSpace(folderId) ? null : folderId;
            var markdown = await ReadPageBodyAsync(root, current, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current.FolderId, targetFolderId, StringComparison.Ordinal))
            {
                return new WikiPageDocument(current, markdown);
            }

            var now = DateTimeOffset.UtcNow;
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(targetFolderId))
            {
                var folder = await GetFolderAsync(connection, targetFolderId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Folder '{targetFolderId}' not found.");
                if (!string.Equals(folder.RootId, root.Id, StringComparison.Ordinal))
                {
                    throw new WikiPathException("Folder belongs to a different wiki root.");
                }
            }

            var slug = await UniquePageSlugAsync(connection, targetFolderId, current.Slug, cancellationToken).ConfigureAwait(false);
            var relativePath = await BuildPageRelativePathAsync(connection, targetFolderId, slug, cancellationToken).ConfigureAwait(false);
            var updated = current with
            {
                FolderId = targetFolderId,
                Slug = slug,
                RelativePath = relativePath,
                Version = current.Version + 1,
                UpdatedAt = now,
            };

            var oldPath = ResolvePagePath(root, current.RelativePath);
            var newPath = ResolvePagePath(root, updated.RelativePath);
            await WritePageFileAsync(root, updated, markdown, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            {
                File.Delete(oldPath);
                DeleteEmptyDirectoriesUpToRoot(root.Path, Path.GetDirectoryName(oldPath));
            }

            await UpdatePageMetadataAsync(connection, updated, cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection,
                updated.Id,
                updated.Version,
                "user",
                now,
                "Moved page",
                markdown,
                cancellationToken).ConfigureAwait(false);
            return new WikiPageDocument(updated, markdown);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeletePageAsync(string pageId, CancellationToken cancellationToken)
    {
        var gate = LockForPage(pageId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var located = await FindPageAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (located is null) return false;

            var root = located.Value.Root;
            var page = located.Value.Page;
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            await SoftDeletePageAsync(connection, page.Id, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WikiPageDocument?> RestorePageAsync(string pageId, CancellationToken cancellationToken)
    {
        var gate = LockForPage(pageId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var located = await FindPageAsync(pageId, cancellationToken, includeDeleted: true).ConfigureAwait(false);
            if (located is null || located.Value.Page.DeletedAt is null) return null;

            var root = located.Value.Root;
            var page = located.Value.Page;
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(page.FolderId))
            {
                var folder = await GetFolderAsync(connection, page.FolderId, cancellationToken, includeDeleted: true).ConfigureAwait(false);
                if (folder?.DeletedAt is not null)
                {
                    throw new WikiPathException("Restore the containing folder before restoring this page.");
                }
            }

            var restoredAt = DateTimeOffset.UtcNow;
            await RestorePageRowAsync(connection, page.Id, restoredAt, cancellationToken).ConfigureAwait(false);
            var restored = page with { UpdatedAt = restoredAt, DeletedAt = null };
            var markdown = await ReadPageBodyAsync(root, page, cancellationToken).ConfigureAwait(false);
            await WritePageFileAsync(root, restored, markdown, cancellationToken).ConfigureAwait(false);
            return new WikiPageDocument(restored, markdown);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> PurgePageAsync(string pageId, CancellationToken cancellationToken)
    {
        var gate = LockForPage(pageId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var located = await FindPageAsync(pageId, cancellationToken, includeDeleted: true).ConfigureAwait(false);
            if (located is null) return false;
            await using var connection = await OpenRootAsync(located.Value.Root, cancellationToken).ConfigureAwait(false);
            await PurgePageAsync(connection, located.Value.Root, located.Value.Page, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<WikiRevision>> ListRevisionsAsync(string pageId, CancellationToken cancellationToken)
    {
        var located = await FindPageAsync(pageId, cancellationToken).ConfigureAwait(false);
        if (located is null) return Array.Empty<WikiRevision>();

        await using var connection = await OpenRootAsync(located.Value.Root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, page_id, version, source, created_at, summary, markdown
            from revisions
            where page_id = $pageId
            order by version desc
            """;
        Add(command, "$pageId", pageId);
        var revisions = new List<WikiRevision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(ReadRevision(reader));
        }
        return revisions;
    }

    public async Task<WikiPageDocument?> RestoreRevisionAsync(
        string pageId,
        string revisionId,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var located = await FindPageAsync(pageId, cancellationToken).ConfigureAwait(false);
        if (located is null) return null;

        await using var connection = await OpenRootAsync(located.Value.Root, cancellationToken).ConfigureAwait(false);
        var revision = await GetRevisionAsync(connection, pageId, revisionId, cancellationToken).ConfigureAwait(false);
        if (revision is null) return null;

        return await UpdatePageAsync(
            pageId,
            revision.Markdown,
            expectedVersion,
            "restore",
            $"Restored {revision.Id}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WikiSearchResult>> SearchAsync(string? rootId, string query, CancellationToken cancellationToken)
    {
        var normalizedQuery = (query ?? string.Empty).Trim();
        if (normalizedQuery.Length == 0) return Array.Empty<WikiSearchResult>();

        var roots = string.IsNullOrWhiteSpace(rootId)
            ? await ListRootsAsync(cancellationToken).ConfigureAwait(false)
            : new[] { await RequireRootAsync(rootId, cancellationToken).ConfigureAwait(false) };

        var results = new List<WikiSearchResult>();
        foreach (var root in roots)
        {
            var tree = await GetTreeAsync(root.Id, cancellationToken).ConfigureAwait(false);
            if (tree is null) continue;

            foreach (var page in tree.Pages)
            {
                var body = await ReadPageBodyAsync(root, page, cancellationToken).ConfigureAwait(false);
                if (!Contains(page.Title, normalizedQuery) && !Contains(page.Excerpt, normalizedQuery) && !Contains(body, normalizedQuery))
                {
                    continue;
                }

                results.Add(new WikiSearchResult(root.Id, page.Id, page.Title, page.Excerpt, page.RelativePath, page.Version));
            }
        }

        return results
            .OrderBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
    }

    public async Task<IReadOnlyList<WikiTrashItem>> ListTrashAsync(string rootId, CancellationToken cancellationToken)
    {
        var root = await RequireRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
        var folders = await ListFoldersAsync(connection, cancellationToken, includeDeleted: true).ConfigureAwait(false);
        var pages = await ListPagesAsync(connection, cancellationToken, includeDeleted: true).ConfigureAwait(false);
        var deletedFolderIds = folders
            .Where(folder => folder.DeletedAt is not null)
            .Select(folder => folder.Id)
            .ToHashSet(StringComparer.Ordinal);
        var topLevelDeletedFolders = folders
            .Where(folder => folder.DeletedAt is not null && (folder.ParentFolderId is null || !deletedFolderIds.Contains(folder.ParentFolderId)))
            .ToArray();
        var items = new List<WikiTrashItem>();
        foreach (var folder in topLevelDeletedFolders)
        {
            var affectedFolderIds = DescendantFolderIds(folders, folder.Id);
            var pageCount = pages.Count(page => page.FolderId is not null && affectedFolderIds.Contains(page.FolderId));
            var relativePath = await BuildFolderRelativePathAsync(connection, folder.Id, cancellationToken, includeDeleted: true).ConfigureAwait(false);
            items.Add(new WikiTrashItem(
                folder.Id,
                folder.RootId,
                "folder",
                folder.Name,
                relativePath,
                folder.DeletedAt!.Value,
                affectedFolderIds.Count,
                pageCount));
        }

        foreach (var page in pages.Where(page => page.DeletedAt is not null))
        {
            if (page.FolderId is not null && deletedFolderIds.Contains(page.FolderId)) continue;
            items.Add(new WikiTrashItem(
                page.Id,
                page.RootId,
                "page",
                page.Title,
                page.RelativePath,
                page.DeletedAt!.Value,
                0,
                1));
        }

        return items
            .OrderByDescending(item => item.DeletedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<WikiIndexRebuildResult?> RebuildIndexAsync(string rootId, CancellationToken cancellationToken)
    {
        var tree = await GetTreeAsync(rootId, cancellationToken).ConfigureAwait(false);
        return tree is null
            ? null
            : new WikiIndexRebuildResult(rootId, tree.Pages.Count, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
        foreach (var gate in _rootLocks.Values) gate.Dispose();
        foreach (var gate in _pageLocks.Values) gate.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
            await using var connection = await OpenRegistryAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                create table if not exists roots (
                    id text primary key,
                    name text not null,
                    path text not null,
                    created_at text not null,
                    updated_at text not null
                );
                create unique index if not exists idx_roots_path on roots(path);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureRootDatabaseAsync(WikiRoot root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(root.Path, ".sir-thaddeus"));
        await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists folders (
                id text primary key,
                root_id text not null,
                parent_folder_id text null,
                name text not null,
                slug text not null,
                sort_order integer not null,
                created_at text not null,
                updated_at text not null,
                deleted_at text null
            );

            create table if not exists pages (
                id text primary key,
                root_id text not null,
                folder_id text null,
                title text not null,
                slug text not null,
                relative_path text not null,
                version integer not null,
                created_at text not null,
                updated_at text not null,
                excerpt text not null,
                word_count integer not null,
                deleted_at text null
            );

            create table if not exists revisions (
                id text primary key,
                page_id text not null,
                version integer not null,
                source text not null,
                created_at text not null,
                summary text null,
                markdown text not null
            );

            create index if not exists idx_folders_parent on folders(parent_folder_id, sort_order);
            create index if not exists idx_pages_folder on pages(folder_id, updated_at);
            create index if not exists idx_revisions_page on revisions(page_id, version desc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "folders", "deleted_at", "text null", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "pages", "deleted_at", "text null", cancellationToken).ConfigureAwait(false);
        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            create index if not exists idx_folders_deleted on folders(deleted_at, parent_folder_id, sort_order);
            create index if not exists idx_pages_deleted on pages(deleted_at, folder_id, updated_at);
            """;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WikiRoot> RequireRootAsync(string rootId, CancellationToken cancellationToken) =>
        await GetRootAsync(rootId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Wiki root '{rootId}' not found.");

    private async Task<WikiRoot?> GetRootAsync(string rootId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenRegistryAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "select id, name, path, created_at, updated_at from roots where id = $id";
        Add(command, "$id", rootId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRoot(reader) : null;
    }

    private async Task<(WikiRoot Root, WikiPage Page)?> FindPageAsync(
        string pageId,
        CancellationToken cancellationToken,
        bool includeDeleted = false)
    {
        foreach (var root in await ListRootsAsync(cancellationToken).ConfigureAwait(false))
        {
            await EnsureRootDatabaseAsync(root, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenRootAsync(root, cancellationToken).ConfigureAwait(false);
            var page = await GetPageMetadataAsync(connection, pageId, cancellationToken, includeDeleted).ConfigureAwait(false);
            if (page is not null) return (root, page);
        }

        return null;
    }

    private async Task<IReadOnlyList<WikiFolder>> ListFoldersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        bool includeDeleted = false,
        bool deletedOnly = false)
    {
        using var command = connection.CreateCommand();
        var where = deletedOnly ? "where deleted_at is not null" : includeDeleted ? string.Empty : "where deleted_at is null";
        command.CommandText = $"""
            select id, root_id, parent_folder_id, name, slug, sort_order, created_at, updated_at, deleted_at
            from folders
            {where}
            order by parent_folder_id, sort_order, name
            """;
        var folders = new List<WikiFolder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(ReadFolder(reader));
        }
        return folders;
    }

    private async Task<IReadOnlyList<WikiPage>> ListPagesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        bool includeDeleted = false,
        bool deletedOnly = false)
    {
        using var command = connection.CreateCommand();
        var where = deletedOnly ? "where deleted_at is not null" : includeDeleted ? string.Empty : "where deleted_at is null";
        command.CommandText = $"""
            select id, root_id, folder_id, title, slug, relative_path, version, created_at, updated_at, excerpt, word_count, deleted_at
            from pages
            {where}
            order by title
            """;
        var pages = new List<WikiPage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pages.Add(ReadPage(reader));
        }
        return pages;
    }

    private async Task<WikiFolder?> GetFolderAsync(
        SqliteConnection connection,
        string folderId,
        CancellationToken cancellationToken,
        bool includeDeleted = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = includeDeleted
            ? """
                select id, root_id, parent_folder_id, name, slug, sort_order, created_at, updated_at, deleted_at
                from folders
                where id = $id
                """
            : """
                select id, root_id, parent_folder_id, name, slug, sort_order, created_at, updated_at, deleted_at
                from folders
                where id = $id and deleted_at is null
                """;
        Add(command, "$id", folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadFolder(reader) : null;
    }

    private static HashSet<string> DescendantFolderIds(IReadOnlyList<WikiFolder> folders, string folderId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { folderId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var folder in folders)
            {
                if (folder.ParentFolderId is not null && ids.Contains(folder.ParentFolderId) && ids.Add(folder.Id))
                {
                    changed = true;
                }
            }
        }

        return ids;
    }

    private async Task<WikiPage?> GetPageMetadataAsync(
        SqliteConnection connection,
        string pageId,
        CancellationToken cancellationToken,
        bool includeDeleted = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = includeDeleted
            ? """
                select id, root_id, folder_id, title, slug, relative_path, version, created_at, updated_at, excerpt, word_count, deleted_at
                from pages
                where id = $id
                """
            : """
                select id, root_id, folder_id, title, slug, relative_path, version, created_at, updated_at, excerpt, word_count, deleted_at
                from pages
                where id = $id and deleted_at is null
                """;
        Add(command, "$id", pageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPage(reader) : null;
    }

    private async Task UpdateFolderAsync(SqliteConnection connection, WikiFolder folder, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            update folders
            set parent_folder_id = $parentFolderId,
                name = $name,
                slug = $slug,
                sort_order = $sortOrder,
                updated_at = $updatedAt,
                deleted_at = $deletedAt
            where id = $id
            """;
        Add(command, "$id", folder.Id);
        AddNullable(command, "$parentFolderId", folder.ParentFolderId);
        Add(command, "$name", folder.Name);
        Add(command, "$slug", folder.Slug);
        Add(command, "$sortOrder", folder.SortOrder);
        Add(command, "$updatedAt", Format(folder.UpdatedAt));
        AddNullable(command, "$deletedAt", folder.DeletedAt.HasValue ? Format(folder.DeletedAt.Value) : null);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertPageAsync(SqliteConnection connection, WikiPage page, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into pages (id, root_id, folder_id, title, slug, relative_path, version, created_at, updated_at, excerpt, word_count, deleted_at)
            values ($id, $rootId, $folderId, $title, $slug, $relativePath, $version, $createdAt, $updatedAt, $excerpt, $wordCount, $deletedAt)
            """;
        AddPageParameters(command, page);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdatePageMetadataAsync(SqliteConnection connection, WikiPage page, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            update pages
            set folder_id = $folderId,
                title = $title,
                slug = $slug,
                relative_path = $relativePath,
                version = $version,
                updated_at = $updatedAt,
                excerpt = $excerpt,
                word_count = $wordCount,
                deleted_at = $deletedAt
            where id = $id
            """;
        AddPageParameters(command, page);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddPageParameters(SqliteCommand command, WikiPage page)
    {
        Add(command, "$id", page.Id);
        Add(command, "$rootId", page.RootId);
        AddNullable(command, "$folderId", page.FolderId);
        Add(command, "$title", page.Title);
        Add(command, "$slug", page.Slug);
        Add(command, "$relativePath", page.RelativePath);
        Add(command, "$version", page.Version);
        Add(command, "$createdAt", Format(page.CreatedAt));
        Add(command, "$updatedAt", Format(page.UpdatedAt));
        Add(command, "$excerpt", page.Excerpt);
        Add(command, "$wordCount", page.WordCount);
        AddNullable(command, "$deletedAt", page.DeletedAt.HasValue ? Format(page.DeletedAt.Value) : null);
    }

    private static async Task SoftDeleteFolderAsync(
        SqliteConnection connection,
        string folderId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            update folders
            set deleted_at = $deletedAt,
                updated_at = $deletedAt
            where id = $id and deleted_at is null
            """;
        Add(command, "$id", folderId);
        Add(command, "$deletedAt", Format(deletedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SoftDeletePageAsync(
        SqliteConnection connection,
        string pageId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            update pages
            set deleted_at = $deletedAt,
                updated_at = $deletedAt
            where id = $id and deleted_at is null
            """;
        Add(command, "$id", pageId);
        Add(command, "$deletedAt", Format(deletedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RestoreFolderRowAsync(
        SqliteConnection connection,
        string folderId,
        DateTimeOffset restoredAt,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            update folders
            set deleted_at = null,
                updated_at = $restoredAt
            where id = $id
            """;
        Add(command, "$id", folderId);
        Add(command, "$restoredAt", Format(restoredAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RestorePageRowAsync(
        SqliteConnection connection,
        string pageId,
        DateTimeOffset restoredAt,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            update pages
            set deleted_at = null,
                updated_at = $restoredAt
            where id = $id
            """;
        Add(command, "$id", pageId);
        Add(command, "$restoredAt", Format(restoredAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PurgePageAsync(
        SqliteConnection connection,
        WikiRoot root,
        WikiPage page,
        CancellationToken cancellationToken)
    {
        var path = ResolvePagePath(root, page.RelativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
            DeleteEmptyDirectoriesUpToRoot(root.Path, Path.GetDirectoryName(path));
        }

        using var deleteRevisions = connection.CreateCommand();
        deleteRevisions.CommandText = "delete from revisions where page_id = $pageId";
        Add(deleteRevisions, "$pageId", page.Id);
        await deleteRevisions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var deletePage = connection.CreateCommand();
        deletePage.CommandText = "delete from pages where id = $pageId";
        Add(deletePage, "$pageId", page.Id);
        await deletePage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertRevisionAsync(
        SqliteConnection connection,
        string pageId,
        long version,
        string source,
        DateTimeOffset createdAt,
        string? summary,
        string markdown,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into revisions (id, page_id, version, source, created_at, summary, markdown)
            values ($id, $pageId, $version, $source, $createdAt, $summary, $markdown)
            """;
        Add(command, "$id", NewId("rev"));
        Add(command, "$pageId", pageId);
        Add(command, "$version", version);
        Add(command, "$source", source);
        Add(command, "$createdAt", Format(createdAt));
        AddNullable(command, "$summary", summary);
        Add(command, "$markdown", markdown ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WikiRevision?> GetRevisionAsync(SqliteConnection connection, string pageId, string revisionId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, page_id, version, source, created_at, summary, markdown
            from revisions
            where page_id = $pageId and id = $id
            """;
        Add(command, "$pageId", pageId);
        Add(command, "$id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRevision(reader) : null;
    }

    private async Task<string> UniqueFolderSlugAsync(SqliteConnection connection, string? parentFolderId, string baseSlug, CancellationToken cancellationToken)
    {
        var existing = await ExistingSlugsAsync(connection, "folders", "parent_folder_id", parentFolderId, cancellationToken).ConfigureAwait(false);
        return UniqueSlug(baseSlug, existing);
    }

    private async Task<string> UniquePageSlugAsync(SqliteConnection connection, string? folderId, string baseSlug, CancellationToken cancellationToken)
    {
        var existing = await ExistingSlugsAsync(connection, "pages", "folder_id", folderId, cancellationToken).ConfigureAwait(false);
        return UniqueSlug(baseSlug, existing);
    }

    private static async Task<HashSet<string>> ExistingSlugsAsync(
        SqliteConnection connection,
        string table,
        string scopeColumn,
        string? scopeValue,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(scopeValue)
            ? $"select slug from {table} where {scopeColumn} is null"
            : $"select slug from {table} where {scopeColumn} = $scope";
        if (!string.IsNullOrWhiteSpace(scopeValue)) Add(command, "$scope", scopeValue);
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            slugs.Add(reader.GetString(0));
        }
        return slugs;
    }

    private static string UniqueSlug(string baseSlug, HashSet<string> existing)
    {
        var slug = baseSlug;
        var suffix = 2;
        while (existing.Contains(slug))
        {
            slug = string.Create(CultureInfo.InvariantCulture, $"{baseSlug}-{suffix++}");
        }
        return slug;
    }

    private static async Task<int> NextFolderSortOrderAsync(SqliteConnection connection, string? parentFolderId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(parentFolderId)
            ? "select coalesce(max(sort_order), -1) + 1 from folders where parent_folder_id is null"
            : "select coalesce(max(sort_order), -1) + 1 from folders where parent_folder_id = $parentFolderId";
        if (!string.IsNullOrWhiteSpace(parentFolderId)) Add(command, "$parentFolderId", parentFolderId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private async Task<string> BuildPageRelativePathAsync(
        SqliteConnection connection,
        string? folderId,
        string pageSlug,
        CancellationToken cancellationToken)
    {
        var fileName = pageSlug + ".md";
        if (string.IsNullOrWhiteSpace(folderId)) return fileName;

        var folderPath = await BuildFolderRelativePathAsync(connection, folderId, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(folderPath) ? fileName : Path.Combine(folderPath, fileName);
    }

    private async Task<string> BuildFolderRelativePathAsync(
        SqliteConnection connection,
        string folderId,
        CancellationToken cancellationToken,
        bool includeDeleted = false)
    {
        var segments = new Stack<string>();
        var currentId = folderId;
        while (!string.IsNullOrWhiteSpace(currentId))
        {
            var folder = await GetFolderAsync(connection, currentId, cancellationToken, includeDeleted).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Folder '{currentId}' not found.");
            segments.Push(folder.Slug);
            currentId = folder.ParentFolderId;
        }

        return Path.Combine(segments.ToArray());
    }

    private async Task WritePageFileAsync(WikiRoot root, WikiPage page, string markdown, CancellationToken cancellationToken)
    {
        var path = ResolvePagePath(root, page.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, WikiFrontmatter.Write(page, markdown ?? string.Empty), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    private async Task<string> ReadPageBodyAsync(WikiRoot root, WikiPage page, CancellationToken cancellationToken)
    {
        var path = ResolvePagePath(root, page.RelativePath);
        if (!File.Exists(path)) return string.Empty;
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return WikiFrontmatter.Strip(text);
    }

    private string ResolveRootPath(string name, string? requestedPath)
    {
        var path = string.IsNullOrWhiteSpace(requestedPath)
            ? Path.Combine(_libraryDirectory, Slugify(name))
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath));
        if (!IsPathUnderRoot(_libraryDirectory, path))
        {
            throw new WikiPathException("Wiki roots must live inside the configured wiki library directory.");
        }
        return path;
    }

    private static string ResolvePagePath(WikiRoot root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new WikiPathException("Wiki page paths must be relative to their root.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root.Path, relativePath));
        if (!IsPathUnderRoot(root.Path, fullPath))
        {
            throw new WikiPathException("Wiki page path escapes the root.");
        }
        return fullPath;
    }

    private static bool IsPathUnderRoot(string rootPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(candidatePath));
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static void DeleteEmptyDirectoriesUpToRoot(string rootPath, string? startDirectory)
    {
        var root = Path.GetFullPath(rootPath);
        var current = string.IsNullOrWhiteSpace(startDirectory) ? null : Path.GetFullPath(startDirectory);
        while (!string.IsNullOrWhiteSpace(current)
            && !current.Equals(root, StringComparison.OrdinalIgnoreCase)
            && IsPathUnderRoot(root, current))
        {
            if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }

            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private async Task<SqliteConnection> OpenRegistryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
        var connection = new SqliteConnection(ConnectionString(_registryPath));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<SqliteConnection> OpenRootAsync(WikiRoot root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root.Path, ".sir-thaddeus", RootDatabaseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(ConnectionString(path));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        using (var readColumns = connection.CreateCommand())
        {
            readColumns.CommandText = $"pragma table_info({table})";
            await using var reader = await readColumns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var addColumn = connection.CreateCommand();
        addColumn.CommandText = $"alter table {table} add column {column} {definition}";
        await addColumn.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    private SemaphoreSlim LockForRoot(string rootId) =>
        _rootLocks.GetOrAdd(rootId, _ => new SemaphoreSlim(1, 1));

    private SemaphoreSlim LockForPage(string pageId) =>
        _pageLocks.GetOrAdd(pageId, _ => new SemaphoreSlim(1, 1));

    private static WikiRoot ReadRoot(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseDate(reader.GetString(3)),
            ParseDate(reader.GetString(4)));

    private static WikiFolder ReadFolder(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            ParseDate(reader.GetString(6)),
            ParseDate(reader.GetString(7)),
            reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)));

    private static WikiPage ReadPage(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            ParseDate(reader.GetString(7)),
            ParseDate(reader.GetString(8)),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)));

    private static WikiRevision ReadRevision(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            ParseDate(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6));

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value);

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string NormalizeName(string? value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) trimmed = fallback;
        return trimmed.Length > 180 ? trimmed[..180] : trimmed;
    }

    private static string NormalizeRevisionSource(string? source)
    {
        var trimmed = (source ?? string.Empty).Trim().ToLowerInvariant();
        return trimmed is "ai" or "user" or "restore" ? trimmed : "user";
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousDash = false;
        foreach (var rune in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(rune))
            {
                builder.Append(rune);
                previousDash = false;
            }
            else if (!previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0) slug = "untitled";
        return slug.Length > 80 ? slug[..80].TrimEnd('-') : slug;
    }

    private static string Excerpt(string markdown)
    {
        var line = (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static candidate => !candidate.StartsWith('#'))
            ?? string.Empty;
        return line.Length > 220 ? line[..220] : line;
    }

    private static int CountWords(string markdown) =>
        (markdown ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Length;

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string NewId(string prefix)
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}_{Convert.ToHexString(bytes).ToLowerInvariant()}");
    }
}