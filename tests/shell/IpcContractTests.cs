using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Shell.Ipc;
using Thaddeus.Shell.Runtime;
using Thaddeus.SharedTypes;
using Xunit;

namespace Thaddeus.Shell.Tests;

/// <summary>
/// Phase-1 contract tests. Spawns the runtime as a child process exactly the way the
/// shell does in production, then exercises the hello handshake (success and version
/// mismatch) and the shutdown round-trip.
/// </summary>
public sealed class IpcContractTests : IAsyncLifetime
{
    private RuntimeProcessSupervisor? _supervisor;
    private RuntimeLockFile? _lockFile;
    private string? _lockDir;
    private string? _lockFilePath;

    public async Task InitializeAsync()
    {
        _lockDir = Path.Combine(Path.GetTempPath(), "sir-thaddeus-ipc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_lockDir);
        _lockFilePath = Path.Combine(_lockDir, "runtime.lock");

        // Pre-clean any stale lock file so we get a fresh runtime each test run.
        var existing = RuntimeLockFileReader.TryRead(_lockFilePath);
        if (existing is not null)
        {
            try
            {
                using var p = Process.GetProcessById(existing.Pid);
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
            RuntimeLockFileReader.TryDelete(_lockFilePath);
        }

        _supervisor = new RuntimeProcessSupervisor(
            NullLogger<RuntimeProcessSupervisor>.Instance,
            _lockFilePath,
            testMode: true,
            startupTimeout: TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        _lockFile = await _supervisor.EnsureRunningAsync(cts.Token);
    }

    public async Task DisposeAsync()
    {
        if (_supervisor is not null) await _supervisor.DisposeAsync();
        if (_lockDir is not null)
        {
            try { Directory.Delete(_lockDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Handshake_succeeds_when_versions_match()
    {
        await using var ipc = new IpcClient(NullLogger<IpcClient>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // The runtime reports its assembly version (default "0.0.0" in tests). Pass the
        // same value explicitly so the test is independent of the shell's entry-assembly
        // version (which is the test runner here).
        await ipc.ConnectAndHandshakeAsync(_lockFile!.IpcEndpoint!, cts.Token, _lockFile.Version);
    }

    [Fact]
    public async Task Handshake_throws_on_version_mismatch()
    {
        await using var ipc = new IpcClient(NullLogger<IpcClient>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<IpcVersionMismatchException>(async () =>
            await ipc.ConnectAndHandshakeAsync(_lockFile!.IpcEndpoint!, cts.Token, "999.999.999"));
    }

    [Fact]
    public async Task Shutdown_message_terminates_runtime()
    {
        await using var ipc = new IpcClient(NullLogger<IpcClient>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ipc.ConnectAndHandshakeAsync(_lockFile!.IpcEndpoint!, cts.Token, _lockFile.Version);

        await ipc.RequestShutdownAsync(cts.Token);

        // Wait for the lock file to disappear (runtime deletes it on shutdown).
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (RuntimeLockFileReader.TryRead(_lockFilePath!) is null) return;
            await Task.Delay(100);
        }
        Assert.Fail("Runtime did not delete its lock file within 10s of shutdown.");
    }
}
