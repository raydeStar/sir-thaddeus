using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using SirThaddeus.Wiki;
using SirThaddeus.Wiki.Storage;

namespace Thaddeus.Runtime.Tests;

public sealed class LocalWikiStoreTests : IDisposable
{
    private readonly string _tempDir;

    public LocalWikiStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "thaddeus-wiki-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Create_root_folder_page_persists_markdown_and_tree()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var folder = await store.CreateFolderAsync(root.Id, "Projects", null, CancellationToken.None);
        var page = await store.CreatePageAsync(
            root.Id,
            folder.Id,
            "Canvas Plan",
            "# Canvas Plan\n\nVersioned local markdown.",
            CancellationToken.None);

        Assert.StartsWith("root_", root.Id);
        Assert.StartsWith("folder_", folder.Id);
        Assert.StartsWith("page_", page.Page.Id);
        Assert.Equal("projects", folder.Slug);
        Assert.Equal(Path.Combine("projects", "canvas-plan.md"), page.Page.RelativePath);

        var filePath = Path.Combine(root.Path, page.Page.RelativePath);
        Assert.True(File.Exists(filePath));
        Assert.Contains("id: " + page.Page.Id, await File.ReadAllTextAsync(filePath));

        var tree = await store.GetTreeAsync(root.Id, CancellationToken.None);
        Assert.NotNull(tree);
        Assert.Equal(root.Id, tree!.Root.Id);
        Assert.Contains(tree.Folders, item => item.Id == folder.Id);
        Assert.Contains(tree.Pages, item => item.Id == page.Page.Id);

        using var reopened = NewStore();
        var fetched = await reopened.GetPageAsync(page.Page.Id, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("# Canvas Plan\n\nVersioned local markdown.", fetched!.Markdown);
    }

    [Fact]
    public async Task Create_root_rejects_paths_outside_library()
    {
        using var store = NewStore();
        var outside = Path.Combine(Path.GetTempPath(), "thaddeus-wiki-outside-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<WikiPathException>(() =>
            store.CreateRootAsync("Outside", outside, CancellationToken.None));
    }

    [Fact]
    public async Task Rename_root_updates_registry_name_without_moving_path()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Old Root", null, CancellationToken.None);

        var renamed = await store.RenameRootAsync(root.Id, "New Root", CancellationToken.None);

        Assert.NotNull(renamed);
        Assert.Equal("New Root", renamed!.Name);
        Assert.Equal(root.Path, renamed.Path);

