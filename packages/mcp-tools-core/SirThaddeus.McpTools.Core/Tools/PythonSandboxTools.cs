using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Python Sandbox Tool
//
// Runs a short, self-contained Python 3 script inside a locked-down Docker
// container and returns what it printed. The model writes the program (the
// reasoning); the sandbox executes it exactly (the iteration/arithmetic).
//
// Isolation: --network none (no DNS, no egress), --read-only rootfs with a
// small tmpfs /tmp, memory/CPU/pid caps, all capabilities dropped, and a
// hard wall-clock timeout with docker-kill cleanup. No host path is mounted,
// so the container can see nothing of the user's machine.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// MCP tool that executes a Python 3 script in a network-isolated,
/// resource-capped Docker sandbox and returns the printed output.
/// </summary>
[McpServerToolType]
public static class PythonSandboxTools
{
    private const int MaxCodeLength = 4000;
    private const int MaxOutputChars = 4000;
    private const int DefaultTimeoutMs = 20_000;

    [McpServerTool, Description(
        "Run a short Python 3 script in a locked-down sandbox (no network, no file " +
        "access, strict CPU/memory/time limits) and return what it PRINTS. Use this " +
        "for any computation that needs loops or many steps — counting, enumerating, " +
        "recurrences, simulations, digit/string manipulation — anything too complex " +
        "for the calculator tool's single expression. Write a complete script that " +
        "ends by printing the final result. Example: to count perfect squares below " +
        "1000, send: print(sum(1 for i in range(1, 1000) if int(i**0.5)**2 == i)). " +
        "Returns JSON {\"stdout\":...,\"exit_code\":...} (stderr included when " +
        "non-empty — read the traceback and fix your script) or {\"error\":...}.")]
    public static string PythonEval(
        [Description("A complete, self-contained Python 3 script that prints its result. Standard library only; the sandbox blocks network and file access.")]
        string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return ErrorJson("Code is empty. Send a Python 3 script that prints its result.");

        if (code.Length > MaxCodeLength)
            return ErrorJson($"Script is too long (max {MaxCodeLength} characters).");

        if (IsDisabledBySettings())
            return ErrorJson("Python sandbox is disabled (ST_PYTHON_SANDBOX_DISABLED).");

        var containerName = $"st-pysandbox-{Guid.NewGuid():N}"[..24];
        var timeoutMs = ResolveTimeoutMs();

        var psi = new ProcessStartInfo
        {
            FileName = ResolveDockerPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in BuildDockerArguments(containerName))
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return ErrorJson("Python sandbox unavailable: could not start the Docker process.");

            process.StandardInput.Write(code);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                KillContainer(psi.FileName, containerName);
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return ErrorJson($"Script timed out after {timeoutMs / 1000}s and was stopped. Simplify the computation (smaller ranges, fewer iterations) and retry.");
            }

            var stdout = Truncate(stdoutTask.GetAwaiter().GetResult());
            var stderr = Truncate(stderrTask.GetAwaiter().GetResult());

            // Docker-side failures (daemon down, bad flags, missing image) exit
            // with 125/126/127 before the script ever runs — surface those as
            // sandbox errors, not script results the model should try to "fix".
            if (process.ExitCode is 125 or 126 or 127 && string.IsNullOrWhiteSpace(stdout))
                return ErrorJson($"Python sandbox unavailable: {FirstLine(stderr)}");

            if (string.IsNullOrWhiteSpace(stderr))
                return JsonSerializer.Serialize(new { stdout, exit_code = process.ExitCode });

            return JsonSerializer.Serialize(new { stdout, stderr, exit_code = process.ExitCode });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ErrorJson("Python sandbox unavailable: Docker is not installed or not on PATH.");
        }
        catch (Exception ex)
        {
            // Don't surface the raw exception message — it can carry host
            // paths, permission details, or other environment specifics. The
            // type name is enough to diagnose without leaking anything.
            return ErrorJson($"Python sandbox failed ({ex.GetType().Name}).");
        }
    }

    // Hardened invocation, verified end-to-end on this machine: network-less,
    // read-only rootfs (small tmpfs for /tmp), memory/CPU/pid caps, all Linux
    // capabilities dropped, no privilege escalation, code passed via stdin so
    // no shell quoting is involved. -I runs Python in isolated mode.
    private static List<string> BuildDockerArguments(string containerName) =>
    [
        "run", "--rm", "-i",
        // Run an init process (PID 1) so any child processes the script
        // spawns are reaped instead of lingering as zombies inside the
        // container's pid namespace.
        "--init",
        "--name", containerName,
        "--network", "none",
        "--memory", "256m",
        "--cpus", "1",
        "--pids-limit", "64",
        "--read-only",
        "--tmpfs", "/tmp:size=16m",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        ResolveImage(),
        "python", "-I", "-q", "-",
    ];

    private static void KillContainer(string dockerPath, string containerName)
    {
        try
        {
            var kill = Process.Start(new ProcessStartInfo
            {
                FileName = dockerPath,
                ArgumentList = { "kill", containerName },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            kill?.WaitForExit(5_000);
        }
        catch
        {
            // Best effort — --rm cleans up once the container stops.
        }
    }

    private static bool IsDisabledBySettings()
    {
        var raw = Environment.GetEnvironmentVariable("ST_PYTHON_SANDBOX_DISABLED")?.Trim();
        return string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDockerPath() =>
        Environment.GetEnvironmentVariable("ST_PYTHON_SANDBOX_DOCKER") is { Length: > 0 } configured
            ? configured
            : "docker";

    private static string ResolveImage() =>
        Environment.GetEnvironmentVariable("ST_PYTHON_SANDBOX_IMAGE") is { Length: > 0 } configured
            ? configured
            : "python:3.11-slim";

    private static int ResolveTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable("ST_PYTHON_SANDBOX_TIMEOUT_MS");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? Math.Clamp(ms, 1_000, 60_000)
            : DefaultTimeoutMs;
    }

    private static string Truncate(string value) =>
        value.Length <= MaxOutputChars ? value : value[..MaxOutputChars] + "\n…(truncated)";

    private static string FirstLine(string value)
    {
        var trimmed = value.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline].Trim();
    }

    private static string ErrorJson(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
