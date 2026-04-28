using Microsoft.Extensions.Logging.Abstractions;
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

    private LocalWikiStore NewStore() =>
        new(_tempDir, NullLogger<LocalWikiStore>.Instance);
}