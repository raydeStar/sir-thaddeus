using SirThaddeus.AuditLog;
using SirThaddeus.KnowledgeStore;
using Xunit.Abstractions;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for the Knowledge Store Guard — validates all file operations.
/// </summary>
public sealed class KnowledgeStoreGuardTests : IDisposable
{
    private readonly string _tempDir;
    private readonly KnowledgeStoreGuard _guard;
    private readonly StorePolicy _policy;

    public KnowledgeStoreGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-guard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _policy = new StorePolicy();
        _guard = new KnowledgeStoreGuard(_policy);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private WorkspaceRoot KnowledgeRoot() => new()
    {
        AbsolutePath = _tempDir,
        AccessLevel = WorkspaceAccessLevel.KnowledgeReadWrite
    };

    private WorkspaceRoot ReferenceRoot() => new()
    {
        AbsolutePath = _tempDir,
        AccessLevel = WorkspaceAccessLevel.ReferenceReadOnly
    };

    [Fact]
    public void PathTraversal_Blocked()
    {
        var result = _guard.Validate(KnowledgeRoot(), "read", "../../.ssh/id_rsa");
        Assert.False(result.IsAllowed);
        Assert.Contains("escapes", result.Reason);
    }

    [Fact]
    public void SiblingRootPrefixEscape_Blocked()
    {
        var sibling = _tempDir + "-sibling";
        var relative = Path.GetRelativePath(_tempDir, Path.Combine(sibling, "note.md"));

        var result = _guard.Validate(KnowledgeRoot(), "read", relative);
        Assert.False(result.IsAllowed);
        Assert.Contains("escapes", result.Reason);
    }

    [Fact]
    public void WriteToReferenceRoot_Blocked()
    {
        var result = _guard.Validate(ReferenceRoot(), "create", "test.md", "content");
        Assert.False(result.IsAllowed);
        Assert.Contains("read-only", result.Reason);
    }

