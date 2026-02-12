using System.Diagnostics;

namespace SirThaddeus.VoiceHost.Backends;

/// <summary>
/// Ensures the local voice backend sidecar is running when VoiceHost needs it.
/// This enables one-click UX: DesktopRuntime starts VoiceHost, and VoiceHost
/// starts the backend automatically if external upstreams are not already ready.
/// </summary>
public sealed class VoiceBackendSupervisor : IDisposable
{
    private readonly VoiceHostRuntimeOptions _options;
    private readonly ILogger<VoiceBackendSupervisor> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly object _processGate = new();

    private Process? _managedProcess;
    private BackendSupervisorResult _lastResult = BackendSupervisorResult.Initial();
    private bool _disposed;

    public VoiceBackendSupervisor(
        VoiceHostRuntimeOptions options,
        ILogger<VoiceBackendSupervisor> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public BackendSupervisorResult LastResult => _lastResult;

    public async Task<BackendSupervisorResult> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.AutoStartBackends)
        {
            _lastResult = BackendSupervisorResult.Skipped(
                "backend_autostart_disabled",
                "Backend auto-start is disabled by configuration.");
            return _lastResult;
        }

        await _ensureLock.WaitAsync(cancellationToken);
        try
        {
            var current = await ProbeTargetsAsync(cancellationToken);
            if (current.Asr.Ready && current.Tts.Ready)
            {
                _lastResult = BackendSupervisorResult.Ok(
                    "backends_ready",
                    "Backend upstreams are ready.");
                return _lastResult;
            }

            if (!TryGetManagedBackendPort(out var backendPort, out var mismatchError))
            {
                _lastResult = BackendSupervisorResult.Failure(
                    "backend_port_mismatch",
                    mismatchError ?? "ASR/TTS upstream mismatch.");
                return _lastResult;
            }

            if (!ManagedProcessAlive())
            {
                var backendPath = ResolveBackendExecutablePath();
                if (!TryBuildStartInfo(backendPath, backendPort, out var startInfo, out var prepError))
                {
                    _lastResult = BackendSupervisorResult.Failure(
                        "backend_missing",
                        prepError ?? "Voice backend executable not found.",
                        executablePath: backendPath);
                    return _lastResult;
                }

                var started = StartManagedProcess(startInfo, backendPath);
                if (!started.Success)
                {
                    _lastResult = started;
                    return _lastResult;
                }
            }

            var wait = await WaitForReadyAsync(cancellationToken);
            _lastResult = wait;
            return wait;
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    private async Task<(BackendReadiness Asr, BackendReadiness Tts)> ProbeTargetsAsync(CancellationToken cancellationToken)
    {
        var asrTask = BackendHealthProbe.ProbeAsync(_httpClient, _options.AsrUpstreamUri, cancellationToken);
        var ttsTask = BackendHealthProbe.ProbeAsync(_httpClient, _options.TtsUpstreamUri, cancellationToken);
        await Task.WhenAll(asrTask, ttsTask);
        return (asrTask.Result, ttsTask.Result);
    }

    private async Task<BackendSupervisorResult> WaitForReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(_options.BackendStartupTimeoutMs);
        BackendReadiness lastAsr = BackendReadiness.NotReady("unknown");
        BackendReadiness lastTts = BackendReadiness.NotReady("unknown");

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = await ProbeTargetsAsync(cancellationToken);
            lastAsr = probe.Asr;
            lastTts = probe.Tts;

            if (probe.Asr.Ready && probe.Tts.Ready)
            {
                return BackendSupervisorResult.Ok(
                    "backends_ready",
                    "Backend upstreams are ready.",
                    processId: TryGetManagedProcessId());
            }

            if (ManagedProcessExited())
            {
                return BackendSupervisorResult.Failure(
                    "backend_exited",
                    "Managed voice backend exited before becoming ready.",
                    processId: TryGetManagedProcessId());
            }

            await Task.Delay(250, cancellationToken);
        }

