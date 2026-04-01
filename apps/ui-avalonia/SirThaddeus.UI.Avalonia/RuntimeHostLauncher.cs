using System.Diagnostics;
using System.Net.Http.Json;
using SirThaddeus.Contracts;
using SirThaddeus.Core;

namespace SirThaddeus.UI.Avalonia;

internal enum RuntimeLaunchStatus
{
    Started,
    AlreadyRunning,
    NotLocalAddress,
    NotFound,
    FailedToStart,
    FailedHealthCheck
}

internal sealed record RuntimeLaunchResult(
    RuntimeLaunchStatus Status,
    string Message);

internal sealed class RuntimeHostLauncher : IDisposable
{
    private Process? _managedProcess;

    public bool IsManagedRuntimeRunning => _managedProcess is { HasExited: false };

    public async Task<RuntimeLaunchResult> EnsureRunningAsync(Uri runtimeBaseUri, CancellationToken cancellationToken)
    {
        if (!LoopbackProcessSupport.IsLoopback(runtimeBaseUri))
        {
            return new RuntimeLaunchResult(
                RuntimeLaunchStatus.NotLocalAddress,
                "Runtime auto-start only supports localhost addresses.");
        }

        if (IsManagedRuntimeRunning)
        {
            return new RuntimeLaunchResult(RuntimeLaunchStatus.AlreadyRunning, "Managed runtime is already running.");
        }

        var launchConfig = ResolveLaunchConfig();
        if (launchConfig is null)
        {
            return new RuntimeLaunchResult(
                RuntimeLaunchStatus.NotFound,
                "Could not find SirThaddeus.HeadlessRuntime executable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launchConfig.StartFileName,
            Arguments = BuildRuntimeArguments(launchConfig.BaseArguments, runtimeBaseUri.Port),
            WorkingDirectory = launchConfig.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _managedProcess = Process.Start(startInfo);
            if (_managedProcess is null)
            {
                return new RuntimeLaunchResult(RuntimeLaunchStatus.FailedToStart, "Failed to start runtime process.");
            }
        }
        catch (Exception ex)
        {
            return new RuntimeLaunchResult(RuntimeLaunchStatus.FailedToStart, ex.Message);
        }

        var healthy = await LoopbackProcessSupport.WaitForProbeAsync(
            probeCancellationToken => ProbeHealthAsync(runtimeBaseUri, probeCancellationToken),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(350),
            cancellationToken);
        if (healthy)
        {
            return new RuntimeLaunchResult(RuntimeLaunchStatus.Started, "Local runtime started.");
        }

        StopManagedRuntime();
        return new RuntimeLaunchResult(RuntimeLaunchStatus.FailedHealthCheck, "Runtime started but did not pass health checks.");
    }

    public void StopManagedRuntime()
    {
        LoopbackProcessSupport.StopManagedProcess(ref _managedProcess);
    }

    public void Dispose()
    {
        StopManagedRuntime();
    }

    private static string BuildRuntimeArguments(string baseArguments, int port)
    {
        return string.IsNullOrWhiteSpace(baseArguments)
            ? $"--server --tools --port {port}"
            : $"{baseArguments} --server --tools --port {port}";
    }

    private static async Task<bool> ProbeHealthAsync(Uri runtimeBaseUri, CancellationToken cancellationToken)
    {
        using var http = new HttpClient
        {
            BaseAddress = runtimeBaseUri,
            Timeout = TimeSpan.FromSeconds(2)
        };

        try
        {
            var health = await http.GetFromJsonAsync<HealthResponse>("/api/health", cancellationToken);
            if (health is not null && health.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // Retry until timeout.
        }

        return false;
    }

    private static RuntimeLaunchConfig? ResolveLaunchConfig()
    {
        var appBase = AppContext.BaseDirectory;

        var repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            var projectPath = Path.Combine(repoRoot, "apps", "headless-runtime", "SirThaddeus.HeadlessRuntime", "SirThaddeus.HeadlessRuntime.csproj");
            if (File.Exists(projectPath))
            {
                return new RuntimeLaunchConfig("dotnet", $"run --project \"{projectPath}\" --", repoRoot);
            }

            var dllPath = Path.Combine(repoRoot, "apps", "headless-runtime", "SirThaddeus.HeadlessRuntime", "bin", "Debug", "net10.0", "SirThaddeus.HeadlessRuntime.dll");
            if (File.Exists(dllPath))
            {
                return new RuntimeLaunchConfig("dotnet", $"\"{dllPath}\"", Path.GetDirectoryName(dllPath)!);
            }
        }

        foreach (var path in CandidateExecutablePaths(appBase))
        {
            if (File.Exists(path))
            {
                return new RuntimeLaunchConfig(path, "", Path.GetDirectoryName(path)!);
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateExecutablePaths(string appBase)
    {
        yield return Path.Combine(appBase, "SirThaddeus.HeadlessRuntime.exe");
        yield return Path.Combine(appBase, "bin", "SirThaddeus.HeadlessRuntime.exe");
        yield return Path.Combine(appBase, "headless", "SirThaddeus.HeadlessRuntime.exe");
        yield return Path.Combine(appBase, "..", "SirThaddeus.HeadlessRuntime.exe");
        yield return Path.Combine(appBase, "..", "headless-runtime", "SirThaddeus.HeadlessRuntime.exe");
    }

    private static string? FindRepoRoot()
    {
        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var start in candidates)
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "SirThaddeus.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private sealed record RuntimeLaunchConfig(string StartFileName, string BaseArguments, string WorkingDirectory);
}