    [Fact]
    public void ReadFromReferenceRoot_Allowed()
    {
        var result = _guard.Validate(ReferenceRoot(), "read", "readme.md");
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void DeleteOperation_Blocked()
    {
        var result = _guard.Validate(KnowledgeRoot(), "delete", "file.md");
        Assert.False(result.IsAllowed);
        Assert.Contains("manual only", result.Reason);
    }

    [Fact]
    public void WriteNonMdFile_Blocked()
    {
        var result = _guard.Validate(KnowledgeRoot(), "create", "script.exe", "binary");
        Assert.False(result.IsAllowed);
        Assert.Contains(".md", result.Reason);
    }

    [Fact]
    public void WritePyFile_Blocked()
    {
        var result = _guard.Validate(KnowledgeRoot(), "create", "script.py", "print('pwn')");
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void WriteMdFile_Allowed()
    {
        var result = _guard.Validate(KnowledgeRoot(), "create", "notes.md", "# My Notes");
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void ReadUnsupportedExtension_Blocked()
    {
        var result = _guard.Validate(KnowledgeRoot(), "read", "data.xlsx");
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void ReadSupportedExtensions_Allowed()
    {
        foreach (var ext in new[] { ".md", ".txt", ".json", ".pdf", ".docx" })
        {
            var result = _guard.Validate(KnowledgeRoot(), "read", $"file{ext}");
            Assert.True(result.IsAllowed, $"Should allow reading {ext} files");
        }
    }

    [Fact]
    public void ExcessiveFolderDepth_Blocked()
    {
        var path = "a/b/c/d/too-deep.md"; // depth 4, limit is 3
        var result = _guard.Validate(KnowledgeRoot(), "create", path, "content");
        Assert.False(result.IsAllowed);
        Assert.Contains("depth", result.Reason);
    }

    [Fact]
    public void AllowedFolderDepth_Allowed()
    {
        var path = "a/b/c/ok.md"; // depth 3 (exactly at limit)
        var result = _guard.Validate(KnowledgeRoot(), "create", path, "content");
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void OversizedContent_Blocked()
    {
        var content = new string('x', 600 * 1024); // 600 KB > 512 KB limit
        var result = _guard.Validate(KnowledgeRoot(), "create", "big.md", content);
        Assert.False(result.IsAllowed);
        Assert.Contains("limit", result.Reason);
    }

    [Fact]
    public void InstructionFileDirectWrite_Blocked()
    {
        var result = _guard.Validate(KnowledgeRoot(), "create", "_instructions.md", "rules");
        Assert.False(result.IsAllowed);
        Assert.Contains("confirmation flow", result.Reason);
    }

    [Fact]
    public void InstructionFileRead_Allowed()
    {
        var result = _guard.Validate(KnowledgeRoot(), "read", "_instructions.md");
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void InstructionFileConfirmedWrite_Allowed()
    {
        var result = _guard.Validate(
            KnowledgeRoot(), "write_instruction_confirmed", "_instructions.md", "rules");
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void ConfirmedInstructionWrite_NonInstructionFile_Blocked()
    {
        var result = _guard.Validate(
            KnowledgeRoot(), "write_instruction_confirmed", "notes.md", "rules");
        Assert.False(result.IsAllowed);
        Assert.Contains("_instructions.md", result.Reason);
    }

    [Fact]
    public void ConfirmedInstructionWrite_OnReferenceRoot_Blocked()
    {
        var result = _guard.Validate(
            ReferenceRoot(), "write_instruction_confirmed", "_instructions.md", "rules");
        Assert.False(result.IsAllowed);
        Assert.Contains("read-only", result.Reason);
    }

    [Fact]
    public void FileLimitReached_CreateBlocked()
    {
        // Create MaxFilesPerFolder files in a subfolder
        var subDir = Path.Combine(_tempDir, "full");
        Directory.CreateDirectory(subDir);

        var lowPolicy = new StorePolicy { MaxFilesPerFolder = 3 };
        var guard = new KnowledgeStoreGuard(lowPolicy);

        for (int i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(subDir, $"file{i}.md"), "x");

        var result = guard.Validate(KnowledgeRoot(), "create", "full/file3.md", "content");
        Assert.False(result.IsAllowed);
        Assert.Contains("Archive", result.Reason);
    }
}

/// <summary>
/// Tests for file naming policy.
/// </summary>
public sealed class FileNamingPolicyTests
{
    private readonly FileNamingPolicy _policy = new();

    [Fact]
    public void Journal_GeneratesDateBasedName()
    {
        var intent = new FileCreationIntent { Date = new DateTime(2026, 3, 19) };
        var name = _policy.GenerateFileName("journal", intent);
        Assert.Equal("2026-03-19.md", name);
    }

    [Fact]
    public void Bloodwork_GeneratesPrefixedDateName()
    {
        var intent = new FileCreationIntent
        {
            Date = new DateTime(2026, 1, 15),
            SubType = "bloodwork"
        };
        var name = _policy.GenerateFileName("health", intent);
        Assert.Equal("bloodwork-2026-01-15.md", name);
    }

    [Fact]
    public void Sleep_GeneratesRollingFileName()
    {
        var intent = new FileCreationIntent { SubType = "sleep" };
        var name = _policy.GenerateFileName("health", intent);
        Assert.Equal("sleep-log.md", name);
    }

    [Fact]
    public void GenericDomain_SanitizesToKebabCase()
    {
        var intent = new FileCreationIntent { ProposedName = "My Cool Project Notes!" };
        var name = _policy.GenerateFileName("projects", intent);
        Assert.Equal("my-cool-project-notes.md", name);
    }

    [Fact]
    public void SanitizeToKebabCase_HandlesSpecialChars()
    {
        Assert.Equal("hello-world", FileNamingPolicy.SanitizeToKebabCase("Hello World!"));
        Assert.Equal("test-123", FileNamingPolicy.SanitizeToKebabCase("Test---123"));
        Assert.Equal("no-extra-dashes", FileNamingPolicy.SanitizeToKebabCase("  no  extra  dashes  "));
    }
}

/// <summary>
/// Tests for the file conflict resolver.
/// </summary>
public sealed class FileConflictResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileConflictResolver _resolver = new();

    public FileConflictResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-conflict-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void NewFile_ReturnsCreate()
    {
        var path = Path.Combine(_tempDir, "new.md");
        Assert.Equal(WriteAction.Create, _resolver.ResolveConflict(path, "content"));
    }

    [Fact]
    public void DuplicateContent_ReturnsSkip()
    {
        var path = Path.Combine(_tempDir, "existing.md");
        File.WriteAllText(path, "# Header\nSame content here.");

        Assert.Equal(WriteAction.SkipDuplicate,
            _resolver.ResolveConflict(path, "Same content here."));
    }

    [Fact]
    public void DifferentContent_ReturnsAppend()
    {
        var path = Path.Combine(_tempDir, "existing.md");
        File.WriteAllText(path, "# Header\nOld content.");

        Assert.Equal(WriteAction.Append,
            _resolver.ResolveConflict(path, "New different content."));
    }

    [Fact]
    public void SimilarFileName_Detected()
    {
        File.WriteAllText(Path.Combine(_tempDir, "character-sheet.md"), "x");

        var similar = _resolver.FindSimilarFile(_tempDir, "character-sheet-v2.md");
        // "character-sheet" vs "character-sheet-v2" → 2/3 overlap = 66%
        Assert.NotNull(similar);
    }

    [Fact]
    public void DifferentFileName_NoMatch()
    {
        File.WriteAllText(Path.Combine(_tempDir, "character-sheet.md"), "x");

        var similar = _resolver.FindSimilarFile(_tempDir, "world-map.md");
        Assert.Null(similar);
    }
}

/// <summary>
/// Tests for the Knowledge Store Tools.
/// </summary>
public sealed class KnowledgeStoreToolsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceRoot _root;
    private readonly KnowledgeStoreTools _tools;
    private readonly TestAuditLogger _audit;

    public KnowledgeStoreToolsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-tools-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _root = new WorkspaceRoot
        {
            Id = "test-root",
            DisplayName = "Test Knowledge",
            AbsolutePath = _tempDir,
            AccessLevel = WorkspaceAccessLevel.KnowledgeReadWrite
        };

        _audit = new TestAuditLogger();
        _tools = new KnowledgeStoreTools(
            [_root],
            new KnowledgeStoreGuard(new StorePolicy()),
            new FileConflictResolver(),
            new TaggingQueue(),
            _audit);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task CreateFile_WritesToDisk()
    {
        var result = await _tools.CreateFileAsync("test-root", "notes.md", "# My Notes");
        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_tempDir, "notes.md")));
    }

    [Fact]
    public async Task CreateFile_CreatesSubdirectory()
    {
        var result = await _tools.CreateFileAsync("test-root", "journal/2026-03-19.md", "# Today");
        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_tempDir, "journal", "2026-03-19.md")));
    }

    [Fact]
    public async Task AppendToFile_AppendsContent()
    {
        var path = Path.Combine(_tempDir, "log.md");
        File.WriteAllText(path, "# Log\n");

        var result = await _tools.AppendToFileAsync("test-root", "log.md", "- Entry 1");
        Assert.True(result.Success);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("Entry 1", content);
        Assert.Contains("# Log", content);
    }

    [Fact]
    public async Task AppendToFile_NonexistentFile_Fails()
    {
        var result = await _tools.AppendToFileAsync("test-root", "nonexistent.md", "content");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReadFile_ReturnsContent()
    {
        File.WriteAllText(Path.Combine(_tempDir, "readme.md"), "Hello world");

        var result = await _tools.ReadFileAsync("test-root", "readme.md");
        Assert.True(result.Success);
        Assert.Equal("Hello world", result.Content);
    }

    [Fact]
    public async Task ReadFile_Nonexistent_Fails()
    {
        var result = await _tools.ReadFileAsync("test-root", "ghost.md");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UpdateSection_ReplacesContent()
    {
        var path = Path.Combine(_tempDir, "sheet.md");
        File.WriteAllText(path, "# Character\nHP: 100\nGold: 50");

        var result = await _tools.UpdateSectionAsync(
            "test-root", "sheet.md", "HP: 100", "HP: 85");

        Assert.True(result.Success);
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("HP: 85", content);
        Assert.DoesNotContain("HP: 100", content);
    }

    [Fact]
    public async Task UpdateSection_MissingContent_Fails()
    {
        var path = Path.Combine(_tempDir, "sheet.md");
        File.WriteAllText(path, "# Character\nHP: 100");

        var result = await _tools.UpdateSectionAsync(
            "test-root", "sheet.md", "MP: 50", "MP: 30");

        Assert.False(result.Success);
        Assert.Contains("find the section", result.Message);
    }

    [Fact]
    public async Task ListFiles_ReturnsDirectoryContents()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "journal"));
        File.WriteAllText(Path.Combine(_tempDir, "journal", "day1.md"), "x");
        File.WriteAllText(Path.Combine(_tempDir, "journal", "day2.md"), "y");

        var result = await _tools.ListFilesAsync("test-root", "journal");
        Assert.True(result.Success);
        Assert.Contains("day1.md", result.Content);
        Assert.Contains("day2.md", result.Content);
    }

    [Fact]
    public async Task UnknownRoot_Fails()
    {
        var result = await _tools.ReadFileAsync("nonexistent-root", "file.md");
        Assert.False(result.Success);
        Assert.Contains("Unknown root", result.Message);
    }

    [Fact]
    public async Task AllOperations_AreAudited()
    {
        await _tools.CreateFileAsync("test-root", "audited.md", "content");
        await _tools.ReadFileAsync("test-root", "audited.md");
        await _tools.AppendToFileAsync("test-root", "audited.md", "more");

        Assert.True(_audit.Events.Count >= 3);
        Assert.Contains(_audit.Events, e => e.Action == "KNOWLEDGE_CREATE");
        Assert.Contains(_audit.Events, e => e.Action == "KNOWLEDGE_READ");
        Assert.Contains(_audit.Events, e => e.Action == "KNOWLEDGE_APPEND");
    }

    [Fact]
    public async Task PathTraversal_IsAuditedAsDenied()
    {
        await _tools.ReadFileAsync("test-root", "../../etc/passwd");

        Assert.Contains(_audit.Events, e =>
            e.Result == "denied" && e.Action.Contains("DENIED"));
    }

    [Fact]
    public async Task ListFiles_PathTraversal_Fails()
    {
        var result = await _tools.ListFilesAsync("test-root", "../../outside");

        Assert.False(result.Success);
        Assert.Contains("escapes", result.Message);
        Assert.Contains(_audit.Events, e => e.Action == "KNOWLEDGE_LIST_DENIED");
    }

    [Fact]
    public async Task WriteInstructionFile_NonInstructionPath_Fails()
    {
        var result = await _tools.WriteInstructionFileAsync("test-root", "notes.md", "bad");

        Assert.False(result.Success);
        Assert.Contains("_instructions.md", result.Message);
    }
}