        using var reopened = NewStore();
        var roots = await reopened.ListRootsAsync(CancellationToken.None);
        var persisted = Assert.Single(roots, candidate => candidate.Id == root.Id);
        Assert.Equal("New Root", persisted.Name);
        Assert.Equal(root.Path, persisted.Path);
    }

    [Fact]
    public async Task Remove_root_unregisters_root_without_deleting_files()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await store.CreatePageAsync(root.Id, null, "Notes", "# Notes\n\nKeep on disk.", CancellationToken.None);
        var pagePath = Path.Combine(root.Path, page.Page.RelativePath);

        Assert.True(Directory.Exists(root.Path));
        Assert.True(File.Exists(pagePath));

        var removed = await store.RemoveRootAsync(root.Id, CancellationToken.None);

        Assert.NotNull(removed);
        Assert.Equal(root.Id, removed!.Id);
        Assert.DoesNotContain(await store.ListRootsAsync(CancellationToken.None), candidate => candidate.Id == root.Id);
        Assert.Null(await store.GetTreeAsync(root.Id, CancellationToken.None));
        Assert.Null(await store.GetPageAsync(page.Page.Id, CancellationToken.None));
        Assert.True(Directory.Exists(root.Path));
        Assert.True(File.Exists(pagePath));

        using var reopened = NewStore();
        Assert.DoesNotContain(await reopened.ListRootsAsync(CancellationToken.None), candidate => candidate.Id == root.Id);
        Assert.True(File.Exists(pagePath));
    }

    [Fact]
    public async Task Update_page_rejects_stale_expected_version()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await store.CreatePageAsync(root.Id, null, "Notes", "first", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<WikiVersionConflictException>(() =>
            store.UpdatePageAsync(page.Page.Id, "second", expectedVersion: 99, source: "user", summary: null, CancellationToken.None));

        Assert.Equal(page.Page.Id, ex.PageId);
        Assert.Equal(1, ex.CurrentVersion);
    }

    [Fact]
    public async Task Ai_update_creates_revision_with_source_and_new_version()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await store.CreatePageAsync(root.Id, null, "Notes", "first", CancellationToken.None);

        var updated = await store.UpdatePageAsync(
            page.Page.Id,
            "second",
            expectedVersion: 1,
            source: "ai",
            summary: "AI rewrite",
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(2, updated!.Page.Version);

        var revisions = await store.ListRevisionsAsync(page.Page.Id, CancellationToken.None);
        var aiRevision = Assert.Single(revisions.Where(revision => revision.Source == "ai"));
        Assert.Equal(2, aiRevision.Version);
        Assert.Equal("second", aiRevision.Markdown);
        Assert.Equal("AI rewrite", aiRevision.Summary);
    }

    [Fact]
    public async Task Rename_page_updates_metadata_frontmatter_path_and_version()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var folder = await store.CreateFolderAsync(root.Id, "Projects", null, CancellationToken.None);
        var page = await store.CreatePageAsync(root.Id, folder.Id, "Old Title", "body", CancellationToken.None);
        var oldPath = Path.Combine(root.Path, page.Page.RelativePath);

        var renamed = await store.RenamePageAsync(page.Page.Id, "New Title", page.Page.Version, CancellationToken.None);

        Assert.NotNull(renamed);
        Assert.Equal("New Title", renamed!.Page.Title);
        Assert.Equal(2, renamed.Page.Version);
        Assert.Equal(Path.Combine("projects", "new-title.md"), renamed.Page.RelativePath);
        Assert.False(File.Exists(oldPath));
        var newFile = await File.ReadAllTextAsync(Path.Combine(root.Path, renamed.Page.RelativePath));
        Assert.Contains("title: New Title", newFile);
        Assert.Contains("body", newFile);

        var revisions = await store.ListRevisionsAsync(page.Page.Id, CancellationToken.None);
        Assert.Contains(revisions, revision => revision.Version == 2 && revision.Summary == "Renamed page");
    }

    [Fact]
    public async Task Move_page_updates_metadata_frontmatter_path_and_version()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var source = await store.CreateFolderAsync(root.Id, "Inbox", null, CancellationToken.None);
        var parent = await store.CreateFolderAsync(root.Id, "Projects", null, CancellationToken.None);
        var target = await store.CreateFolderAsync(root.Id, "Archive", parent.Id, CancellationToken.None);
        var page = await store.CreatePageAsync(root.Id, source.Id, "Plan", "body", CancellationToken.None);
        var sourcePath = Path.Combine(root.Path, page.Page.RelativePath);

        var moved = await store.MovePageAsync(page.Page.Id, target.Id, page.Page.Version, CancellationToken.None);

        Assert.NotNull(moved);
        Assert.Equal(target.Id, moved!.Page.FolderId);
        Assert.Equal(2, moved.Page.Version);
        Assert.Equal(Path.Combine("projects", "archive", "plan.md"), moved.Page.RelativePath);
        Assert.False(File.Exists(sourcePath));
        var nestedFile = await File.ReadAllTextAsync(Path.Combine(root.Path, moved.Page.RelativePath));
        Assert.Contains("folderId: " + target.Id, nestedFile);
        Assert.Contains("version: 2", nestedFile);
        Assert.Contains("body", nestedFile);

        var movedToRoot = await store.MovePageAsync(page.Page.Id, null, moved.Page.Version, CancellationToken.None);

        Assert.NotNull(movedToRoot);
        Assert.Null(movedToRoot!.Page.FolderId);
        Assert.Equal(3, movedToRoot.Page.Version);
        Assert.Equal("plan.md", movedToRoot.Page.RelativePath);
        var rootFile = await File.ReadAllTextAsync(Path.Combine(root.Path, movedToRoot.Page.RelativePath));
        Assert.DoesNotContain("folderId:", rootFile);

        var revisions = await store.ListRevisionsAsync(page.Page.Id, CancellationToken.None);
        Assert.Contains(revisions, revision => revision.Version == 2 && revision.Summary == "Moved page");
        Assert.Contains(revisions, revision => revision.Version == 3 && revision.Summary == "Moved page");
    }

    [Fact]
    public async Task Delete_page_moves_to_trash_restore_recovers_and_purge_removes_file()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var folder = await store.CreateFolderAsync(root.Id, "Projects", null, CancellationToken.None);
        var page = await store.CreatePageAsync(root.Id, folder.Id, "Plan", "first", CancellationToken.None);
        var updated = await store.UpdatePageAsync(page.Page.Id, "second", page.Page.Version, "user", "Manual save", CancellationToken.None);
        Assert.NotNull(updated);
        var filePath = Path.Combine(root.Path, updated!.Page.RelativePath);
        Assert.True(File.Exists(filePath));

        var deleted = await store.DeletePageAsync(page.Page.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.True(File.Exists(filePath));
        Assert.Null(await store.GetPageAsync(page.Page.Id, CancellationToken.None));
        var tree = await store.GetTreeAsync(root.Id, CancellationToken.None);
        Assert.NotNull(tree);
        Assert.DoesNotContain(tree!.Pages, candidate => candidate.Id == page.Page.Id);
        var trashed = Assert.Single(await store.ListTrashAsync(root.Id, CancellationToken.None));
        Assert.Equal("page", trashed.Type);
        Assert.Equal(page.Page.Id, trashed.Id);
        Assert.False(await store.DeletePageAsync(page.Page.Id, CancellationToken.None));

        var restored = await store.RestorePageAsync(page.Page.Id, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("second", restored!.Markdown);
        Assert.Null(restored.Page.DeletedAt);
        Assert.NotEmpty(await store.ListRevisionsAsync(page.Page.Id, CancellationToken.None));
        tree = await store.GetTreeAsync(root.Id, CancellationToken.None);
        Assert.NotNull(tree);
        Assert.Contains(tree!.Pages, candidate => candidate.Id == page.Page.Id);
        Assert.Empty(await store.ListTrashAsync(root.Id, CancellationToken.None));

        Assert.True(await store.DeletePageAsync(page.Page.Id, CancellationToken.None));
        Assert.True(await store.PurgePageAsync(page.Page.Id, CancellationToken.None));
        Assert.False(File.Exists(filePath));
        Assert.Null(await store.GetPageAsync(page.Page.Id, CancellationToken.None));
        Assert.Empty(await store.ListTrashAsync(root.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Rename_folder_updates_descendant_page_paths_and_frontmatter()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var parent = await store.CreateFolderAsync(root.Id, "Old Projects", null, CancellationToken.None);
        var child = await store.CreateFolderAsync(root.Id, "Archive", parent.Id, CancellationToken.None);
        var parentPage = await store.CreatePageAsync(root.Id, parent.Id, "Plan", "parent body", CancellationToken.None);
        var childPage = await store.CreatePageAsync(root.Id, child.Id, "Notes", "child body", CancellationToken.None);
        var oldParentPath = Path.Combine(root.Path, parentPage.Page.RelativePath);
        var oldChildPath = Path.Combine(root.Path, childPage.Page.RelativePath);

        var renamed = await store.RenameFolderAsync(root.Id, parent.Id, "New Projects", CancellationToken.None);

        Assert.NotNull(renamed);
        Assert.Equal("New Projects", renamed!.Name);
        Assert.Equal("new-projects", renamed.Slug);
        Assert.False(File.Exists(oldParentPath));
        Assert.False(File.Exists(oldChildPath));

        var tree = await store.GetTreeAsync(root.Id, CancellationToken.None);
        Assert.NotNull(tree);
        var renamedParentPage = Assert.Single(tree!.Pages, page => page.Id == parentPage.Page.Id);
        var renamedChildPage = Assert.Single(tree.Pages, page => page.Id == childPage.Page.Id);
        Assert.Equal(Path.Combine("new-projects", "plan.md"), renamedParentPage.RelativePath);
        Assert.Equal(Path.Combine("new-projects", "archive", "notes.md"), renamedChildPage.RelativePath);

        var parentFile = await File.ReadAllTextAsync(Path.Combine(root.Path, renamedParentPage.RelativePath));
        var childFile = await File.ReadAllTextAsync(Path.Combine(root.Path, renamedChildPage.RelativePath));
        Assert.Contains("parent body", parentFile);
        Assert.Contains("child body", childFile);
    }

    [Fact]
    public async Task Move_folder_updates_descendant_page_paths_and_rejects_cycles()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var parent = await store.CreateFolderAsync(root.Id, "Projects", null, CancellationToken.None);
        var child = await store.CreateFolderAsync(root.Id, "Archive", parent.Id, CancellationToken.None);
        var target = await store.CreateFolderAsync(root.Id, "Clients", null, CancellationToken.None);
        var parentPage = await store.CreatePageAsync(root.Id, parent.Id, "Plan", "parent body", CancellationToken.None);
        var childPage = await store.CreatePageAsync(root.Id, child.Id, "Notes", "child body", CancellationToken.None);
        var oldParentPath = Path.Combine(root.Path, parentPage.Page.RelativePath);
        var oldChildPath = Path.Combine(root.Path, childPage.Page.RelativePath);

        var moved = await store.MoveFolderAsync(root.Id, parent.Id, target.Id, CancellationToken.None);

        Assert.NotNull(moved);
        Assert.Equal(target.Id, moved!.ParentFolderId);
        Assert.Equal("projects", moved.Slug);
        Assert.False(File.Exists(oldParentPath));
        Assert.False(File.Exists(oldChildPath));

        var tree = await store.GetTreeAsync(root.Id, CancellationToken.None);
        Assert.NotNull(tree);
        var movedParentPage = Assert.Single(tree!.Pages, page => page.Id == parentPage.Page.Id);
        var movedChildPage = Assert.Single(tree.Pages, page => page.Id == childPage.Page.Id);
        Assert.Equal(Path.Combine("clients", "projects", "plan.md"), movedParentPage.RelativePath);
        Assert.Equal(Path.Combine("clients", "projects", "archive", "notes.md"), movedChildPage.RelativePath);

        var parentFile = await File.ReadAllTextAsync(Path.Combine(root.Path, movedParentPage.RelativePath));
        var childFile = await File.ReadAllTextAsync(Path.Combine(root.Path, movedChildPage.RelativePath));
        Assert.Contains("folderId: " + parent.Id, parentFile);
        Assert.Contains("folderId: " + child.Id, childFile);
        Assert.Contains("parent body", parentFile);
        Assert.Contains("child body", childFile);

        await Assert.ThrowsAsync<WikiPathException>(() =>
            store.MoveFolderAsync(root.Id, parent.Id, child.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_folder_moves_subtree_to_trash_restore_recovers_and_purge_removes_files()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Personal", null, CancellationToken.None);
        var parent = await store.CreateFolderAsync(root.Id, "Projects", null, CancellationToken.None);
        var child = await store.CreateFolderAsync(root.Id, "Archive", parent.Id, CancellationToken.None);
        var outside = await store.CreateFolderAsync(root.Id, "Outside", null, CancellationToken.None);
        var parentPage = await store.CreatePageAsync(root.Id, parent.Id, "Plan", "parent body", CancellationToken.None);
        var childPage = await store.CreatePageAsync(root.Id, child.Id, "Notes", "child body", CancellationToken.None);
        var outsidePage = await store.CreatePageAsync(root.Id, outside.Id, "Keep", "outside body", CancellationToken.None);
        var parentPath = Path.Combine(root.Path, parentPage.Page.RelativePath);
        var childPath = Path.Combine(root.Path, childPage.Page.RelativePath);

        var deleted = await store.DeleteFolderAsync(root.Id, parent.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.True(File.Exists(parentPath));
        Assert.True(File.Exists(childPath));
        Assert.Null(await store.GetPageAsync(parentPage.Page.Id, CancellationToken.None));
        Assert.Null(await store.GetPageAsync(childPage.Page.Id, CancellationToken.None));
        Assert.NotNull(await store.GetPageAsync(outsidePage.Page.Id, CancellationToken.None));

        var tree = await store.GetTreeAsync(root.Id, CancellationToken.None);
        Assert.NotNull(tree);
        Assert.DoesNotContain(tree!.Folders, folder => folder.Id == parent.Id || folder.Id == child.Id);
        Assert.DoesNotContain(tree.Pages, page => page.Id == parentPage.Page.Id || page.Id == childPage.Page.Id);
        Assert.Contains(tree.Folders, folder => folder.Id == outside.Id);
        Assert.Contains(tree.Pages, page => page.Id == outsidePage.Page.Id);
        var trashed = Assert.Single(await store.ListTrashAsync(root.Id, CancellationToken.None));
        Assert.Equal("folder", trashed.Type);
        Assert.Equal(parent.Id, trashed.Id);
        Assert.Equal(2, trashed.FolderCount);
        Assert.Equal(2, trashed.PageCount);
        Assert.False(await store.DeleteFolderAsync(root.Id, parent.Id, CancellationToken.None));

        Assert.True(await store.RestoreFolderAsync(root.Id, parent.Id, CancellationToken.None));
        Assert.NotNull(await store.GetPageAsync(parentPage.Page.Id, CancellationToken.None));
        Assert.NotNull(await store.GetPageAsync(childPage.Page.Id, CancellationToken.None));
        Assert.NotEmpty(await store.ListRevisionsAsync(parentPage.Page.Id, CancellationToken.None));
        Assert.NotEmpty(await store.ListRevisionsAsync(childPage.Page.Id, CancellationToken.None));
        tree = await store.GetTreeAsync(root.Id, CancellationToken.None);
        Assert.NotNull(tree);
        Assert.Contains(tree!.Folders, folder => folder.Id == parent.Id);
        Assert.Contains(tree.Folders, folder => folder.Id == child.Id);
        Assert.Contains(tree.Pages, page => page.Id == parentPage.Page.Id);
        Assert.Contains(tree.Pages, page => page.Id == childPage.Page.Id);
        Assert.Empty(await store.ListTrashAsync(root.Id, CancellationToken.None));

        Assert.True(await store.DeleteFolderAsync(root.Id, parent.Id, CancellationToken.None));
        Assert.True(await store.PurgeFolderAsync(root.Id, parent.Id, CancellationToken.None));
        Assert.False(File.Exists(parentPath));
        Assert.False(File.Exists(childPath));
        Assert.Null(await store.GetPageAsync(parentPage.Page.Id, CancellationToken.None));
        Assert.Null(await store.GetPageAsync(childPage.Page.Id, CancellationToken.None));
        Assert.Empty(await store.ListTrashAsync(root.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Folder_ids_are_not_valid_across_roots()
    {
        using var store = NewStore();
        var firstRoot = await store.CreateRootAsync("First", null, CancellationToken.None);
        var secondRoot = await store.CreateRootAsync("Second", null, CancellationToken.None);
        var folder = await store.CreateFolderAsync(firstRoot.Id, "Only Here", null, CancellationToken.None);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.CreatePageAsync(secondRoot.Id, folder.Id, "Wrong Root", "body", CancellationToken.None));
    }

    [Fact]
    public async Task Search_returns_matching_page_inside_selected_root()
    {
        using var store = NewStore();
        var firstRoot = await store.CreateRootAsync("First", null, CancellationToken.None);
        var secondRoot = await store.CreateRootAsync("Second", null, CancellationToken.None);
        var firstPage = await store.CreatePageAsync(firstRoot.Id, null, "Alpha", "needle in first root", CancellationToken.None);
        await store.CreatePageAsync(secondRoot.Id, null, "Beta", "needle in second root", CancellationToken.None);

        var results = await store.SearchAsync(firstRoot.Id, "needle", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(firstRoot.Id, result.RootId);
        Assert.Equal(firstPage.Page.Id, result.PageId);
    }

    [Fact]
    public async Task Search_uses_rebuilt_index_when_search_table_is_empty()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Research", null, CancellationToken.None);
        var page = await store.CreatePageAsync(root.Id, null, "Phoenix Notes", "The phoenix archive mentions copper bells.", CancellationToken.None);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root.Path, ".sir-thaddeus", "wiki.sqlite")}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "delete from page_search";
            await command.ExecuteNonQueryAsync();
        }

        var results = await store.SearchAsync(root.Id, "phoenix", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(page.Page.Id, result.PageId);
    }

    [Fact]
    public async Task Search_hides_trashed_pages_and_restore_reindexes_them()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Research", null, CancellationToken.None);
        var page = await store.CreatePageAsync(root.Id, null, "Archive", "The citadel keyword should be searchable.", CancellationToken.None);

        Assert.Single(await store.SearchAsync(root.Id, "citadel", CancellationToken.None));

        Assert.True(await store.DeletePageAsync(page.Page.Id, CancellationToken.None));
        Assert.Empty(await store.SearchAsync(root.Id, "citadel", CancellationToken.None));

        var restored = await store.RestorePageAsync(page.Page.Id, CancellationToken.None);

        Assert.NotNull(restored);
        var restoredResult = Assert.Single(await store.SearchAsync(root.Id, "citadel", CancellationToken.None));
        Assert.Equal(page.Page.Id, restoredResult.PageId);

        Assert.True(await store.DeletePageAsync(page.Page.Id, CancellationToken.None));
        Assert.True(await store.PurgePageAsync(page.Page.Id, CancellationToken.None));
        Assert.Empty(await store.SearchAsync(root.Id, "citadel", CancellationToken.None));
    }

    [Fact]
    public async Task Page_graph_indexes_links_tags_and_lifecycle_visibility()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Research", null, CancellationToken.None);
        var atlas = await store.CreatePageAsync(root.Id, null, "Atlas", "# Atlas\n\nReference page.", CancellationToken.None);
        var bridge = await store.CreatePageAsync(
            root.Id,
            null,
            "Bridge",
            "---\ntags: [Lore, worldbuilding]\n---\n# Bridge\n\nConnects to [[Atlas]] and [the file](atlas.md). #Field-Note",
            CancellationToken.None);

        var bridgeGraph = await store.GetPageGraphAsync(bridge.Page.Id, CancellationToken.None);
        var atlasGraph = await store.GetPageGraphAsync(atlas.Page.Id, CancellationToken.None);

        Assert.NotNull(bridgeGraph);
        var link = Assert.Single(bridgeGraph!.Links);
        Assert.Equal(atlas.Page.Id, link.PageId);
        Assert.Contains("field-note", bridgeGraph.Tags);
        Assert.Contains("lore", bridgeGraph.Tags);
        Assert.Contains("worldbuilding", bridgeGraph.Tags);
        Assert.NotNull(atlasGraph);
        var backlink = Assert.Single(atlasGraph!.Backlinks);
        Assert.Equal(bridge.Page.Id, backlink.PageId);

        var tagged = Assert.Single(await store.SearchAsync(root.Id, "#lore", CancellationToken.None));
        Assert.Equal(bridge.Page.Id, tagged.PageId);

        Assert.True(await store.DeletePageAsync(bridge.Page.Id, CancellationToken.None));
        Assert.Empty((await store.GetPageGraphAsync(atlas.Page.Id, CancellationToken.None))!.Backlinks);
        Assert.Empty(await store.SearchAsync(root.Id, "#lore", CancellationToken.None));

        var restored = await store.RestorePageAsync(bridge.Page.Id, CancellationToken.None);

        Assert.NotNull(restored);
        var restoredBacklink = Assert.Single((await store.GetPageGraphAsync(atlas.Page.Id, CancellationToken.None))!.Backlinks);
        Assert.Equal(bridge.Page.Id, restoredBacklink.PageId);
        Assert.Single(await store.SearchAsync(root.Id, "#lore", CancellationToken.None));
    }

    [Fact]
    public async Task RebuildIndex_restores_page_graph_metadata()
    {
        using var store = NewStore();
        var root = await store.CreateRootAsync("Research", null, CancellationToken.None);
        var target = await store.CreatePageAsync(root.Id, null, "Target", "# Target", CancellationToken.None);
        var source = await store.CreatePageAsync(root.Id, null, "Source", "See [[Target]]. #indexed", CancellationToken.None);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root.Path, ".sir-thaddeus", "wiki.sqlite")}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                delete from page_links;
                delete from page_tags;
                """;
            await command.ExecuteNonQueryAsync();
        }

        Assert.Empty((await store.GetPageGraphAsync(target.Page.Id, CancellationToken.None))!.Backlinks);
        Assert.Empty(await store.SearchAsync(root.Id, "#indexed", CancellationToken.None));

        var rebuilt = await store.RebuildIndexAsync(root.Id, CancellationToken.None);

        Assert.NotNull(rebuilt);
        Assert.Equal(2, rebuilt!.PageCount);
        var backlink = Assert.Single((await store.GetPageGraphAsync(target.Page.Id, CancellationToken.None))!.Backlinks);
        Assert.Equal(source.Page.Id, backlink.PageId);
        var tagged = Assert.Single(await store.SearchAsync(root.Id, "#indexed", CancellationToken.None));
        Assert.Equal(source.Page.Id, tagged.PageId);
    }

    private LocalWikiStore NewStore() =>
        new(_tempDir, NullLogger<LocalWikiStore>.Instance);
}