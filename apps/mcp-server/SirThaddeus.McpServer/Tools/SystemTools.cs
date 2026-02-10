using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// System Command Tools
//
// Executes commands with strict safety constraints:
//   - Base command must be in the allowlist
//   - Shell metacharacters are blocked (no injection)
//   - dotnet subcommands are restricted to safe verbs
//   - Optional cwd must point to an existing directory
//
// Invariants:
//   T3 — Strict allowlists for execution
//   I3 — Explicit permission required (enforced by runtime gate)
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class SystemTools
{
    // ─────────────────────────────────────────────────────────────────
    // Allowlist: commands that are safe to run
    // ─────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> SafeCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "whoami", "hostname", "date", "time", "echo", "dir", "ls",
        "type", "where", "systeminfo", "ipconfig", "dotnet"
    };

    // ─────────────────────────────────────────────────────────────────
    // Shell metacharacters that enable command injection. Any of these
    // in the command string causes an immediate rejection.
    // ─────────────────────────────────────────────────────────────────

    private static readonly char[] BlockedMetachars =
        ['&', '|', '>', '<', ';', '`', '$', '(', ')', '{', '}'];

    // ─────────────────────────────────────────────────────────────────
    // dotnet: allowed subcommands / verbs. Everything else is blocked.
    // ─────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> AllowedDotnetVerbs =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "--info", "--version", "restore", "build", "test"
    };

    [McpServerTool, Description(
        "Execute a system command and return its output. " +
        "Only allowlisted commands are permitted. " +
        "Shell metacharacters (& | > < ; ` $ ( ) { }) are blocked. " +
        "For dotnet, only --info, --version, restore, build, and test are allowed.")]
    public static async Task<string> SystemExecute(
        [Description("The command to execute (e.g. 'whoami', 'hostname', 'dotnet --info')")]
        string command,
        [Description("Optional working directory. Must be an existing directory path. " +
            "Defaults to the server's current directory if not specified.")]
        string? cwd = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Error: command is required.";

        // ── Guard: shell metacharacters ──────────────────────────────
        if (command.IndexOfAny(BlockedMetachars) >= 0)
        {
            return "Error: Command contains blocked shell metacharacters " +
                   $"({string.Join(' ', BlockedMetachars.Select(c => $"'{c}'"))}). " +
                   "Use structured tool calls instead of shell chaining.";
        }

        // ── Guard: allowlist ─────────────────────────────────────────
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var baseCommand = tokens.FirstOrDefault() ?? "";

        if (!SafeCommands.Contains(baseCommand))
        {
            return $"Error: Command '{baseCommand}' is not in the allowlist. " +
                   $"Permitted commands: {string.Join(", ", SafeCommands.Order())}";
        }

        // ── Guard: dotnet verb restrictions ──────────────────────────
        if (string.Equals(baseCommand, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var verb = tokens.Length > 1 ? tokens[1] : "";
            if (string.IsNullOrEmpty(verb))
            {
                return "Error: 'dotnet' requires a subcommand. " +
                       $"Allowed: {string.Join(", ", AllowedDotnetVerbs.Order())}";
            }

            if (!AllowedDotnetVerbs.Contains(verb))
            {
                return $"Error: 'dotnet {verb}' is not permitted. " +
                       $"Allowed dotnet verbs: {string.Join(", ", AllowedDotnetVerbs.Order())}";
            }
        }

        // ── Guard: working directory ─────────────────────────────────
        string? resolvedCwd = null;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            resolvedCwd = Path.GetFullPath(cwd);
            if (!Directory.Exists(resolvedCwd))
            {
                return $"Error: Working directory does not exist: {resolvedCwd}";
            }
        }

        // ── Execute ──────────────────────────────────────────────────
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                Arguments              = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = resolvedCwd ?? ""
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
