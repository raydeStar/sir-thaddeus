using System.Diagnostics;

namespace SirThaddeus.Core;

public static class LoopbackProcessSupport
{
    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (uri.IsLoopback)
        {
            return true;
        }

        var host = uri.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> WaitForProbeAsync(
        Func<CancellationToken, Task<bool>> probeAsync,
        TimeSpan timeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probeAsync);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (retryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            if (await probeAsync(timeoutCts.Token))
            {
                return true;
            }

            try
            {
                await Task.Delay(retryDelay, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return false;
    }

    public static void StopManagedProcess(ref Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort shutdown only.
        }
        finally
        {
            process.Dispose();
            process = null;
        }
    }
}