/// <summary>
/// Tests for the YAML frontmatter parser.
/// </summary>
public sealed class FrontmatterParserTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FrontmatterParser _parser = new();

    public FrontmatterParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-fm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ReadFrontmatter_ValidYaml_Parsed()
    {
        var path = Path.Combine(_tempDir, "test.md");
        File.WriteAllText(path, """
            ---
            tags: [combat, chapter-3]
            mentions: [ennix, lyra]
            summary: "Ennix leads the assault."
            created: 2026-03-19
            updated: 2026-03-19
            type: scene
            ---

            # Chapter 3
            Body content here.
            """);

        var fm = await _parser.ReadFrontmatterOnlyAsync(path);
        Assert.NotNull(fm);
        Assert.Equal(["combat", "chapter-3"], fm.Tags);
        Assert.Equal(["ennix", "lyra"], fm.Mentions);
        Assert.Equal("Ennix leads the assault.", fm.Summary);
        Assert.Equal("scene", fm.Type);
    }

    [Fact]
    public async Task ReadFrontmatter_NoFrontmatter_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "noheader.md");
        File.WriteAllText(path, "# Just a title\nNo frontmatter here.");

        var fm = await _parser.ReadFrontmatterOnlyAsync(path);
        Assert.Null(fm);
    }

    [Fact]
    public async Task ReadBody_SkipsFrontmatter()
    {
        var path = Path.Combine(_tempDir, "withbody.md");
        File.WriteAllText(path, """
            ---
            tags: [test]
            summary: "A test"
            ---
            # Title
            Body content.
            """);

        var body = await _parser.ReadBodyAsync(path);
        Assert.Contains("# Title", body);
        Assert.Contains("Body content.", body);
        Assert.DoesNotContain("tags:", body);
    }

    [Fact]
    public async Task WriteFrontmatter_PreservesBody()
    {
        var path = Path.Combine(_tempDir, "write.md");
        File.WriteAllText(path, "# Title\nBody here.");

        var fm = new Frontmatter
        {
            Tags = ["test", "demo"],
            Summary = "A test file.",
            Type = "note"
        };

        await _parser.WriteFrontmatterAsync(path, fm);

        var content = await File.ReadAllTextAsync(path);
        Assert.StartsWith("---\n", content);
        Assert.Contains("tags:", content);
        Assert.Contains("test", content);
        Assert.Contains("# Title", content);
        Assert.Contains("Body here.", content);
    }
}

