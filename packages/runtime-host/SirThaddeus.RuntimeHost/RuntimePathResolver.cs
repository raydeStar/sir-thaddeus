namespace SirThaddeus.RuntimeHost;

public static class RuntimePathResolver
{
    public static string ResolveMcpServerPath(string configuredPath, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        if (!string.Equals(configuredPath, "auto", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(configuredPath);

        var names = OperatingSystem.IsWindows()
            ? new[] { "SirThaddeus.McpServer.exe", "SirThaddeus.McpServer" }
            : new[] { "SirThaddeus.McpServer", "SirThaddeus.McpServer.exe" };

        foreach (var name in names)
        {
            var adjacent = Path.Combine(baseDirectory, name);
            if (File.Exists(adjacent))
                return adjacent;
        }

        var dir = baseDirectory;
        var dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        while (!string.IsNullOrWhiteSpace(dir) &&
               !string.Equals(dirName, "apps", StringComparison.OrdinalIgnoreCase))
        {
            dir = Path.GetDirectoryName(dir);
            dirName = Path.GetFileName(dir?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        var mcpBinDebug = dir is not null
            ? Path.GetFullPath(Path.Combine(dir, "mcp-server", "SirThaddeus.McpServer", "bin", "Debug"))
            : Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "apps", "mcp-server", "SirThaddeus.McpServer", "bin", "Debug"));

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

        return Path.Combine(mcpBinDebug, "net8.0", names[0]);
    }
}