        var detail = $"ASR: {lastAsr.Detail}; TTS: {lastTts.Detail}";
        return BackendSupervisorResult.Failure(
            "backend_startup_timeout",
            $"Voice backend did not become ready within {_options.BackendStartupTimeoutMs}ms. {detail}",
            processId: TryGetManagedProcessId());
    }

    private bool TryGetManagedBackendPort(out int port, out string? error)
    {
        port = 0;
        error = null;

        // Single sidecar backend mode supports one shared upstream host/port.
        if (!SameHostAndPort(_options.AsrUpstreamUri, _options.TtsUpstreamUri))
        {
            error = "Auto-start requires ASR and TTS upstreams to share the same loopback host/port.";
            return false;
        }

        port = _options.AsrUpstreamUri.Port;
        if (port is < 1 or > 65535)
        {
            error = "Configured backend upstream port is invalid.";
            return false;
        }

        return true;
    }

    private static bool SameHostAndPort(Uri a, Uri b)
        => string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
           a.Port == b.Port;

    private string ResolveBackendExecutablePath()
    {
        if (!string.Equals(_options.BackendExecutablePath, "auto", StringComparison.OrdinalIgnoreCase))
            return _options.BackendExecutablePath;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            // Preferred publish layout
            Path.Combine(baseDir, "voice", "voice-backend.exe"),
            // Portable fallback
            Path.Combine(baseDir, "voice-backend.exe"),
            // Dev fallback (PyInstaller dist under source tree)
            Path.GetFullPath(Path.Combine(
                baseDir, "..", "..", "..", "..", "..",
                "voice-backend", "dist", "voice-backend.exe")),
            // Dev fallback (raw Python script)
            Path.GetFullPath(Path.Combine(
                baseDir, "..", "..", "..", "..", "..",
                "voice-backend", "server.py"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Return preferred path so diagnostics are deterministic/actionable.
        return candidates[0];
    }

    private bool TryBuildStartInfo(
        string backendPath,
        int port,
        out ProcessStartInfo startInfo,
        out string? error)
    {
        startInfo = new ProcessStartInfo();
        error = null;

        if (!File.Exists(backendPath))
        {
            error = $"Backend path not found: {backendPath}";
            return false;
        }

        var ext = Path.GetExtension(backendPath);
        var workingDir = Path.GetDirectoryName(backendPath) ?? AppContext.BaseDirectory;

        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"{QuoteArg(backendPath)} --port {port}",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            return true;
        }

        startInfo = new ProcessStartInfo
        {
            FileName = backendPath,
            Arguments = $"--port {port}",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        return true;
    }

    private BackendSupervisorResult StartManagedProcess(ProcessStartInfo startInfo, string resolvedPath)
    {
        try
        {
            StopManagedProcess(graceful: true);

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return BackendSupervisorResult.Failure(
                    "backend_start_failed",
                    "Process.Start returned null.",
                    executablePath: resolvedPath);
            }

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                _logger.LogWarning(
                    "Voice backend exited. pid={Pid} exitCode={ExitCode}",
                    SafePid(process),
                    SafeExitCode(process));
            };
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                // Optional stream readers; process can still run without them.
            }

            lock (_processGate)
            {
                _managedProcess = process;
            }

            _logger.LogInformation(
                "Voice backend started. path={Path} pid={Pid} args={Args}",
                resolvedPath,
                process.Id,
                startInfo.Arguments);

            return BackendSupervisorResult.Ok(
                "backend_started",
                "Managed backend process started.",
                processId: process.Id,
                executablePath: resolvedPath);
        }
        catch (Exception ex)
        {
            return BackendSupervisorResult.Failure(
                "backend_start_failed",
                ex.Message,
                executablePath: resolvedPath);
        }
    }

    private bool ManagedProcessAlive()
    {
        lock (_processGate)
            return _managedProcess is not null && !_managedProcess.HasExited;
    }

    private bool ManagedProcessExited()
    {
        lock (_processGate)
            return _managedProcess is not null && _managedProcess.HasExited;
    }

    private int? TryGetManagedProcessId()
    {
        lock (_processGate)
        {
            if (_managedProcess is null)
                return null;
            return SafePid(_managedProcess);
        }
    }

    private void StopManagedProcess(bool graceful)
    {
        Process? process;
        lock (_processGate)
        {
            process = _managedProcess;
            _managedProcess = null;
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited && graceful)
                TryGracefulShutdown(process);

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(Math.Min(2_000, _options.BackendShutdownGraceMs));
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            process.Dispose();
        }
    }

    private void TryGracefulShutdown(Process process)
    {
        try
        {
            if (TryRequestShutdown())
            {
                process.WaitForExit(_options.BackendShutdownGraceMs);
                return;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            if (process.CloseMainWindow())
                process.WaitForExit(_options.BackendShutdownGraceMs);
        }
        catch
        {
            // best effort
        }
    }

    private bool TryRequestShutdown()
    {
        try
        {
            var shutdownUri = DeriveShutdownUri(_options.AsrUpstreamUri);
            using var cts = new CancellationTokenSource(Math.Min(2_000, _options.BackendShutdownGraceMs));
            using var response = _httpClient.PostAsync(shutdownUri, content: null, cts.Token)
                .GetAwaiter()
                .GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static Uri DeriveShutdownUri(Uri upstreamUri)
    {
        var path = upstreamUri.AbsolutePath;
        if (path.EndsWith("/asr", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/tts", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            path = "/";
        }

        return new UriBuilder(upstreamUri)
        {
            Path = string.IsNullOrWhiteSpace(path) || path == "/" ? "/shutdown" : $"{path.TrimEnd('/')}/shutdown",
            Query = ""
        }.Uri;
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"\"";
        if (!value.Contains('"'))
            return $"\"{value}\"";
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static int SafePid(Process process)
    {
        try { return process.Id; } catch { return 0; }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _ensureLock.Wait();
        try
        {
            StopManagedProcess(graceful: true);
            _httpClient.Dispose();
        }
        finally
        {
            _ensureLock.Release();
            _ensureLock.Dispose();
        }
    }
}

public sealed record BackendSupervisorResult
{
    public required bool Success { get; init; }
    public required string ErrorCode { get; init; }
    public string Message { get; init; } = "";
    public int? ProcessId { get; init; }
    public string? ExecutablePath { get; init; }

    public static BackendSupervisorResult Initial() => new()
    {
        Success = true,
        ErrorCode = "",
        Message = "Not evaluated yet."
    };

    public static BackendSupervisorResult Ok(
        string errorCode,
        string message,
        int? processId = null,
        string? executablePath = null) => new()
    {
        Success = true,
        ErrorCode = errorCode,
        Message = message,
        ProcessId = processId,
        ExecutablePath = executablePath
    };

    public static BackendSupervisorResult Skipped(string errorCode, string message) => new()
    {
        Success = true,
        ErrorCode = errorCode,
        Message = message
    };

    public static BackendSupervisorResult Failure(
        string errorCode,
        string message,
        int? processId = null,
        string? executablePath = null) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        Message = message,
        ProcessId = processId,
        ExecutablePath = executablePath
    };
}
