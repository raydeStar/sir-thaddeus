using System.Diagnostics;
using System.Net;

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

    /// <summary>
    /// Ensures ASR/TTS backends are ready and that YouTube API routes exist.
    /// If the upstream is stale (healthy for ASR/TTS but missing YouTube routes),
    /// attempts a one-time recycle before surfacing an actionable failure.
    /// </summary>
    public async Task<BackendSupervisorResult> EnsureYouTubeReadyAsync(CancellationToken cancellationToken)
    {
        var ensure = await EnsureRunningAsync(cancellationToken);
        if (!ensure.Success)
            return ensure;

        if (await SupportsYouTubeApiAsync(cancellationToken))
            return ensure;

        _logger.LogWarning(
            "Backend upstream missing YouTube routes; attempting recycle on port {Port}.",
            _options.AsrUpstreamUri.Port);

        TryRequestShutdown();
        StopManagedProcess(graceful: true);

        await Task.Delay(400, cancellationToken);

        ensure = await EnsureRunningAsync(cancellationToken);
        if (!ensure.Success)
            return ensure;

        if (await SupportsYouTubeApiAsync(cancellationToken))
            return ensure;

        _lastResult = BackendSupervisorResult.Failure(
            "backend_youtube_api_missing",
            "Voice backend is reachable but missing YouTube API routes. " +
            "Restart VoiceHost from Settings and ensure apps/voice-backend/server.py is up to date.");
        return _lastResult;
    }

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

    private async Task<bool> SupportsYouTubeApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            var uri = new UriBuilder(_options.AsrUpstreamUri)
            {
                Path = "/api/youtube/transcribe",
                Query = ""
            }.Uri;

            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            return response.StatusCode != HttpStatusCode.NotFound;
        }
        catch
        {
            return false;
        }
    }

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
        var engineArgs = BuildEngineArgs();

        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"{QuoteArg(backendPath)} --port {port}{engineArgs}",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ApplyEngineEnvironment(startInfo);
            return true;
        }

        startInfo = new ProcessStartInfo
        {
            FileName = backendPath,
            Arguments = $"--port {port}{engineArgs}",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ApplyEngineEnvironment(startInfo);
        return true;
    }

    private string BuildEngineArgs()
    {
        var args = $" --tts-engine {QuoteArg(_options.TtsEngine)}" +
                   $" --stt-engine {QuoteArg(_options.SttEngine)}" +
                   $" --stt-model-id {QuoteArg(_options.SttModelId)}";
        if (!string.IsNullOrWhiteSpace(_options.SttLanguage))
            args += $" --stt-language {QuoteArg(_options.SttLanguage)}";

        if (!string.IsNullOrWhiteSpace(_options.TtsModelId))
            args += $" --tts-model-id {QuoteArg(_options.TtsModelId)}";
        if (!string.IsNullOrWhiteSpace(_options.TtsVoiceId))
            args += $" --tts-voice-id {QuoteArg(_options.TtsVoiceId)}";

        return args;
    }

    private void ApplyEngineEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment["ST_VOICE_TTS_ENGINE"] = _options.TtsEngine;
        startInfo.Environment["ST_VOICE_TTS_MODEL_ID"] = _options.TtsModelId;
        startInfo.Environment["ST_VOICE_TTS_VOICE_ID"] = _options.TtsVoiceId;
        startInfo.Environment["ST_VOICE_STT_ENGINE"] = _options.SttEngine;
        startInfo.Environment["ST_VOICE_STT_MODEL_ID"] = _options.SttModelId;
        startInfo.Environment["ST_VOICE_STT_LANGUAGE"] = _options.SttLanguage;

        // Compose an effective PATH from process + user + machine values so
        // child processes see recently installed tools without requiring logoff.
        var effectivePath = BuildEffectivePath();
        if (!string.IsNullOrWhiteSpace(effectivePath))
            startInfo.Environment["PATH"] = effectivePath;

        var lookupPath = startInfo.Environment.TryGetValue("PATH", out var pathValue)
            ? pathValue
            : string.Empty;
        var ytDlpPath = ResolveExecutablePathOnPath("yt-dlp", lookupPath);
        var ffmpegPath = ResolveExecutablePathOnPath("ffmpeg", lookupPath);

        if (!string.IsNullOrWhiteSpace(ytDlpPath))
            startInfo.Environment["ST_YOUTUBE_YTDLP_PATH"] = ytDlpPath;
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
            startInfo.Environment["ST_YOUTUBE_FFMPEG_PATH"] = ffmpegPath;

        _logger.LogInformation(
            "Prepared backend tool paths. yt-dlp={YtDlpAvailable} ffmpeg={FfmpegAvailable}",
            !string.IsNullOrWhiteSpace(ytDlpPath),
            !string.IsNullOrWhiteSpace(ffmpegPath));
    }

    private static string BuildEffectivePath()
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Append(Environment.GetEnvironmentVariable("PATH"));
        Append(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        Append(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));

        return string.Join(Path.PathSeparator, entries);

        void Append(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return;

            var parts = rawPath.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var expanded = Environment.ExpandEnvironmentVariables(part.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(expanded))
                    continue;

                if (seen.Add(expanded))
                    entries.Add(expanded);
            }
        }
    }

    private static string? ResolveExecutablePathOnPath(string command, string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        var names = BuildCommandNameCandidates(command);
        var directories = pathValue.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dirRaw in directories)
        {
            var dir = dirRaw.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            foreach (var name in names)
            {
                try
                {
                    var fullPath = Path.Combine(dir, name);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
                catch
                {
                    // Continue scanning remaining PATH entries.
                }
            }
        }

        return null;
    }

    private static List<string> BuildCommandNameCandidates(string command)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(command))
            return names;

        if (seen.Add(command))
            names.Add(command);

        if (Path.HasExtension(command))
            return names;

        var pathExtRaw = Environment.GetEnvironmentVariable("PATHEXT");
        var extParts = string.IsNullOrWhiteSpace(pathExtRaw)
            ? new[] { ".EXE", ".CMD", ".BAT" }
            : pathExtRaw.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var extRaw in extParts)
        {
            var ext = extRaw.StartsWith(".", StringComparison.Ordinal) ? extRaw : "." + extRaw;
            var candidate = command + ext;
            if (seen.Add(candidate))
                names.Add(candidate);
        }

        return names;
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
