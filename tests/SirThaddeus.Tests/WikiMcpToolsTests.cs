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

    private static async Task<string> CreateRootAsync()
    {
        var json = await WikiMcpTools.WikiRootCreate("Harness Wiki");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        return doc.RootElement.GetProperty("root").GetProperty("id").GetString()!;
    }
}