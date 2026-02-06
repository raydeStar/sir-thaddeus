using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// File system tools exposed via MCP.
/// Provides read access to local files with basic safety checks.
/// </summary>
[McpServerToolType]
public static class FileTools
{
    [McpServerTool, Description("Read the contents of a file at the specified path.")]
    public static async Task<string> FileRead(
        [Description("Absolute or relative path to the file")] string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Error: path is required.";

        try
        {
            var fullPath = Path.GetFullPath(path);

            if (!File.Exists(fullPath))
                return $"Error: File not found at '{fullPath}'.";

            var info = new FileInfo(fullPath);
            if (info.Length > 1_048_576) // 1 MB safety limit
                return $"Error: File is too large ({info.Length:N0} bytes). Max is 1 MB.";

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
            return content;
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    [McpServerTool, Description("List files and directories at the specified path.")]
    public static string FileList(
        [Description("Directory path to list")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Error: path is required.";

        try
        {
            var fullPath = Path.GetFullPath(path);

            if (!Directory.Exists(fullPath))
                return $"Error: Directory not found at '{fullPath}'.";

            var entries = Directory.GetFileSystemEntries(fullPath)
                .Select(e =>
                {
                    var isDir = Directory.Exists(e);
                    var name = Path.GetFileName(e);
                    return isDir ? $"[DIR]  {name}" : $"[FILE] {name}";
                })
                .Take(100); // Cap at 100 entries

            return string.Join("\n", entries);
        }
        catch (Exception ex)
        {
            return $"Error listing directory: {ex.Message}";
        }
    }
}
