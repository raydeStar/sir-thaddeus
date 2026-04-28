using System.Text.Json;
using Microsoft.Data.Sqlite;
using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Tests;

[Collection(KnowledgeStoreMcpEnvironmentCollection.Name)]
public sealed class WikiMcpToolsTests : IDisposable
{
    private readonly string _tempDir;

    public WikiMcpToolsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-mcp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("ST_WIKI_LIBRARY_PATH", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ST_WIKI_LIBRARY_PATH", null);
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task RootCreate_AndListRoots_UseConfiguredWikiLibrary()
    {
        var createJson = await WikiMcpTools.WikiRootCreate("Research");
        using var createDoc = JsonDocument.Parse(createJson);

        Assert.True(createDoc.RootElement.GetProperty("ok").GetBoolean());
        var root = createDoc.RootElement.GetProperty("root");
        Assert.Equal("Research", root.GetProperty("name").GetString());
        Assert.StartsWith(Path.GetFullPath(_tempDir), root.GetProperty("path").GetString());

        var listJson = await WikiMcpTools.WikiRootsList();
        using var listDoc = JsonDocument.Parse(listJson);

        Assert.True(listDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(Path.GetFullPath(_tempDir), listDoc.RootElement.GetProperty("library_directory").GetString());
        Assert.Single(listDoc.RootElement.GetProperty("roots").EnumerateArray());
    }

    [Fact]
    public async Task PageCreate_Search_AndRead_ReturnMarkdown()
    {
        var rootId = await CreateRootAsync();
        var createJson = await WikiMcpTools.WikiPageCreate(
            rootId,
            "Launch Notes",
            "# Launch Notes\n\nTelemetry review and rollout checklist.");
        using var createDoc = JsonDocument.Parse(createJson);
        var pageId = createDoc.RootElement.GetProperty("document").GetProperty("page").GetProperty("id").GetString()!;

        var searchJson = await WikiMcpTools.WikiSearch("telemetry", rootId);
        using var searchDoc = JsonDocument.Parse(searchJson);

        Assert.True(searchDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, searchDoc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(pageId, searchDoc.RootElement.GetProperty("results")[0].GetProperty("page_id").GetString());

        var readJson = await WikiMcpTools.WikiPageRead(pageId);
        using var readDoc = JsonDocument.Parse(readJson);

        Assert.True(readDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Telemetry review", readDoc.RootElement.GetProperty("document").GetProperty("markdown").GetString());
        Assert.Equal(1, readDoc.RootElement.GetProperty("document").GetProperty("page").GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task PageUpdate_UsesExpectedVersion_AndReportsConflicts()
    {
        var rootId = await CreateRootAsync();
        var createJson = await WikiMcpTools.WikiPageCreate(rootId, "Runbook", "# Runbook\n\nOriginal");
        using var createDoc = JsonDocument.Parse(createJson);
        var pageId = createDoc.RootElement.GetProperty("document").GetProperty("page").GetProperty("id").GetString()!;

        var updateJson = await WikiMcpTools.WikiPageUpdate(pageId, "# Runbook\n\nUpdated", 1, "MCP update");
        using var updateDoc = JsonDocument.Parse(updateJson);

        Assert.True(updateDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, updateDoc.RootElement.GetProperty("document").GetProperty("page").GetProperty("version").GetInt64());

        var staleJson = await WikiMcpTools.WikiPageUpdate(pageId, "# Runbook\n\nStale", 1, "stale update");
        using var staleDoc = JsonDocument.Parse(staleJson);

        Assert.False(staleDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("version", staleDoc.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PagePatchSelection_ReplacesOnlySelectedText_AndCreatesNewVersion()
    {
        var rootId = await CreateRootAsync();
        var createJson = await WikiMcpTools.WikiPageCreate(rootId, "Runbook", "# Runbook\n\nOriginal step.\n\nKeep this line.");
        using var createDoc = JsonDocument.Parse(createJson);
        var pageId = createDoc.RootElement.GetProperty("document").GetProperty("page").GetProperty("id").GetString()!;

        var patchJson = await WikiMcpTools.WikiPagePatchSelection(
            pageId,
            "Original step.",
            "Updated step.",
            1,
            "Selection patch");
        using var patchDoc = JsonDocument.Parse(patchJson);

        Assert.True(patchDoc.RootElement.GetProperty("ok").GetBoolean());
        var document = patchDoc.RootElement.GetProperty("document");
        Assert.Equal(2, document.GetProperty("page").GetProperty("version").GetInt64());
        Assert.Contains("Updated step.", document.GetProperty("markdown").GetString());
        Assert.Contains("Keep this line.", document.GetProperty("markdown").GetString());
        Assert.DoesNotContain("Original step.", document.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task PagePatchSelection_RejectsAmbiguousSelectedText()
    {
        var rootId = await CreateRootAsync();
        var createJson = await WikiMcpTools.WikiPageCreate(rootId, "Runbook", "Repeat me.\n\nRepeat me.");
        using var createDoc = JsonDocument.Parse(createJson);
        var pageId = createDoc.RootElement.GetProperty("document").GetProperty("page").GetProperty("id").GetString()!;

        var patchJson = await WikiMcpTools.WikiPagePatchSelection(
            pageId,
            "Repeat me.",
            "Updated.",
            1,
            "Ambiguous patch");
        using var patchDoc = JsonDocument.Parse(patchJson);

        Assert.False(patchDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("more than once", patchDoc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RootFolderAndPageRenameMove_UpdateWikiOrganization()
    {
        var rootId = await CreateRootAsync();

        var renameRootJson = await WikiMcpTools.WikiRootRename(rootId, "Renamed Wiki");
        using var renameRootDoc = JsonDocument.Parse(renameRootJson);
        Assert.True(renameRootDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Renamed Wiki", renameRootDoc.RootElement.GetProperty("root").GetProperty("name").GetString());

        var folderId = await CreateFolderAsync(rootId, "Projects");
        var archiveId = await CreateFolderAsync(rootId, "Archive");
        var pageId = await CreatePageAsync(rootId, folderId, "Launch Notes", "# Launch Notes\n\nBody.");

        var renameFolderJson = await WikiMcpTools.WikiFolderRename(rootId, folderId, "Active Projects");
        using var renameFolderDoc = JsonDocument.Parse(renameFolderJson);
        Assert.True(renameFolderDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("active-projects", renameFolderDoc.RootElement.GetProperty("folder").GetProperty("slug").GetString());

        var moveFolderJson = await WikiMcpTools.WikiFolderMove(rootId, folderId, archiveId);
        using var moveFolderDoc = JsonDocument.Parse(moveFolderJson);
        Assert.True(moveFolderDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(archiveId, moveFolderDoc.RootElement.GetProperty("folder").GetProperty("parent_folder_id").GetString());

        var renamePageJson = await WikiMcpTools.WikiPageRename(pageId, "Launch Plan", 1);
        using var renamePageDoc = JsonDocument.Parse(renamePageJson);
        Assert.True(renamePageDoc.RootElement.GetProperty("ok").GetBoolean());
        var renamedPage = renamePageDoc.RootElement.GetProperty("document").GetProperty("page");
        Assert.Equal(2, renamedPage.GetProperty("version").GetInt64());
        Assert.Equal(Path.Combine("archive", "active-projects", "launch-plan.md"), renamedPage.GetProperty("relative_path").GetString());

        var movePageJson = await WikiMcpTools.WikiPageMove(pageId, null, 2);
        using var movePageDoc = JsonDocument.Parse(movePageJson);
        Assert.True(movePageDoc.RootElement.GetProperty("ok").GetBoolean());
        var movedPage = movePageDoc.RootElement.GetProperty("document").GetProperty("page");
        Assert.Equal(3, movedPage.GetProperty("version").GetInt64());
        Assert.Equal(JsonValueKind.Null, movedPage.GetProperty("folder_id").ValueKind);
        Assert.Equal("launch-plan.md", movedPage.GetProperty("relative_path").GetString());

        var staleMoveJson = await WikiMcpTools.WikiPageMove(pageId, archiveId, 2);
        using var staleMoveDoc = JsonDocument.Parse(staleMoveJson);
        Assert.False(staleMoveDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("version", staleMoveDoc.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PageRevisionsList_AndRevisionRestore_UseExpectedVersion()
    {
        var rootId = await CreateRootAsync();
        var pageId = await CreatePageAsync(rootId, null, "Runbook", "# Runbook\n\nOriginal");

        var updateJson = await WikiMcpTools.WikiPageUpdate(pageId, "# Runbook\n\nUpdated", 1, "MCP update");
        using var updateDoc = JsonDocument.Parse(updateJson);
        Assert.True(updateDoc.RootElement.GetProperty("ok").GetBoolean());

        var revisionsJson = await WikiMcpTools.WikiPageRevisionsList(pageId, maxItems: 10);
        using var revisionsDoc = JsonDocument.Parse(revisionsJson);
        Assert.True(revisionsDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, revisionsDoc.RootElement.GetProperty("revision_count").GetInt32());
        Assert.Equal(2, revisionsDoc.RootElement.GetProperty("revisions")[0].GetProperty("version").GetInt64());

        var originalRevisionId = revisionsDoc.RootElement
            .GetProperty("revisions")
            .EnumerateArray()
            .Single(revision => revision.GetProperty("version").GetInt64() == 1)
            .GetProperty("id")
            .GetString()!;

        var restoreJson = await WikiMcpTools.WikiPageRevisionRestore(pageId, originalRevisionId, 2);
        using var restoreDoc = JsonDocument.Parse(restoreJson);
        Assert.True(restoreDoc.RootElement.GetProperty("ok").GetBoolean());
        var restored = restoreDoc.RootElement.GetProperty("document");
        Assert.Equal(3, restored.GetProperty("page").GetProperty("version").GetInt64());
        Assert.Contains("Original", restored.GetProperty("markdown").GetString());
        Assert.DoesNotContain("Updated", restored.GetProperty("markdown").GetString());

        var staleRestoreJson = await WikiMcpTools.WikiPageRevisionRestore(pageId, originalRevisionId, 2);
        using var staleRestoreDoc = JsonDocument.Parse(staleRestoreJson);
        Assert.False(staleRestoreDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("version", staleRestoreDoc.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FolderAndPageDelete_RemoveWikiContent()
    {
        var rootId = await CreateRootAsync();
        var parentId = await CreateFolderAsync(rootId, "Projects");
        var childId = await CreateFolderAsync(rootId, "Design", parentId);
        var nestedPageId = await CreatePageAsync(rootId, childId, "Canvas Notes", "# Canvas Notes\n\nNested only.");

        var deleteFolderJson = await WikiMcpTools.WikiFolderDelete(rootId, parentId);
        using var deleteFolderDoc = JsonDocument.Parse(deleteFolderJson);
        Assert.True(deleteFolderDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(deleteFolderDoc.RootElement.GetProperty("deleted").GetBoolean());

        var readNestedJson = await WikiMcpTools.WikiPageRead(nestedPageId);
        using var readNestedDoc = JsonDocument.Parse(readNestedJson);
        Assert.False(readNestedDoc.RootElement.GetProperty("ok").GetBoolean());

        var pageId = await CreatePageAsync(rootId, null, "Root Page", "# Root Page\n\nStandalone.");

        var staleDeleteJson = await WikiMcpTools.WikiPageDelete(pageId, expectedVersion: 99);
        using var staleDeleteDoc = JsonDocument.Parse(staleDeleteJson);
        Assert.False(staleDeleteDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("version", staleDeleteDoc.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        var deletePageJson = await WikiMcpTools.WikiPageDelete(pageId, expectedVersion: 1);
        using var deletePageDoc = JsonDocument.Parse(deletePageJson);
        Assert.True(deletePageDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(deletePageDoc.RootElement.GetProperty("deleted").GetBoolean());

        var readDeletedJson = await WikiMcpTools.WikiPageRead(pageId);
        using var readDeletedDoc = JsonDocument.Parse(readDeletedJson);
        Assert.False(readDeletedDoc.RootElement.GetProperty("ok").GetBoolean());
    }

    private static async Task<string> CreateRootAsync()
    {
        var json = await WikiMcpTools.WikiRootCreate("Harness Wiki");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        return doc.RootElement.GetProperty("root").GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateFolderAsync(string rootId, string name, string? parentFolderId = null)
    {
        var json = await WikiMcpTools.WikiFolderCreate(rootId, name, parentFolderId);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        return doc.RootElement.GetProperty("folder").GetProperty("id").GetString()!;
    }

    private static async Task<string> CreatePageAsync(string rootId, string? folderId, string title, string markdown)
    {
        var json = await WikiMcpTools.WikiPageCreate(rootId, title, markdown, folderId);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        return doc.RootElement.GetProperty("document").GetProperty("page").GetProperty("id").GetString()!;
    }
}