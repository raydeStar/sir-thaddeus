using System.Diagnostics;
using System.Text;

namespace Thaddeus.Runtime.Voice;

/// <summary>
/// Abstraction over <see cref="Process"/> invocation so sidecar adapters can be
/// tested without spawning real binaries. Implementations honour cancellation
/// and time-bounded execution.
/// </summary>
public interface IExternalProcessRunner
{
    /// <summary>Runs the requested process and returns its exit code, stdout, and stderr.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

/// <summary>Description of a process invocation.</summary>
/// <param name="FileName">Executable path or program name resolvable on PATH.</param>
/// <param name="Arguments">Pre-tokenised arguments. The runner is responsible for quoting.</param>
/// <param name="WorkingDirectory">Optional working directory; defaults to the current process cwd.</param>
/// <param name="Timeout">Maximum wall-clock time before the runner kills the process tree.</param>
/// <param name="StandardInput">Optional UTF-8 text written to the process's stdin and then closed.</param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    string? StandardInput = null);

/// <summary>Captured result of a process invocation.</summary>
/// <param name="ExitCode">Process exit code; -1 when the runner killed it on timeout.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
/// <param name="DurationMs">Wall-clock duration from spawn to exit.</param>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, int DurationMs);

/// <summary>
/// Default <see cref="IExternalProcessRunner"/> that wraps <see cref="Process"/>
/// with a kill-on-cancel/timeout policy and proper stdout/stderr drainage.
/// </summary>
public sealed class DefaultExternalProcessRunner : IExternalProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StandardInput is not null,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in spec.Arguments) startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        var sw = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process {spec.FileName}.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (spec.StandardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), ct).ConfigureAwait(false);
            }
            finally
            {
                process.StandardInput.Close();
            }
        }

        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (spec.Timeout is { } t) combined.CancelAfter(t);

        try
        {
            await process.WaitForExitAsync(combined.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            if (ct.IsCancellationRequested) throw;
            // Timeout — surface as a non-zero exit with whatever output we drained.
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), (int)sw.ElapsedMilliseconds);
        }
    }
}
