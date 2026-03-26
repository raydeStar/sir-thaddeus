namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Sandboxed filesystem interface for tests. Constrains file operations
/// to a specific root directory to prevent tests from touching real files.
/// </summary>
public interface ISandboxedFileSystem
{
    string RootDirectory { get; }
    bool FileExists(string relativePath);
    string ReadFile(string relativePath);
    void WriteFile(string relativePath, string content);
    IReadOnlyList<string> ListFiles(string relativeDirectory = "");
    void DeleteFile(string relativePath);
}

/// <summary>
/// In-memory filesystem sandbox for test isolation.
/// No actual disk I/O occurs.
/// </summary>
public sealed class InMemorySandboxedFileSystem : ISandboxedFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public string RootDirectory { get; }

    public InMemorySandboxedFileSystem(string rootDirectory = "/sandbox")
    {
        RootDirectory = rootDirectory;
    }

    public bool FileExists(string relativePath) => _files.ContainsKey(Normalize(relativePath));

    public string ReadFile(string relativePath)
    {
        var key = Normalize(relativePath);
        return _files.TryGetValue(key, out var content)
            ? content
            : throw new FileNotFoundException($"File not found in sandbox: {relativePath}");
    }

    public void WriteFile(string relativePath, string content)
    {
        ValidatePath(relativePath);
        _files[Normalize(relativePath)] = content;
    }

    public IReadOnlyList<string> ListFiles(string relativeDirectory = "")
    {
        var prefix = string.IsNullOrWhiteSpace(relativeDirectory)
            ? ""
            : Normalize(relativeDirectory) + "/";

        return _files.Keys
            .Where(k => string.IsNullOrEmpty(prefix) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void DeleteFile(string relativePath)
    {
        _files.Remove(Normalize(relativePath));
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/').TrimEnd('/');

    private static void ValidatePath(string path)
    {
        var normalized = Normalize(path);
        if (normalized.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Path traversal not allowed in sandbox.", nameof(path));
    }
}

/// <summary>
/// Disk-backed filesystem sandbox that constrains operations to a temp directory.
/// Implements IDisposable for cleanup.
/// </summary>
public sealed class TempDirectorySandboxedFileSystem : ISandboxedFileSystem, IDisposable
{
    public string RootDirectory { get; }

    public TempDirectorySandboxedFileSystem()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), $"sir-thaddeus-sandbox-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(RootDirectory);
    }

    public bool FileExists(string relativePath)
        => File.Exists(ResolveFull(relativePath));

    public string ReadFile(string relativePath)
        => File.ReadAllText(ResolveFull(relativePath));

    public void WriteFile(string relativePath, string content)
    {
        var fullPath = ResolveFull(relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }

    public IReadOnlyList<string> ListFiles(string relativeDirectory = "")
    {
        var dir = string.IsNullOrWhiteSpace(relativeDirectory)
            ? RootDirectory
            : Path.Combine(RootDirectory, relativeDirectory);

        if (!Directory.Exists(dir))
            return [];

        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RootDirectory, f).Replace('\\', '/'))
            .ToList();
    }

    public void DeleteFile(string relativePath)
    {
        var fullPath = ResolveFull(relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    private string ResolveFull(string relativePath)
    {
        ValidateContainment(relativePath);
        return Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
    }

    private void ValidateContainment(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
        if (!full.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path escapes sandbox: {relativePath}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