/// <summary>
/// Tests for the tag index.
/// </summary>
public sealed class TagIndexTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FrontmatterParser _parser = new();

    public TagIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-index-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private void WriteTaggedFile(string relativePath, string tags, string mentions, string summary)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, $"""
            ---
            tags: [{tags}]
            mentions: [{mentions}]
            summary: "{summary}"
            created: 2026-03-19
            updated: 2026-03-19
            type: note
            ---

            # Content
            """);
    }

    [Fact]
    public async Task Build_IndexesAllFiles()
    {
        WriteTaggedFile("file1.md", "combat, adventure", "ennix", "First file");
        WriteTaggedFile("file2.md", "combat, magic", "lyra", "Second file");
        WriteTaggedFile("sub/file3.md", "adventure", "ennix, lyra", "Third file");

        var index = new TagIndex(_parser);
        await index.BuildAsync(_tempDir);

        Assert.Equal(3, index.AllEntries.Count);
        Assert.Equal(2, index.FindByTag("combat").Count);
        Assert.Equal(2, index.FindByTag("adventure").Count);
        Assert.Single(index.FindByTag("magic"));
        Assert.Equal(2, index.FindByMention("ennix").Count);
        Assert.Equal(2, index.FindByMention("lyra").Count);
    }

    [Fact]
    public async Task Build_SkipsInstructionFiles()
    {
        WriteTaggedFile("file1.md", "test", "", "Regular file");
        File.WriteAllText(Path.Combine(_tempDir, "_instructions.md"), "---\ntags: [system]\n---\nRules");

        var index = new TagIndex(_parser);
        await index.BuildAsync(_tempDir);

        Assert.Single(index.AllEntries);
    }

    [Fact]
    public async Task UpsertEntry_UpdatesExisting()
    {
        WriteTaggedFile("file1.md", "old-tag", "", "Old summary");

        var index = new TagIndex(_parser);
        await index.BuildAsync(_tempDir);

        Assert.Single(index.FindByTag("old-tag"));

        // Upsert with new tags
        index.UpsertEntry(new IndexEntry
        {
            RelativePath = "file1.md",
            Summary = "New summary",
            Tags = ["new-tag"],
            Mentions = [],
            Type = "note"
        });

        Assert.Empty(index.FindByTag("old-tag"));
        Assert.Single(index.FindByTag("new-tag"));
        Assert.Single(index.AllEntries);
    }
}

