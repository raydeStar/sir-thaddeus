using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// System command tools exposed via MCP.
/// Executes commands with strict safety constraints.
/// </summary>
[McpServerToolType]
public static class SystemTools
{
    // Allowlisted commands that are safe to run without elevated concern.
    private static readonly HashSet<string> SafeCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "whoami", "hostname", "date", "time", "echo", "dir", "ls",
        "type", "where", "systeminfo", "ipconfig", "dotnet"
    };

    [McpServerTool, Description("Execute a system command and return its output. Only allowlisted commands are permitted.")]
    public static async Task<string> SystemExecute(
        [Description("The command to execute (e.g. 'whoami', 'hostname', 'dir')")] string command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Error: command is required.";

        // Extract the base command name for allowlist checking
        var baseCommand = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!SafeCommands.Contains(baseCommand))
        {
            return $"Error: Command '{baseCommand}' is not in the allowlist. " +
                   $"Permitted commands: {string.Join(", ", SafeCommands.Order())}";
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var result = $"Exit code: {process.ExitCode}";
            if (!string.IsNullOrWhiteSpace(stdout))
                result += $"\nOutput:\n{stdout.Trim()}";
            if (!string.IsNullOrWhiteSpace(stderr))
                result += $"\nStderr:\n{stderr.Trim()}";

            return result;
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }
}
