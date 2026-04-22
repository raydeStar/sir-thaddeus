using System.Collections.Immutable;
using System.Diagnostics;
using SirThaddeus.Config;

namespace SirThaddeus.Diagnostics;

/// <summary>
/// Runs a pinned set of reachability/sanity checks at component startup so
/// operators find out quickly — via a log line — why a user is about to hit
/// a cold failure ("connection refused to LM Studio on startup" beats
/// "the chat window just stopped responding after a prompt").
/// </summary>
/// <remarks>
/// All checks are advisory. None of them block a component from starting.
/// Surfaced via <see cref="StartupDiagnosticReport"/> so the caller can
/// choose to log, display in a diagnostics panel, or both.
/// </remarks>
public static class StartupDiagnostics
{
    private static readonly TimeSpan DefaultPerCheckTimeout = TimeSpan.FromSeconds(2);

    public static Task<StartupDiagnosticReport> RunAsync(
        AppSettings settings,
        TimeSpan? perCheckTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var timeout = perCheckTimeout ?? DefaultPerCheckTimeout;

        var checks = new List<Func<Task<StartupCheck>>>
        {
            () => CheckLlmReachableAsync(settings, timeout, cancellationToken),
            () => CheckVoiceHostReachableAsync(settings, timeout, cancellationToken),
            () => CheckLogDirectoryWritableAsync(cancellationToken),
        };

        return RunAllAsync(checks, cancellationToken);
    }

    private static async Task<StartupDiagnosticReport> RunAllAsync(
        IEnumerable<Func<Task<StartupCheck>>> checks,
        CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(checks.Select(c => c()));
        cancellationToken.ThrowIfCancellationRequested();

        return new StartupDiagnosticReport
        {
            Checks = [.. results],
        };
    }

    private static async Task<StartupCheck> CheckLlmReachableAsync(
        AppSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        const string name = "llm.reachable";
        var baseUrl = settings.Llm.BaseUrl?.Trim();
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Skip(name, "No LLM base URL configured", sw);
        }

        using var http = new HttpClient { Timeout = timeout };
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(timeout);

        try
        {
            var target = baseUrl.TrimEnd('/') + "/v1/models";
            using var response = await http.GetAsync(target, probeCts.Token);

            // Any HTTP response — even 401/404 — proves something is listening.
            return Ok(name,
                $"LLM endpoint reachable at {baseUrl} (HTTP {(int)response.StatusCode})",
                sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(name, $"LLM endpoint at {baseUrl} did not respond within {timeout.TotalSeconds:0}s", sw);
        }
        catch (HttpRequestException ex)
        {
            return Failed(name, $"LLM endpoint at {baseUrl} unreachable: {ex.Message}", sw, ex);
        }
        catch (Exception ex)
        {
            return Failed(name, $"LLM probe failed: {ex.Message}", sw, ex);
        }
    }

    private static async Task<StartupCheck> CheckVoiceHostReachableAsync(
        AppSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        const string name = "voicehost.reachable";
        var sw = Stopwatch.StartNew();

        if (!settings.Voice.VoiceHostEnabled)
        {
            return Skip(name, "VoiceHost disabled in settings", sw);
        }

        var healthUrl = settings.Voice.GetHealthUrl();
        if (string.IsNullOrWhiteSpace(healthUrl))
        {
            return Skip(name, "VoiceHost health URL not configured", sw);
        }

        using var http = new HttpClient { Timeout = timeout };
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(timeout);

        try
        {
            using var response = await http.GetAsync(healthUrl, probeCts.Token);
            return Ok(name,
                $"VoiceHost reachable at {healthUrl} (HTTP {(int)response.StatusCode})",
                sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // VoiceHost is typically launched on demand, so a failure here is
            // a Warning (worth surfacing) rather than Failed (hard blocker).
            return Warn(name, $"VoiceHost did not respond within {timeout.TotalSeconds:0}s — will be started on demand", sw);
        }
        catch (HttpRequestException ex)
        {
            return Warn(name, $"VoiceHost not yet running at {healthUrl} — will be started on demand: {ex.Message}", sw, ex);
        }
        catch (Exception ex)
        {
            return Warn(name, $"VoiceHost probe failed: {ex.Message}", sw, ex);
        }
    }

    private static Task<StartupCheck> CheckLogDirectoryWritableAsync(CancellationToken cancellationToken)
    {
        const string name = "logs.writable";
        var sw = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logsRoot = Path.Combine(localAppData, "SirThaddeus", "logs");

        try
        {
            Directory.CreateDirectory(logsRoot);
            var probe = Path.Combine(logsRoot, $".writable-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return Task.FromResult(Ok(name, $"Log directory writable: {logsRoot}", sw));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Failed(name, $"Log directory not writable: {ex.Message}", sw, ex));
        }
    }

    private static StartupCheck Ok(string name, string message, Stopwatch sw) => new()
    {
        Name = name,
        Status = StartupCheckStatus.Ok,
        Message = message,
        Elapsed = sw.Elapsed,
    };

    private static StartupCheck Skip(string name, string message, Stopwatch sw) => new()
    {
        Name = name,
        Status = StartupCheckStatus.Skipped,
        Message = message,
        Elapsed = sw.Elapsed,
    };

    private static StartupCheck Failed(string name, string message, Stopwatch sw, Exception? ex = null) => new()
    {
        Name = name,
        Status = StartupCheckStatus.Failed,
        Message = message,
        Elapsed = sw.Elapsed,
        Exception = ex,
    };

    private static StartupCheck Warn(string name, string message, Stopwatch sw, Exception? ex = null) => new()
    {
        Name = name,
        Status = StartupCheckStatus.Warning,
        Message = message,
        Elapsed = sw.Elapsed,
        Exception = ex,
    };
}