/// <summary>
/// Tests for the knowledge domain router.
/// </summary>
public sealed class KnowledgeDomainRouterTests : IDisposable
{
    private readonly string _tempDir;

    public KnowledgeDomainRouterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-router-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "journal"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "health"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "games"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void ExplicitDomainReference_MatchesFirst()
    {
        var router = new KnowledgeDomainRouter(_tempDir);
        var result = router.Route("Add this to my journal please");
        Assert.Equal("journal", result.Domain);
        Assert.Equal(DomainConfidence.ExplicitReference, result.Confidence);
    }

    [Fact]
    public void KeywordMatch_Journal()
    {
        // Use a root with no matching folder names to avoid explicit match
        var emptyRoot = Path.Combine(Path.GetTempPath(), "ks-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(emptyRoot);
        try
        {
            var router = new KnowledgeDomainRouter(emptyRoot);
            var result = router.Route("I ate 500 calories today");
            Assert.Equal("journal", result.Domain);
        }
        finally
        {
            Directory.Delete(emptyRoot, true);
        }
    }

    [Fact]
    public void KeywordMatch_Health()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "ks-empty2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(emptyRoot);
        try
        {
            var router = new KnowledgeDomainRouter(emptyRoot);
            var result = router.Route("My vitamin D bloodwork came back");
            Assert.Equal("health", result.Domain);
            Assert.Equal(DomainConfidence.StrongKeywordMatch, result.Confidence);
        }
        finally
        {
            Directory.Delete(emptyRoot, true);
        }
    }

