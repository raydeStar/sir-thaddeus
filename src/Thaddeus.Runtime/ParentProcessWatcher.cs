using System.Diagnostics;
using Thaddeus.Runtime.Hosting;

namespace Thaddeus.Runtime;

/// <summary>
/// When started with <c>--parent-pid=&lt;pid&gt;</c>, watches that PID and shuts down
/// the runtime if it disappears. Mitigates the "shell crashed but runtime is still
/// running" zombie scenario from spec §6.1.
/// </summary>
internal sealed class ParentProcessWatcher : BackgroundService
{
    private readonly RuntimeOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ParentProcessWatcher> _logger;

    public ParentProcessWatcher(
        RuntimeOptions options,
        IHostApplicationLifetime lifetime,
        ILogger<ParentProcessWatcher> logger)
    {
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.ParentPid is not int pid)
        {
            return;
        }

        Process? parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("parent.not_found pid={Pid} — exiting", pid);
            _lifetime.StopApplication();
            return;
        }

        try
        {
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) =>
            {
                _logger.LogInformation("parent.exited pid={Pid} — runtime shutting down", pid);
                _lifetime.StopApplication();
            };
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            parent.Dispose();
        }
    }
}
