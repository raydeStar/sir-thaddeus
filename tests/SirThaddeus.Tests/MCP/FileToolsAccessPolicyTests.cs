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

        var result = await FileTools.FileRead(filePath, CancellationToken.None);

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

        var result = await FileTools.FileRead(deniedFile, CancellationToken.None);

        Assert.StartsWith("Error: Access denied.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentRead_AllowsFilesInsideConfiguredRoot()
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

        var result = await FileTools.DocumentRead(filePath, cancellationToken: CancellationToken.None);

        Assert.Contains("\"ok\":true", result, StringComparison.Ordinal);
        Assert.Contains("alpha beta gamma", result, StringComparison.Ordinal);
    }

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