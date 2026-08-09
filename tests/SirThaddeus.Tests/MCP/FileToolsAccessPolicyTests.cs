using System.Text;
using System.Text.Json;
using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Tests.MCP;

[Collection(FileToolsEnvironmentCollection.Name)]
public sealed class FileToolsAccessPolicyTests
{
    [Fact]
    public void FileWrite_WritesExactUtf8AndReturnsVerifiedReceipt()
    {
        var root = CreateTempDirectory();
        using var env = AllowedFileEnvironment(root);
        const string content = "Owner: D'Arcy\nToken: ${API_KEY}\nPath: D:\\Ops Tools\n";

        var result = FileTools.FileWrite("nested/effect.txt", content);

        using var document = JsonDocument.Parse(result);
        var receipt = document.RootElement;
        Assert.True(receipt.GetProperty("ok").GetBoolean());
        Assert.True(receipt.GetProperty("verified").GetBoolean());
        Assert.Equal(content, receipt.GetProperty("post_content").GetString());
        Assert.False(receipt.GetProperty("post_content_truncated").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetByteCount(content), receipt.GetProperty("bytes").GetInt32());
        Assert.Equal(
            content,
            File.ReadAllText(Path.Combine(root, "nested", "effect.txt"), new UTF8Encoding(false, true)));
    }

