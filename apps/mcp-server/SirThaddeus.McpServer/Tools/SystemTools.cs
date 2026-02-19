using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
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
    private sealed record SystemPreview(string Command, string? ResolvedCwd, DateTimeOffset ExpiresAtUtc);

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

    private static readonly ConcurrentDictionary<string, SystemPreview> PreviewCache = new();
    private static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
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
        if (!TryValidateRequest(command, cwd, out var resolvedCwd, out var validationError))
            return validationError;

        return await RunCommandAsync(command, resolvedCwd, cancellationToken);
    }

    [McpServerTool(
        Name = "system_execute_preview",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Builds a dry-run preview for system_execute and returns a preview_id.")]
    public static string SystemExecutePreview(
        [Description("The command to validate and stage for execution")] string command,
        [Description("Optional working directory for the command")] string? cwd = null)
    {
        if (!TryValidateRequest(command, cwd, out var resolvedCwd, out var validationError))
            return JsonSerializer.Serialize(new { ok = false, error = validationError }, JsonOpts);

        var previewId = CreatePreview(command, resolvedCwd);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            preview_id = previewId,
            tool = "system_execute",
            command = command.Trim(),
            cwd = resolvedCwd ?? "",
            expires_at_utc = DateTimeOffset.UtcNow.Add(PreviewTtl).ToString("O")
        }, JsonOpts);
    }

    [McpServerTool(
        Name = "system_execute_apply",
        ReadOnly = false,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Executes a previously previewed command. Requires confirm=true.")]
    public static async Task<string> SystemExecuteApply(
        [Description("Preview identifier returned by system_execute_preview")] string previewId,
        [Description("Explicit confirmation gate. Must be true to execute.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "confirm_required",
                message = "Set confirm=true to execute system_execute_apply."
            }, JsonOpts);
        }

        if (!TryGetPreview(previewId, out var preview))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "preview_not_found_or_expired"
            }, JsonOpts);
        }

        var result = await RunCommandAsync(preview!.Command, preview.ResolvedCwd, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            ok = !result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase),
            preview_id = previewId,
            tool = "system_execute",
            result
        }, JsonOpts);
    }

    private static bool TryValidateRequest(
        string command,
        string? cwd,
        out string? resolvedCwd,
        out string error)
    {
        resolvedCwd = null;
        error = "";

        if (string.IsNullOrWhiteSpace(command))
        {
            error = "Error: command is required.";
            return false;
        }

        if (command.IndexOfAny(BlockedMetachars) >= 0)
        {
            error = "Error: Command contains blocked shell metacharacters " +
                    $"({string.Join(' ', BlockedMetachars.Select(c => $"'{c}'"))}). " +
                    "Use structured tool calls instead of shell chaining.";
            return false;
        }

        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var baseCommand = tokens.FirstOrDefault() ?? "";
        if (!SafeCommands.Contains(baseCommand))
        {
            error = $"Error: Command '{baseCommand}' is not in the allowlist. " +
                    $"Permitted commands: {string.Join(", ", SafeCommands.Order())}";
            return false;
        }

        if (string.Equals(baseCommand, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var verb = tokens.Length > 1 ? tokens[1] : "";
            if (string.IsNullOrEmpty(verb))
            {
                error = "Error: 'dotnet' requires a subcommand. " +
                        $"Allowed: {string.Join(", ", AllowedDotnetVerbs.Order())}";
                return false;
            }

            if (!AllowedDotnetVerbs.Contains(verb))
            {
                error = $"Error: 'dotnet {verb}' is not permitted. " +
                        $"Allowed dotnet verbs: {string.Join(", ", AllowedDotnetVerbs.Order())}";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(cwd))
        {
            resolvedCwd = Path.GetFullPath(cwd);
            if (!Directory.Exists(resolvedCwd))
            {
                error = $"Error: Working directory does not exist: {resolvedCwd}";
                return false;
            }
        }

        return true;
    }

    private static async Task<string> RunCommandAsync(
        string command,
        string? resolvedCwd,
        CancellationToken cancellationToken)
    {
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
                CreateNoWindow = true,
                WorkingDirectory = resolvedCwd ?? ""
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

    private static string CreatePreview(string command, string? resolvedCwd)
    {
        PruneExpiredPreviews();
        var previewId = $"preview-{Guid.NewGuid():N}";
        PreviewCache[previewId] = new SystemPreview(
            command.Trim(),
            string.IsNullOrWhiteSpace(resolvedCwd) ? null : resolvedCwd,
            DateTimeOffset.UtcNow.Add(PreviewTtl));
        return previewId;
    }

    private static bool TryGetPreview(string previewId, out SystemPreview? preview)
    {
        preview = null;
        if (string.IsNullOrWhiteSpace(previewId))
            return false;

        PruneExpiredPreviews();
        var key = previewId.Trim();
        if (!PreviewCache.TryGetValue(key, out var existing))
            return false;

        if (existing.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            PreviewCache.TryRemove(key, out _);
            return false;
        }

        preview = existing;
        return true;
    }

    private static void PruneExpiredPreviews()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in PreviewCache)
        {
            if (pair.Value.ExpiresAtUtc < now)
                PreviewCache.TryRemove(pair.Key, out _);
        }
    }
}