    [Fact]
    public void SessionContinuity_MaintainsDomain()
    {
        var router = new KnowledgeDomainRouter(_tempDir);

        // First message establishes domain
        router.Route("Add to my journal: woke up early");
        Assert.Equal("journal", router.ActiveSessionDomain);

        // Next message without keywords continues in same domain
        var result = router.Route("Also had a nice walk.");
        Assert.Equal("journal", result.Domain);
        Assert.Equal(DomainConfidence.SessionContinuity, result.Confidence);
    }

    [Fact]
    public void ClearSession_ResetsState()
    {
        var router = new KnowledgeDomainRouter(_tempDir);
        router.Route("journal entry: breakfast");
        Assert.NotNull(router.ActiveSessionDomain);

        router.ClearSession();
        Assert.Null(router.ActiveSessionDomain);
    }

    [Fact]
    public void NoMatch_ReturnsNone()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "ks-empty3-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(emptyRoot);
        try
        {
            var router = new KnowledgeDomainRouter(emptyRoot);
            var result = router.Route("How do you spell necessary?");
            Assert.Null(result.Domain);
            Assert.Equal(DomainConfidence.None, result.Confidence);
        }
        finally
        {
            Directory.Delete(emptyRoot, true);
        }
    }
}

/// <summary>
/// Tests for the journal handler.
/// </summary>
public sealed class JournalHandlerTests
{
    [Theory]
    [InlineData("5 PM", 17, 0)]
    [InlineData("at 5:30 PM", 17, 30)]
    [InlineData("8 AM", 8, 0)]
    [InlineData("12 PM", 12, 0)]
    [InlineData("12 AM", 0, 0)]
    [InlineData("this morning", 8, 0)]
    [InlineData("tonight", 21, 0)]
    [InlineData("noon", 12, 0)]
    public void ParseTimeHint_CorrectTime(string hint, int expectedHour, int expectedMinute)
    {
        var fallback = new DateTime(2026, 3, 19, 15, 0, 0);
        var result = JournalHandler.ParseTimeHint(hint, fallback);
        Assert.Equal(expectedHour, result.Hour);
        Assert.Equal(expectedMinute, result.Minute);
    }

    [Fact]
    public void ParseTimeHint_NullHint_ReturnsFallback()
    {
        var fallback = new DateTime(2026, 3, 19, 15, 30, 0);
        var result = JournalHandler.ParseTimeHint(null, fallback);
        Assert.Equal(fallback, result);
    }
}

/// <summary>
/// Tests for the instruction chain loader.
/// </summary>
public sealed class InstructionChainLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InstructionChainLoader _loader = new();

    public InstructionChainLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-instr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task LoadChain_RootAndDomainInstructions_Cascaded()
    {
        // Root instructions
        File.WriteAllText(Path.Combine(_tempDir, "_instructions.md"), "Root rules");

        // Domain instructions
        var domainDir = Path.Combine(_tempDir, "journal");
        Directory.CreateDirectory(domainDir);
        File.WriteAllText(Path.Combine(domainDir, "_instructions.md"), "Journal rules");

        var root = new WorkspaceRoot
        {
            AbsolutePath = _tempDir,
            AccessLevel = WorkspaceAccessLevel.KnowledgeReadWrite
        };

        var chain = await _loader.LoadChainAsync(root, "journal/2026-03-19.md");

        Assert.Contains("Root rules", chain);
        Assert.Contains("Journal rules", chain);
    }

    [Fact]
    public async Task LoadChain_ReferenceRoot_ReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_tempDir, "_instructions.md"), "Should be ignored");

        var root = new WorkspaceRoot
        {
            AbsolutePath = _tempDir,
            AccessLevel = WorkspaceAccessLevel.ReferenceReadOnly
        };

        var chain = await _loader.LoadChainAsync(root, "file.md");
        Assert.Empty(chain);
    }

    [Fact]
    public async Task LoadChain_NoInstructions_ReturnsEmpty()
    {
        var root = new WorkspaceRoot
        {
            AbsolutePath = _tempDir,
            AccessLevel = WorkspaceAccessLevel.KnowledgeReadWrite
        };

        var chain = await _loader.LoadChainAsync(root, "file.md");
        Assert.Empty(chain);
    }
}