    [Fact]
    public void FileReplace_ReplacesOneExactSpanAndReturnsVerifiedReceipt()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "service.txt");
        File.WriteAllText(path, "MODE=old\nTOKEN=$HOME\nTAIL=keep\n", new UTF8Encoding(false));
        using var env = AllowedFileEnvironment(root);

        var result = FileTools.FileReplace(
            "service.txt",
            "MODE=old\nTOKEN=$HOME",
            "MODE=sealed\nTOKEN=${SERVICE_KEY}");

        using var document = JsonDocument.Parse(result);
        var receipt = document.RootElement;
        Assert.True(receipt.GetProperty("ok").GetBoolean());
        Assert.True(receipt.GetProperty("verified").GetBoolean());
        Assert.Equal(1, receipt.GetProperty("replacements").GetInt32());
        Assert.Equal(
            "MODE=sealed\nTOKEN=${SERVICE_KEY}\nTAIL=keep\n",
            receipt.GetProperty("post_content").GetString());
    }

    [Fact]
    public void FileReplace_AmbiguousSpanFailsWithoutChangingFile()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "values.txt");
        File.WriteAllText(path, "same same", new UTF8Encoding(false));
        using var env = AllowedFileEnvironment(root);

        var result = FileTools.FileReplace("values.txt", "same", "new");

        Assert.Contains("old_text_must_occur_exactly_once", result, StringComparison.Ordinal);
        Assert.Equal("same same", File.ReadAllText(path));
    }

    [Fact]
    public void FileWrite_DeniesTraversalWithoutCreatingOutsideFile()
    {
        var root = CreateTempDirectory();
        var outside = Path.Combine(Directory.GetParent(root)!.FullName, "escaped.txt");
        if (File.Exists(outside))
            File.Delete(outside);
        using var env = AllowedFileEnvironment(root);

        var result = FileTools.FileWrite("../escaped.txt", "blocked");

        Assert.Contains("access_denied", result, StringComparison.Ordinal);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void FileWrite_DeniesWhenFileAccessIsDisabled()
    {
        var root = CreateTempDirectory();
        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = "true",
            ["ST_DOCUMENT_READER_ALLOWED_ROOTS"] = root,
            ["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] = ".txt"
        });

        var result = FileTools.FileWrite("blocked.txt", "blocked");

        Assert.Contains("file_access_disabled", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "blocked.txt")));
    }

    [Fact]
    public void FileWrite_DeniesDisallowedExtensionWithoutCreatingFile()
    {
        var root = CreateTempDirectory();
        using var env = AllowedFileEnvironment(root);

        var result = FileTools.FileWrite("blocked.exe", "blocked");

        Assert.Contains("extension_not_allowed", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "blocked.exe")));
    }

    [Fact]
    public void FileWrite_MissingExtensionPolicyFailsClosedForExecutableFile()
    {
        var root = CreateTempDirectory();
        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = "false",
            ["ST_DOCUMENT_READER_ALLOWED_ROOTS"] = root,
            ["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] = null
        });

        var result = FileTools.FileWrite("blocked.exe", "blocked");

        Assert.Contains("extension_not_allowed", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "blocked.exe")));
    }

    [Fact]
    public void FileWrite_MissingExtensionPolicyUsesConservativeTextDefault()
    {
        var root = CreateTempDirectory();
        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = "false",
            ["ST_DOCUMENT_READER_ALLOWED_ROOTS"] = root,
            ["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] = null
        });

        var result = FileTools.FileWrite("allowed.md", "safe text");

        using var document = JsonDocument.Parse(result);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(document.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal("safe text", File.ReadAllText(Path.Combine(root, "allowed.md")));
    }

    [Fact]
    public void FileWrite_DeniesOversizedContentWithoutCreatingFile()
    {
        var root = CreateTempDirectory();
        using var env = AllowedFileEnvironment(root);

        var result = FileTools.FileWrite("oversized.txt", new string('x', (1024 * 1024) + 1));

        Assert.Contains("file_too_large", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "oversized.txt")));
    }

    [Fact]
    public void FileWrite_DeniesMalformedUnicodeWithoutCreatingFile()
    {
        var root = CreateTempDirectory();
        using var env = AllowedFileEnvironment(root);

        var result = FileTools.FileWrite("malformed.txt", "prefix\ud800suffix");

        Assert.Contains("content_is_not_valid_utf8", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "malformed.txt")));
    }

    [Fact]
    public void FileWrite_DeniesSymbolicLinkPathWithoutChangingTarget()
    {
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        var link = Path.Combine(root, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        using var env = AllowedFileEnvironment(root);
        var result = FileTools.FileWrite("linked/escape.txt", "blocked");

        Assert.Contains("reparse_point_not_allowed", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outside, "escape.txt")));
    }

    [Fact]
    public async Task FileRead_DeniesWhenFileAccessDisabled()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(filePath, "hello world");

        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = "true",
            ["ST_DOCUMENT_READER_ALLOWED_ROOTS"] = root,
            ["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] = ".txt"
        });

        var result = await FileTools.FileRead(filePath, cancellationToken: CancellationToken.None);

        Assert.Equal("Error: File access is disabled in settings.", result);
    }

    [Fact]
    public async Task FileRead_DeniesPathsOutsideAllowedRoots()
    {
        var allowedRoot = CreateTempDirectory();
        var deniedRoot = CreateTempDirectory();
        var deniedFile = Path.Combine(deniedRoot, "outside.txt");
        await File.WriteAllTextAsync(deniedFile, "outside root");

        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = "false",
            ["ST_DOCUMENT_READER_ALLOWED_ROOTS"] = allowedRoot,
            ["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] = ".txt"
        });

        var result = await FileTools.FileRead(deniedFile, cancellationToken: CancellationToken.None);

        Assert.StartsWith("Error: Access denied.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_AllowsFilesInsideConfiguredRoot()
    {
        var allowedRoot = CreateTempDirectory();
        var filePath = Path.Combine(allowedRoot, "inside.txt");
        await File.WriteAllTextAsync(filePath, "alpha beta gamma");

        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = "false",
            ["ST_DOCUMENT_READER_ALLOWED_ROOTS"] = allowedRoot,
            ["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] = ".txt",
            ["ST_DOCUMENT_READER_MAX_DEFAULT_CHARS"] = "4000"
        });

        var result = await FileTools.FileRead(filePath, cancellationToken: CancellationToken.None);

        Assert.Contains("\"ok\":true", result, StringComparison.Ordinal);
        Assert.Contains("alpha beta gamma", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FileList_ResolvesMyPersonalFolderAlias_WhenExactlyOneAllowedRootExists()
    {
        var allowedRoot = CreateTempDirectory();
        File.WriteAllText(Path.Combine(allowedRoot, "note.txt"), "hello");

        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = "false",
            ["ST_DOCUMENT_READER_ALLOWED_ROOTS"] = allowedRoot,
            ["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] = ".txt"
        });

        var result = FileTools.FileList("my personal folder");

        Assert.Contains("[FILE] note.txt", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_ResolvesSingleRootRelativePath()
    {
        var allowedRoot = CreateTempDirectory();
        var filePath = Path.Combine(allowedRoot, "note.txt");
        await File.WriteAllTextAsync(filePath, "hello from allowed root");

        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = "false",
            ["ST_DOCUMENT_READER_ALLOWED_ROOTS"] = allowedRoot,
            ["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] = ".txt"
        });

        var result = await FileTools.FileRead("note.txt", cancellationToken: CancellationToken.None);

        // FileRead returns a JSON envelope with extracted text content.
        Assert.Contains("\"ok\":true", result, StringComparison.Ordinal);
        Assert.Contains("hello from allowed root", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_ResolvesUniqueNestedBasenameWithinAllowedRoot()
    {
        var allowedRoot = CreateTempDirectory();
        var nested = Path.Combine(allowedRoot, "operations", "night");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "dispatch.txt"), "Dispatch lead: Imani Vale");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead("dispatch.txt", cancellationToken: CancellationToken.None);

        Assert.Contains("\"ok\":true", result, StringComparison.Ordinal);
        Assert.Contains("Imani Vale", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_ResolvesUniqueWindowsStylePathSuffix()
    {
        var allowedRoot = CreateTempDirectory();
        var nested = Path.Combine(allowedRoot, "teams", "east", "handover");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "beta-slot.txt"), "Slot time: 06:20 UTC");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead("handover\\beta-slot.txt", cancellationToken: CancellationToken.None);

        Assert.Contains("06:20 UTC", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_DoesNotSearchNestedFilesForLeadingCurrentDirectoryMarker()
    {
        var allowedRoot = CreateTempDirectory();
        var nested = Path.Combine(allowedRoot, "archive", "reviews");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "launch-note.txt"), "Launch decision: GO");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead("./launch-note.txt", cancellationToken: CancellationToken.None);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Launch decision: GO", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_ResolvesDirectoryQualifiedSuffixAfterLeadingCurrentDirectoryMarker()
    {
        var allowedRoot = CreateTempDirectory();
        var nested = Path.Combine(allowedRoot, "archive", "reviews");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "launch-note.txt"), "Launch decision: GO");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead(
            "./reviews/launch-note.txt",
            cancellationToken: CancellationToken.None);

        Assert.Contains("Launch decision: GO", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_PreservesExactLeadingCurrentDirectoryPathWithinAllowedRoot()
    {
        var allowedRoot = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(allowedRoot, "launch-note.txt"), "Launch decision: GO");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead("./launch-note.txt", cancellationToken: CancellationToken.None);

        Assert.Contains("Launch decision: GO", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_DoesNotGuessWhenBasenameIsAmbiguous()
    {
        var allowedRoot = CreateTempDirectory();
        var first = Path.Combine(allowedRoot, "projects", "coral");
        var second = Path.Combine(allowedRoot, "projects", "indigo");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(first, "summary.txt"), "Project: CORAL");
        await File.WriteAllTextAsync(Path.Combine(second, "summary.txt"), "Project: INDIGO");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead("summary.txt", cancellationToken: CancellationToken.None);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("CORAL", result, StringComparison.Ordinal);
        Assert.DoesNotContain("INDIGO", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_DoesNotSubstituteForMissingBasename()
    {
        var allowedRoot = CreateTempDirectory();
        var nested = Path.Combine(allowedRoot, "schedules");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "current-schedule.txt"), "Do not substitute");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead("missing-schedule.txt", cancellationToken: CancellationToken.None);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not substitute", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../private/keys.txt")]
    [InlineData("folder/../../private/keys.txt")]
    [InlineData("folder/./keys.txt")]
    public async Task FileRead_DoesNotResolveUnsafeRelativeSuffix(string requestedPath)
    {
        var allowedRoot = CreateTempDirectory();
        var nested = Path.Combine(allowedRoot, "private");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "keys.txt"), "never expose");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead(requestedPath, cancellationToken: CancellationToken.None);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("never expose", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_PreservesExistingNormalizedPathInsideAllowedRoot()
    {
        var allowedRoot = CreateTempDirectory();
        var nested = Path.Combine(allowedRoot, "private");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "keys.txt"), "allowed in-root content");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead(
            "folder/../private/keys.txt",
            cancellationToken: CancellationToken.None);

        Assert.Contains("allowed in-root content", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_ResolvesOneUniqueSuffixAcrossMultipleAllowedRoots()
    {
        var firstRoot = CreateTempDirectory();
        var secondRoot = CreateTempDirectory();
        var nested = Path.Combine(secondRoot, "facilities", "lab");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "temperature.txt"), "Target temperature: 19 C");

        using var env = AllowedFileEnvironment(firstRoot + Path.PathSeparator + secondRoot);

        var result = await FileTools.FileRead("temperature.txt", cancellationToken: CancellationToken.None);

        Assert.Contains("Target temperature: 19 C", result, StringComparison.Ordinal);
    }

    private static EnvironmentVariableScope AllowedFileEnvironment(string roots) =>
        new(new Dictionary<string, string?>
        {
            ["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = "false",
            ["ST_DOCUMENT_READER_ALLOWED_ROOTS"] = roots,
            ["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] = ".txt"
        });

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sir-thaddeus-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _priorValues;

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            _priorValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values)
            {
                _priorValues[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _priorValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileToolsEnvironmentCollection
{
    public const string Name = "FileToolsEnvironment";
}
