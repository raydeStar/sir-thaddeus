using System.Diagnostics;
using System.Reflection;
using Thaddeus.SharedTypes;

namespace Thaddeus.Shell.Runtime;

/// <summary>
/// Spawns and supervises the runtime child process. On startup the supervisor:
///   1. Looks for an existing lock file at <c>~/.thaddeus/runtime.lock</c>.
///   2. If a runtime already responds on the recorded port, attaches to it.
///   3. Otherwise spawns <c>Thaddeus.Runtime</c> as a child and polls until the lock
///      file appears.
/// </summary>
public sealed class RuntimeProcessSupervisor : IAsyncDisposable
{
    private readonly ILogger<RuntimeProcessSupervisor> _logger;
    private readonly string _lockFilePath;
    private Process? _process;

    /// <summary>
    /// Raised when the supervised runtime process exits unexpectedly (or after
    /// a graceful <c>/api/runtime/stop</c>). Subscribers should treat this as
    /// the cue to tear the shell down — without it the workspace window would
    /// stay open pointing at a dead backend.
    /// </summary>
    public event EventHandler? RuntimeExited;

    /// <summary>Wires the supervisor.</summary>
    public RuntimeProcessSupervisor(ILogger<RuntimeProcessSupervisor> logger)
    {
        _logger = logger;
        _lockFilePath = RuntimeLockFileReader.GetDefaultPath();
    }

    /// <summary>Spawns or attaches and returns the resolved lock-file contents.</summary>
    public async Task<RuntimeLockFile> EnsureRunningAsync(CancellationToken ct)
    {
        if (TryAttachExisting(out var existing))
        {
            _logger.LogInformation("runtime.attached pid={Pid} port={Port}", existing!.Pid, existing.Port);
            // We didn't spawn this runtime, but we still need to know when it
            // exits — otherwise the kill-app button (which calls
            // /api/runtime/stop on the attached runtime) would tear the
            // backend down without ever notifying the shell.
            TryWatchAttachedProcess(existing.Pid);
            return existing;
        }

        // Stale lock file; the existing PID is gone or unresponsive.
        RuntimeLockFileReader.TryDelete(_lockFilePath);

        var runtimePath = ResolveRuntimeLaunchInfo();
        var psi = new ProcessStartInfo
        {
            FileName = runtimePath.command,
            WorkingDirectory = runtimePath.workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var arg in runtimePath.args) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add($"--parent-pid={Environment.ProcessId}");

        _logger.LogInformation(
            "runtime.spawning command={Cmd} workingDir={WorkingDirectory} args={Args}",
            psi.FileName,
            psi.WorkingDirectory,
            string.Join(' ', psi.ArgumentList));
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to spawn runtime process.");

        // Surface child-process exit so the shell can tear itself down when the
        // runtime is killed (web "kill app" button, /api/runtime/stop, crash).
        try
        {
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                _logger.LogInformation("runtime.process_exited pid={Pid}", _process?.Id);
                try { RuntimeExited?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.LogWarning(ex, "runtime.exited_handler_failed"); }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "runtime.exited_subscribe_failed");
        }

        return await WaitForLockFileAsync(ct).ConfigureAwait(false);
    }

    private bool TryAttachExisting(out RuntimeLockFile? lockFile)
    {
        lockFile = RuntimeLockFileReader.TryRead(_lockFilePath);
        if (lockFile is null) return false;

        try
        {
            using var probe = Process.GetProcessById(lockFile.Pid);
            return !probe.HasExited;
        }
        catch (ArgumentException)
        {
            lockFile = null;
            return false;
        }
    }

    private void TryWatchAttachedProcess(int pid)
    {
        try
        {
            // We hold our own Process handle (not via `using`) so it stays
            // open for the lifetime of the supervisor and the Exited event
            // can still fire. Disposed in DisposeAsync().
            _process = Process.GetProcessById(pid);
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                _logger.LogInformation("runtime.process_exited pid={Pid} (attached)", pid);
                try { RuntimeExited?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.LogWarning(ex, "runtime.exited_handler_failed"); }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "runtime.attach_watch_failed pid={Pid}", pid);
        }
    }

    private async Task<RuntimeLockFile> WaitForLockFileAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var lf = RuntimeLockFileReader.TryRead(_lockFilePath);
            if (lf is not null) return lf;
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException($"Runtime exited with code {_process.ExitCode} before writing lock file.");
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Runtime did not become ready within 15 seconds.");
    }

    private static (string command, string workingDirectory, IReadOnlyList<string> args) ResolveRuntimeLaunchInfo()
    {
        // The shell publishes alongside the runtime in the install layout. During
        // development we point at the runtime project directly via `dotnet run`.
        var shellDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!;

        // Production layout: Thaddeus.Runtime(.exe) sits next to the shell.
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var prod = Path.Combine(shellDir, "Thaddeus.Runtime" + ext);
        if (File.Exists(prod))
        {
            return (prod, shellDir, Array.Empty<string>());
        }

        // Dev layout: walk up to the repo root and `dotnet run` the runtime project.
        // localrunner.ps1 pre-builds the runtime in the default (Debug) configuration,
        // so we match that here with --no-build for fast startup.
        var repoRoot = FindRepoRoot(shellDir);
        var runtimeProj = Path.Combine(repoRoot, "src", "Thaddeus.Runtime", "Thaddeus.Runtime.csproj");
        var runtimeDir = Path.GetDirectoryName(runtimeProj)!;
        return ("dotnet", runtimeDir, new[] { "run", "--project", runtimeProj, "--no-build" });
    }

    private static string FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SirThaddeus.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate SirThaddeus.sln above the shell binary.");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _logger.LogInformation("runtime.terminating pid={Pid}", _process.Id);
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "runtime.dispose_failed");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }
}