/// <summary>
/// Tests for the time series archiver.
/// </summary>
public sealed class TimeSeriesArchiverTests : IDisposable
{
    private readonly string _tempDir;

    public TimeSeriesArchiverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-archive-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void IsDateBasedFile_ValidDates()
    {
        Assert.True(TimeSeriesArchiver.IsDateBasedFile("2026-03-19.md", out var d));
        Assert.Equal(new DateTime(2026, 3, 19), d);

        Assert.True(TimeSeriesArchiver.IsDateBasedFile("bloodwork-2026-01-15.md", out _));
    }

    [Fact]
    public void IsDateBasedFile_InvalidDates()
    {
        Assert.False(TimeSeriesArchiver.IsDateBasedFile("character-sheet.md", out _));
        Assert.False(TimeSeriesArchiver.IsDateBasedFile("readme.md", out _));
    }

    [Fact]
    public void Archive_MovesOldFiles()
    {
        var journalDir = Path.Combine(_tempDir, "journal");
        Directory.CreateDirectory(journalDir);

        // Old files (previous months)
        File.WriteAllText(Path.Combine(journalDir, "2026-01-15.md"), "January entry");
        File.WriteAllText(Path.Combine(journalDir, "2026-02-10.md"), "February entry");

        // Current month file — should NOT be moved
        var now = DateTime.UtcNow;
        var currentFile = $"{now:yyyy-MM-dd}.md";
        File.WriteAllText(Path.Combine(journalDir, currentFile), "Today's entry");

        var archiver = new TimeSeriesArchiver(new StorePolicy());
        var result = archiver.Archive(journalDir);

        Assert.True(result.Success);
        Assert.Contains("2026-01-15.md", result.MovedFiles);
        Assert.Contains("2026-02-10.md", result.MovedFiles);

        // Verify files moved to archive
        Assert.True(File.Exists(Path.Combine(journalDir, "_archive", "2026-01", "2026-01-15.md")));
        Assert.True(File.Exists(Path.Combine(journalDir, "_archive", "2026-02", "2026-02-10.md")));

        // Current month file still in place
        Assert.True(File.Exists(Path.Combine(journalDir, currentFile)));
    }

    [Fact]
    public void CheckFolder_UnderThreshold_ReturnsNone()
    {
        var dir = Path.Combine(_tempDir, "few");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file1.md"), "x");

        var archiver = new TimeSeriesArchiver(new StorePolicy { MaxFilesPerFolder = 200 });
        var rec = archiver.CheckFolder(dir);

        Assert.Equal(0, rec.TotalFiles);
    }
}

/// <summary>
/// Tests for the instruction edit handler.
/// </summary>
public sealed class InstructionEditHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceRoot _root;
    private readonly KnowledgeStoreTools _tools;
    private readonly InstructionEditHandler _handler;

    public InstructionEditHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ks-iedit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _root = new WorkspaceRoot
        {
            Id = "edit-root",
            AbsolutePath = _tempDir,
            AccessLevel = WorkspaceAccessLevel.KnowledgeReadWrite
        };

        _tools = new KnowledgeStoreTools(
            [_root],
            new KnowledgeStoreGuard(new StorePolicy()),
            new FileConflictResolver(),
            new TaggingQueue(),
            new TestAuditLogger());

        _handler = new InstructionEditHandler(_tools);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ProposeEdit_CreatesProposal()
    {
        var proposal = await _handler.ProposeEditAsync(
            "edit-root",
            "health/_instructions.md",
            "Add water tracking",
            "# Health\n- Track water intake");

        Assert.Equal(EditStatus.AwaitingConfirmation, proposal.Status);
        Assert.NotNull(_handler.PendingProposal);
    }

    [Fact]
    public async Task ConfirmEdit_WritesFile()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "health"));

        await _handler.ProposeEditAsync(
            "edit-root",
            "health/_instructions.md",
            "Add water tracking",
            "# Health\n- Track water intake");

        var result = await _handler.ConfirmAsync("edit-root");
        Assert.True(result.Success);
        Assert.Null(_handler.PendingProposal);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "health", "_instructions.md"));
        Assert.Contains("Track water intake", content);
    }

    [Fact]
    public async Task RejectEdit_ClearsProposal()
    {
        var result = _handler.Reject();
        Assert.False(result.Success); // No pending proposal

        // After a real proposal
        await _handler.ProposeEditAsync(
            "edit-root", "_instructions.md", "test", "content");

        result = _handler.Reject();
        Assert.True(result.Success);
        Assert.Null(_handler.PendingProposal);
    }
}

