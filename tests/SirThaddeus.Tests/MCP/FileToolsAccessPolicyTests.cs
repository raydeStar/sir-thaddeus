using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Tests.MCP;

[Collection(FileToolsEnvironmentCollection.Name)]
public sealed class FileToolsAccessPolicyTests
{
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
    public async Task FileRead_ResolvesSuffixAfterLeadingCurrentDirectoryMarker()
    {
        var allowedRoot = CreateTempDirectory();
        var nested = Path.Combine(allowedRoot, "archive", "reviews");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "launch-note.txt"), "Launch decision: GO");

        using var env = AllowedFileEnvironment(allowedRoot);

        var result = await FileTools.FileRead("./reviews/launch-note.txt", cancellationToken: CancellationToken.None);

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
