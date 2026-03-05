namespace SirThaddeus.RuntimeHost;

public static class RuntimePathResolver
{
    public static string ResolveMcpServerPath(string configuredPath, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        if (!string.Equals(configuredPath, "auto", StringComparison.OrdinalIgnoreCase))
            return ResolveConfiguredPath(configuredPath, baseDirectory);

        var names = OperatingSystem.IsWindows()
            ? new[] { "SirThaddeus.McpServer.exe", "SirThaddeus.McpServer" }
            : new[] { "SirThaddeus.McpServer", "SirThaddeus.McpServer.exe" };

        foreach (var name in names)
        {
            var adjacent = Path.Combine(baseDirectory, name);
            if (File.Exists(adjacent))
                return adjacent;
        }

        var mcpBinDebug = FindMcpBinDebugDirectory(baseDirectory)
            ?? Path.GetFullPath(Path.Combine(baseDirectory, "apps", "mcp-server", "SirThaddeus.McpServer", "bin", "Debug"));

        if (Directory.Exists(mcpBinDebug))
        {
            var candidates = new List<string>();
            foreach (var name in names)
                candidates.AddRange(Directory.EnumerateFiles(mcpBinDebug, name, SearchOption.AllDirectories));

            var newest = candidates
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(newest))
                return newest;
        }

        return Path.Combine(mcpBinDebug, "net10.0", names[0]);
    }

    private static string ResolveConfiguredPath(string configuredPath, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        var trimmed = configuredPath.Trim();

        // On Windows, "/apps/..." (or "\apps\...") is treated as rooted on the
        // current drive. Treat it as repo-relative to keep local configs portable.
        var isRootedWithoutDrive = OperatingSystem.IsWindows() &&
                                   (trimmed.StartsWith('/') || trimmed.StartsWith('\\')) &&
                                   !Path.IsPathFullyQualified(trimmed);
        if (isRootedWithoutDrive)
            return Path.GetFullPath(Path.Combine(baseDirectory, trimmed.TrimStart('/', '\\')));

        if (Path.IsPathRooted(trimmed))
            return Path.GetFullPath(trimmed);

        return Path.GetFullPath(Path.Combine(baseDirectory, trimmed));
    }

    private static string? FindMcpBinDebugDirectory(string baseDirectory)
    {
        var current = Path.GetFullPath(baseDirectory);
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(current, "apps", "mcp-server", "SirThaddeus.McpServer", "bin", "Debug");
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }

        return null;
    }
}