/// <summary>
/// Tests for the context retriever's search term extraction.
/// </summary>
public sealed class ContextRetrieverTests
{
    [Fact]
    public void ExtractSearchTerms_FindsKnownKeys()
    {
        var parser = new FrontmatterParser();
        var index = new TagIndex(parser);

        // Manually add some entries to the index
        index.UpsertEntry(new IndexEntry
        {
            RelativePath = "file1.md",
            Tags = ["ennix", "combat", "castle-siege"],
            Mentions = ["lyra", "iron-crown"],
            Summary = "Test"
        });

        var store = new FakeKnowledgeStoreTools();
        var retriever = new ContextRetriever(index, store, "root");

        var terms = retriever.ExtractSearchTerms("What do I know about Ennix and the Iron Crown?");
        Assert.Contains("ennix", terms);
        Assert.Contains("iron-crown", terms);
    }

    [Fact]
    public async Task Retrieve_TagsOnly_ReturnsFilePaths()
    {
        var parser = new FrontmatterParser();
        var index = new TagIndex(parser);

        index.UpsertEntry(new IndexEntry
        {
            RelativePath = "chapter-1.md",
            Tags = ["ennix"],
            Mentions = [],
            Summary = "Ennix introduced"
        });

        var store = new FakeKnowledgeStoreTools();
        var retriever = new ContextRetriever(index, store, "root");

        var result = await retriever.RetrieveAsync("ennix", 1000, RetrievalDepth.TagsOnly);
        Assert.Equal(1, result.MatchedFiles);
        Assert.Contains("chapter-1.md", result.FileList);
        Assert.Empty(result.Summaries);
    }

    [Fact]
    public async Task Retrieve_Summaries_ReturnsSummaryData()
    {
        var parser = new FrontmatterParser();
        var index = new TagIndex(parser);

        index.UpsertEntry(new IndexEntry
        {
            RelativePath = "chapter-1.md",
            Tags = ["ennix"],
            Mentions = [],
            Summary = "Ennix leads the assault."
        });

        var store = new FakeKnowledgeStoreTools();
        var retriever = new ContextRetriever(index, store, "root");

        var result = await retriever.RetrieveAsync("ennix", 1000, RetrievalDepth.Summaries);
        Assert.Single(result.Summaries);
        Assert.Equal("Ennix leads the assault.", result.Summaries[0].Summary);
    }

    /// <summary>A minimal fake for context retriever tests.</summary>
    private sealed class FakeKnowledgeStoreTools : IKnowledgeStoreTools
    {
        public Task<KnowledgeToolResult> AppendToFileAsync(string rootId, string relativePath, string content) =>
            Task.FromResult(KnowledgeToolResult.Ok("ok"));

        public Task<KnowledgeToolResult> CreateFileAsync(string rootId, string relativePath, string content) =>
            Task.FromResult(KnowledgeToolResult.Ok("ok"));

        public Task<KnowledgeToolResult> ListFilesAsync(string rootId, string relativeFolderPath, string? pattern = null) =>
            Task.FromResult(KnowledgeToolResult.Ok("ok"));

        public Task<InstructionEditProposal> ProposeInstructionEditAsync(string rootId, string relativePath, string proposedContent) =>
            Task.FromResult(new InstructionEditProposal());

        public Task<KnowledgeToolResult> ReadFileAsync(string rootId, string relativePath) =>
            Task.FromResult(KnowledgeToolResult.Ok("ok", content: "file content here"));

        public Task<KnowledgeToolResult> UpdateSectionAsync(string rootId, string relativePath, string oldContent, string newContent) =>
            Task.FromResult(KnowledgeToolResult.Ok("ok"));

        public Task<KnowledgeToolResult> WriteInstructionFileAsync(string rootId, string relativePath, string content) =>
            Task.FromResult(KnowledgeToolResult.Ok("ok"));
    }
}
