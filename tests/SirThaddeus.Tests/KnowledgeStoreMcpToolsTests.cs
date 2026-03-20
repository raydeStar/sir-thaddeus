using System.Text.Json;
using SirThaddeus.Config;
using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Tests;

[Collection(KnowledgeStoreMcpEnvironmentCollection.Name)]
public sealed class KnowledgeStoreMcpToolsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly string _auditPath;

    public KnowledgeStoreMcpToolsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-mcp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _auditPath = Path.Combine(_tempDir, "audit.jsonl");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ST_SETTINGS_PATH", null);
        Environment.SetEnvironmentVariable("ST_AUDIT_PATH", null);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void ListRoots_ReturnsConfiguredKnowledgeRoots()
    {
        var rootPath = CreateRootFolder("wiki");
        WriteSettings(new AppSettings
        {
            KnowledgeStore = new KnowledgeStoreSettings
            {
                Enabled = true,
                Roots =
                [
                    new KnowledgeStoreRootConfig
                    {
                        Id = "harness",
                        DisplayName = "Harness Knowledge Store",
                        AbsolutePath = rootPath,
                        AccessLevel = "KnowledgeReadWrite",
                        AllowIndexing = true,
                        ConfirmWrites = false
                    }
                ]
            }
        });

        var json = KnowledgeStoreMcpTools.KnowledgeStoreListRoots();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var root = doc.RootElement.GetProperty("roots")[0];
        Assert.Equal("harness", root.GetProperty("id").GetString());
        Assert.Equal("Harness Knowledge Store", root.GetProperty("display_name").GetString());
        Assert.Equal(Path.GetFullPath(rootPath), root.GetProperty("absolute_path").GetString());
        Assert.False(root.GetProperty("confirm_writes").GetBoolean());
    }

    [Fact]
    public async Task ReadFile_UsesSingleConfiguredRootWhenRootIdOmitted()
    {
        var rootPath = CreateRootFolder("single-root");
        var notePath = Path.Combine(rootPath, "projects", "note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);
        await File.WriteAllTextAsync(notePath, "single root content");

        WriteSettings(SingleRootSettings(rootPath));

        var json = await KnowledgeStoreMcpTools.KnowledgeStoreReadFile("projects/note.md");
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Read successful.", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("single root content", doc.RootElement.GetProperty("content").GetString());
        Assert.Equal("projects/note.md", doc.RootElement.GetProperty("file_path").GetString());
    }

    [Fact]
    public async Task ListFiles_RequiresRootIdWhenMultipleRootsConfigured()
    {
        var firstRoot = CreateRootFolder("root-one");
        var secondRoot = CreateRootFolder("root-two");
        WriteSettings(new AppSettings
        {
            KnowledgeStore = new KnowledgeStoreSettings
            {
                Enabled = true,
                Roots =
                [
                    Root("alpha", "Alpha", firstRoot),
                    Root("beta", "Beta", secondRoot)
                ]
            }
        });

        var json = await KnowledgeStoreMcpTools.KnowledgeStoreListFiles("projects");
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "A rootId is required when multiple knowledge-store roots are configured.",
            doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReadFile_ReturnsDisabledMessageWhenKnowledgeStoreIsOff()
    {
        WriteSettings(new AppSettings
        {
            KnowledgeStore = new KnowledgeStoreSettings
            {
                Enabled = false,
                Roots = []
            }
        });

        var json = await KnowledgeStoreMcpTools.KnowledgeStoreReadFile("projects/note.md", "harness");
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Knowledge store is disabled in settings.", doc.RootElement.GetProperty("message").GetString());
    }

    private AppSettings SingleRootSettings(string rootPath)
    {
        return new AppSettings
        {
            KnowledgeStore = new KnowledgeStoreSettings
            {
                Enabled = true,
                Roots = [Root("harness", "Harness Knowledge Store", rootPath)]
            }
        };
    }

    private KnowledgeStoreRootConfig Root(string id, string displayName, string absolutePath)
    {
        return new KnowledgeStoreRootConfig
        {
            Id = id,
            DisplayName = displayName,
            AbsolutePath = absolutePath,
            AccessLevel = "KnowledgeReadWrite",
            AllowIndexing = true,
            ConfirmWrites = false
        };
    }

    private string CreateRootFolder(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteSettings(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
        Environment.SetEnvironmentVariable("ST_SETTINGS_PATH", _settingsPath);
        Environment.SetEnvironmentVariable("ST_AUDIT_PATH", _auditPath);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KnowledgeStoreMcpEnvironmentCollection
{
    public const string Name = "KnowledgeStoreMcpEnvironment";
}