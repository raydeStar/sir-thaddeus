using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Ensures the local VoiceHost process is reachable and ready for ASR/TTS.
/// Startup is lazy and triggered on first voice usage.
/// </summary>
public sealed class VoiceHostProcessManager : IAsyncDisposable
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IAuditLogger _auditLogger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly object _settingsGate = new();
    private readonly object _processGate = new();
    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly Func<string> _voiceHostPathResolver;
    private readonly Func<int?> _ephemeralPortProvider;
    private readonly TimeProvider _timeProvider;
    private readonly string _sessionStatePath;

    private VoiceSettings _settings;
    private Process? _managedProcess;
    private int? _managedProcessPort;
    private string? _currentBaseUrl;
    private int? _currentPort;
    private int _warmupScheduled;
    private int _staleSessionReaped;
    private bool _disposed;

    public VoiceHostProcessManager(
        IAuditLogger auditLogger,
        VoiceSettings settings,
        HttpMessageHandler? httpMessageHandler = null,
        Func<ProcessStartInfo, Process?>? processStarter = null,
        TimeProvider? timeProvider = null,
        Func<string>? voiceHostPathResolver = null,
        Func<int?>? ephemeralPortProvider = null)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = httpMessageHandler is null
            ? new HttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(2);
        _processStarter = processStarter ?? Process.Start;
        _voiceHostPathResolver = voiceHostPathResolver ?? ResolveVoiceHostPath;
        _ephemeralPortProvider = ephemeralPortProvider ?? TryGetEphemeralPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SirThaddeus",
            "voicehost-session.json");
    }

    public string CurrentBaseUrl
    {
        get
        {
            var snapshot = GetSettingsSnapshot();
            return _currentBaseUrl ?? snapshot.GetVoiceHostBaseUrl();
        }
    }

    public int? CurrentPort => _currentPort;

    /// <summary>
    /// Probes the health endpoint without starting a process.
    /// Used by the UI health panel for on-demand status checks.
    /// </summary>
    public async Task<VoiceHostHealthResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettingsSnapshot();
        if (!TryNormalizeBaseUrl(settings.GetVoiceHostBaseUrl(), out var baseUrl, out _))
            return VoiceHostHealthResult.Unreachable("invalid_voicehost_base", "VoiceHost base URL is invalid.");

        var healthPath = string.IsNullOrWhiteSpace(settings.VoiceHostHealthPath)
            ? "/health"
            : settings.VoiceHostHealthPath.Trim();

        return await ProbeHealthAsync(baseUrl, healthPath, cancellationToken);
    }

    /// <summary>
    /// Kills the managed VoiceHost process if one is alive.
    /// Clears session state so health checks reflect the stopped state.
    /// </summary>
    public void Stop()
    {
        StopManagedProcessIfAny();
        _currentBaseUrl = null;
        _currentPort = null;
        WriteAudit("VOICEHOST_MANUAL_STOP", "ok", new Dictionary<string, object>
        {
            ["reason"] = "user_requested"
        });
    }

    public void UpdateSettings(VoiceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var shouldRestartManagedProcess = false;
        lock (_settingsGate)
        {
            var oldSettings = _settings;
            _settings = settings;
            shouldRestartManagedProcess =
                !string.Equals(oldSettings.GetNormalizedTtsEngine(), settings.GetNormalizedTtsEngine(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(oldSettings.GetResolvedTtsModelId(), settings.GetResolvedTtsModelId(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(oldSettings.GetResolvedTtsVoiceId(), settings.GetResolvedTtsVoiceId(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(oldSettings.GetNormalizedSttEngine(), settings.GetNormalizedSttEngine(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(oldSettings.GetResolvedSttModelId(), settings.GetResolvedSttModelId(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(oldSettings.GetResolvedSttLanguage(), settings.GetResolvedSttLanguage(), StringComparison.OrdinalIgnoreCase);
        }

        if (shouldRestartManagedProcess && HasManagedProcessAlive())
        {
            StopManagedProcessIfAny();
            _currentBaseUrl = null;
            _currentPort = null;
            WriteAudit("VOICEHOST_PROCESS_RESTART_REQUIRED", "ok", new Dictionary<string, object>
            {
                ["reason"] = "engine_settings_changed"
            });
        }
    }

    public void ScheduleWarmup(TimeSpan delay, bool startIfMissing = false)
    {
        if (Interlocked.Exchange(ref _warmupScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                var result = startIfMissing
                    ? await EnsureRunningAsync(CancellationToken.None)
                    : await ProbeOnlyAsync(CancellationToken.None);
                if (!result.Success)
                {
                    WriteAudit("VOICEHOST_WARMUP_FAILED", "error", new Dictionary<string, object>
                    {
                        ["errorCode"] = result.ErrorCode ?? "",
                        ["message"] = result.UserMessage
                    });
                }
            }
            catch (Exception ex)
            {
                WriteAudit("VOICEHOST_WARMUP_FAILED", "error", new Dictionary<string, object>
                {
                    ["message"] = ex.Message
                });
            }
        });
    }

    private async Task<VoiceHostEnsureResult> ProbeOnlyAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettingsSnapshot();
        if (!settings.VoiceHostEnabled)
            return VoiceHostEnsureResult.Ok(settings.GetVoiceHostBaseUrl());

        if (!TryNormalizeBaseUrl(settings.GetVoiceHostBaseUrl(), out var preferredBaseUrl, out var normalizedError))
        {
            return VoiceHostEnsureResult.Failure(
                "invalid_voicehost_base",
                normalizedError ?? "VoiceHost base URL is invalid.");
        }

        var healthPath = string.IsNullOrWhiteSpace(settings.VoiceHostHealthPath)
            ? "/health"
            : settings.VoiceHostHealthPath.Trim();
        var preferredHealth = await ProbeHealthAsync(preferredBaseUrl, healthPath, cancellationToken);
        if (preferredHealth.Ready)
        {
            var port = new Uri(preferredBaseUrl).Port;
            SetCurrentSession(preferredBaseUrl, port, processId: null);
            return VoiceHostEnsureResult.Ok(preferredBaseUrl);
        }

        return VoiceHostEnsureResult.Failure(
            "voicehost_not_ready",
            "Voice components are not ready yet.");
    }

    public async Task<VoiceHostEnsureResult> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = GetSettingsSnapshot();
        if (!settings.VoiceHostEnabled)
        {
            return VoiceHostEnsureResult.Ok(settings.GetVoiceHostBaseUrl());
        }

        await _ensureLock.WaitAsync(cancellationToken);
        try
        {
            TryReapStaleSessionProcess();
            settings = GetSettingsSnapshot();
            if (!TryNormalizeBaseUrl(settings.GetVoiceHostBaseUrl(), out var preferredBaseUrl, out var normalizedError))
            {
                return VoiceHostEnsureResult.Failure(
                    "invalid_voicehost_base",
                    normalizedError ?? "VoiceHost base URL is invalid.");
            }

            var preferredPort = new Uri(preferredBaseUrl).Port;
            var preferredHost = new Uri(preferredBaseUrl).Host;
            var healthPath = string.IsNullOrWhiteSpace(settings.VoiceHostHealthPath)
                ? "/health"
                : settings.VoiceHostHealthPath.Trim();

            // Fast path: if current base is already ready, no process action needed.
            if (!string.IsNullOrWhiteSpace(_currentBaseUrl))
            {
                var currentHealth = await ProbeHealthAsync(_currentBaseUrl, healthPath, cancellationToken);
                if (currentHealth.Ready)
                    return VoiceHostEnsureResult.Ok(_currentBaseUrl);
            }

            // Next quick path: preferred port already has a ready VoiceHost.
            var preferredHealth = await ProbeHealthAsync(preferredBaseUrl, healthPath, cancellationToken);
            if (preferredHealth.Ready)
            {
                SetCurrentSession(preferredBaseUrl, preferredPort, processId: null);
                return VoiceHostEnsureResult.Ok(preferredBaseUrl);
            }

            var hostPath = _voiceHostPathResolver.Invoke();
            if (!File.Exists(hostPath))
            {
                return VoiceHostEnsureResult.Failure(
                    "voicehost_missing",
                    "VoiceHost component missing. Reinstall to restore voice.");
            }

            var startupTimeout = TimeSpan.FromMilliseconds(
                Math.Max(5_000, settings.VoiceHostStartupTimeoutMs));
            var deadline = _timeProvider.GetUtcNow() + startupTimeout;
            var lastHealth = preferredHealth;
            var lastStartError = "";
            var keepManagedProcessAlive = false;

            // Preferred + bounded fallback range.
            var candidatePorts = BuildPortCandidates(preferredPort);
            foreach (var port in candidatePorts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_timeProvider.GetUtcNow() >= deadline)
                    break;

                var baseUrl = $"http://{preferredHost}:{port}";
                var existingHealth = await ProbeHealthAsync(baseUrl, healthPath, cancellationToken);
                lastHealth = existingHealth;
                if (existingHealth.Ready)
                {
                    SetCurrentSession(baseUrl, port, processId: null);
                    return VoiceHostEnsureResult.Ok(baseUrl);
                }

                var managedOnCandidatePort = IsManagedProcessAliveOnPort(port);
                if (!managedOnCandidatePort)
                {
                    var startResult = StartManagedProcess(hostPath, port, settings);
                    if (!startResult.Started)
                    {
                        lastStartError = startResult.Error;
                        continue;
                    }
                }

                var wait = await WaitForReadyAsync(baseUrl, healthPath, deadline, cancellationToken);
                lastHealth = wait;
                if (wait.Ready)
                {
                    var pid = TryGetManagedProcessId();
                    SetCurrentSession(baseUrl, port, pid);
                    return VoiceHostEnsureResult.Ok(baseUrl);
                }

                if (!string.IsNullOrWhiteSpace(wait.Message))
                    lastStartError = wait.Message;

                // Keep the same managed process running if it's reachable but still loading.
                // This avoids cold-start thrash on repeated readiness probes.
                if (wait.Reachable && IsManagedProcessAliveOnPort(port))
                {
                    keepManagedProcessAlive = true;
                    break;
                }

                StopManagedProcessIfAny();
            }

            // Ephemeral fallback after bounded deterministic range.
            if (!keepManagedProcessAlive &&
                _timeProvider.GetUtcNow() < deadline &&
                (_ephemeralPortProvider.Invoke()) is int dynamicPort)
            {
                var dynamicBase = $"http://{preferredHost}:{dynamicPort}";
                var existingDynamic = await ProbeHealthAsync(dynamicBase, healthPath, cancellationToken);
                lastHealth = existingDynamic;
                if (existingDynamic.Ready)
                {
                    SetCurrentSession(dynamicBase, dynamicPort, processId: null);
                    return VoiceHostEnsureResult.Ok(dynamicBase);
                }

                var managedOnDynamicPort = IsManagedProcessAliveOnPort(dynamicPort);
                if (!managedOnDynamicPort)
                {
                    var started = StartManagedProcess(hostPath, dynamicPort, settings);
                    if (!started.Started)
                    {
                        lastStartError = started.Error;
                        managedOnDynamicPort = false;
                    }
                    else
                    {
                        managedOnDynamicPort = true;
                    }
                }

                if (managedOnDynamicPort)
                {
                    var ready = await WaitForReadyAsync(dynamicBase, healthPath, deadline, cancellationToken);
                    lastHealth = ready;
                    if (ready.Ready)
                    {
                        var pid = TryGetManagedProcessId();
                        SetCurrentSession(dynamicBase, dynamicPort, pid);
                        return VoiceHostEnsureResult.Ok(dynamicBase);
                    }

                    if (!string.IsNullOrWhiteSpace(ready.Message))
                        lastStartError = ready.Message;

                    if (ready.Reachable && IsManagedProcessAliveOnPort(dynamicPort))
                    {
                        keepManagedProcessAlive = true;
                    }
                    else
                    {
                        StopManagedProcessIfAny();
                    }
                }
            }

            if (!keepManagedProcessAlive)
                StopManagedProcessIfAny();

            if (keepManagedProcessAlive && lastHealth.Reachable && !lastHealth.Ready)
            {
                return VoiceHostEnsureResult.Failure(
                    "voicehost_warming_up",
                    BuildHealthFailureMessage(lastHealth));
            }

            if (LooksLikePortInUse(lastStartError))
            {
                return VoiceHostEnsureResult.Failure(
                    "voicehost_port_unavailable",
                    "VoiceHost could not bind to an available local port.");
            }

            if (lastHealth.Reachable && !lastHealth.Ready)
            {
                return VoiceHostEnsureResult.Failure(
                    "voicehost_unhealthy",
                    BuildHealthFailureMessage(lastHealth));
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                return VoiceHostEnsureResult.Failure(
                    "voicehost_startup_timeout",
                    $"VoiceHost startup timed out after {(int)startupTimeout.TotalMilliseconds}ms.");
            }

            return VoiceHostEnsureResult.Failure(
                "voicehost_not_ready",
                "VoiceHost failed to become ready for voice.");
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    private VoiceSettings GetSettingsSnapshot()
    {
        lock (_settingsGate)
        {
            return _settings;
        }
    }

    private (bool Started, string Error) StartManagedProcess(
        string hostPath,
        int port,
        VoiceSettings settings)
    {
        try
        {
            StopManagedProcessIfAny();

            var args = $"--port {port} --bind 127.0.0.1 --mode proxy-first";
            if (!string.IsNullOrWhiteSpace(settings.AsrEndpoint))
                args += $" --asr-upstream {QuoteArg(settings.AsrEndpoint.Trim())}";
            if (!string.IsNullOrWhiteSpace(settings.TtsEndpoint))
                args += $" --tts-upstream {QuoteArg(settings.TtsEndpoint.Trim())}";
            args += $" --tts-engine {QuoteArg(settings.GetNormalizedTtsEngine())}";
            args += $" --stt-engine {QuoteArg(settings.GetNormalizedSttEngine())}";

            var resolvedSttModelId = settings.GetResolvedSttModelId();
            if (!string.IsNullOrWhiteSpace(resolvedSttModelId))
                args += $" --stt-model-id {QuoteArg(resolvedSttModelId)}";
            var resolvedSttLanguage = settings.GetResolvedSttLanguage();
            if (!string.IsNullOrWhiteSpace(resolvedSttLanguage))
                args += $" --stt-language {QuoteArg(resolvedSttLanguage)}";

            var resolvedTtsModelId = settings.GetResolvedTtsModelId();
            if (!string.IsNullOrWhiteSpace(resolvedTtsModelId))
                args += $" --tts-model-id {QuoteArg(resolvedTtsModelId)}";

            var resolvedTtsVoiceId = settings.GetResolvedTtsVoiceId();
            if (!string.IsNullOrWhiteSpace(resolvedTtsVoiceId))
                args += $" --tts-voice-id {QuoteArg(resolvedTtsVoiceId)}";

            var startInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(hostPath) ?? AppContext.BaseDirectory
            };

            var process = _processStarter(startInfo);
            if (process is null)
            {
                WriteAudit("VOICEHOST_PROCESS_START_FAILED", "error", new Dictionary<string, object>
                {
                    ["path"] = hostPath,
                    ["port"] = port
                });
                return (false, "Process.Start returned null.");
            }

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                WriteAudit("VOICEHOST_PROCESS_EXITED", "ok", new Dictionary<string, object>
                {
                    ["pid"] = process.Id,
                    ["exitCode"] = process.ExitCode
                });
            };

            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                // Not fatal; process can still run without stream readers.
            }

            lock (_processGate)
            {
                _managedProcess = process;
                _managedProcessPort = port;
            }

            WriteAudit("VOICEHOST_PROCESS_STARTED", "ok", new Dictionary<string, object>
            {
                ["path"] = hostPath,
                ["port"] = port,
                ["pid"] = process.Id,
                ["args"] = args
            });

            return (true, "");
        }
        catch (Exception ex)
        {
            WriteAudit("VOICEHOST_PROCESS_START_FAILED", "error", new Dictionary<string, object>
            {
                ["path"] = hostPath,
                ["port"] = port,
                ["message"] = ex.Message
            });
            return (false, ex.Message);
        }
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"\"";
        if (!value.Contains('\"'))
            return $"\"{value}\"";
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private async Task<VoiceHostHealthResult> WaitForReadyAsync(
        string baseUrl,
        string healthPath,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var lastHealth = VoiceHostHealthResult.Unreachable();
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var health = await ProbeHealthAsync(baseUrl, healthPath, cancellationToken);
            lastHealth = health;
            if (health.Ready)
                return health;

            if (HasManagedProcessExited())
            {
                return VoiceHostHealthResult.Unreachable(
                    "voicehost_process_exited",
                    "VoiceHost process exited before readiness.");
            }

            await Task.Delay(250, cancellationToken);
        }

        if (lastHealth.Reachable)
            return lastHealth;

        if (HasManagedProcessAlive())
        {
            return new VoiceHostHealthResult(
                Reachable: true,
                Ready: false,
                Status: "loading",
                AsrReady: false,
                TtsReady: false,
                Version: "",
                ErrorCode: "voicehost_warming_up",
                Message: "VoiceHost is still warming up.");
        }

        return VoiceHostHealthResult.Unreachable(
            "voicehost_unreachable",
            "VoiceHost health endpoint was unreachable before timeout.");
    }

    private async Task<VoiceHostHealthResult> ProbeHealthAsync(
        string baseUrl,
        string healthPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = CombineUrl(baseUrl, healthPath);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HealthProbeTimeout);

            using var response = await _httpClient.GetAsync(url, timeoutCts.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new VoiceHostHealthResult(
                    Reachable: true,
                    Ready: false,
                    Status: "",
                    AsrReady: false,
                    TtsReady: false,
                    Version: "",
                    ErrorCode: "voicehost_health_http_status",
                    Message: $"VoiceHost health endpoint returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(body))
            {
                return new VoiceHostHealthResult(
                    Reachable: true,
                    Ready: false,
                    Status: "",
                    AsrReady: false,
                    TtsReady: false,
                    Version: "",
                    ErrorCode: "voicehost_health_empty",
                    Message: "VoiceHost health endpoint returned an empty body.");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var status = ReadString(root, "status");
            var ready = ReadBool(root, "ready");
            var asrReady = ReadBool(root, "asrReady");
            var ttsReady = ReadBool(root, "ttsReady");
            var version = ReadString(root, "version");
            var errorCode = ReadString(root, "errorCode");
            var message = ReadString(root, "message");

            var isReady =
                string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) &&
                ready;

            return new VoiceHostHealthResult(
                Reachable: true,
                Ready: isReady,
                Status: status ?? "",
                AsrReady: asrReady,
                TtsReady: ttsReady,
                Version: version ?? "",
                ErrorCode: errorCode ?? "",
                Message: message ?? "");
        }
        catch
        {
            return VoiceHostHealthResult.Unreachable(
                "voicehost_unreachable",
                "VoiceHost health endpoint was unreachable.");
        }
    }

    private void SetCurrentSession(string baseUrl, int port, int? processId)
    {
        _currentBaseUrl = baseUrl.TrimEnd('/');
        _currentPort = port;
        PersistSessionState(_currentBaseUrl, port, processId);
        WriteAudit("VOICEHOST_READY", "ok", new Dictionary<string, object>
        {
            ["baseUrl"] = _currentBaseUrl,
            ["port"] = port,
            ["pid"] = processId ?? 0
        });
    }

    private static IEnumerable<int> BuildPortCandidates(int preferredPort)
    {
        var candidates = new List<int>();
        var boundedRange = Enumerable.Range(17845, 11).ToArray();
        if (preferredPort is > 0 and <= 65535 && boundedRange.Contains(preferredPort))
        {
            candidates.Add(preferredPort);
            candidates.AddRange(boundedRange.Where(p => p != preferredPort));
        }
        else
        {
            if (preferredPort is > 0 and <= 65535)
                candidates.Add(preferredPort);
            candidates.AddRange(boundedRange);
        }

        return candidates.Distinct();
    }

    private static int? TryGetEphemeralPort()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryNormalizeBaseUrl(
        string rawBaseUrl,
        out string normalizedBaseUrl,
        out string? error)
    {
        normalizedBaseUrl = "";
        error = null;

        if (!Uri.TryCreate(rawBaseUrl, UriKind.Absolute, out var uri))
        {
            error = "VoiceHost base URL is invalid.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = "VoiceHost base URL must use http.";
            return false;
        }

        var isLoopbackHost =
            string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!isLoopbackHost)
        {
            error = "VoiceHost base URL must be loopback (127.0.0.1 or localhost).";
            return false;
        }

        var normalizedHost = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : uri.Host;
        normalizedBaseUrl = $"{uri.Scheme}://{normalizedHost}:{uri.Port}";
        return true;
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        var path = string.IsNullOrWhiteSpace(relativePath) ? "/" : relativePath.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;
        return baseUrl.TrimEnd('/') + path;
    }

    private static bool LooksLikePortInUse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHealthFailureMessage(VoiceHostHealthResult health)
    {
        if (!health.Ready &&
            (string.Equals(health.Status, "loading", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(health.ErrorCode, "voicehost_warming_up", StringComparison.OrdinalIgnoreCase)))
        {
            var loadingDetail = string.IsNullOrWhiteSpace(health.Message)
                ? "VoiceHost is starting local voice components."
                : health.Message;
            return AppendVoiceRemediation($"Voice components are still warming up. {loadingDetail}");
        }

        if (!string.IsNullOrWhiteSpace(health.Message))
            return AppendVoiceRemediation(health.Message);

        var reason = !string.IsNullOrWhiteSpace(health.ErrorCode)
            ? health.ErrorCode
            : "dependency_not_ready";
        var fallbackMessage = $"VoiceHost dependencies are not ready ({reason}). " +
                              $"asrReady={health.AsrReady}, ttsReady={health.TtsReady}.";
        return AppendVoiceRemediation(fallbackMessage);
    }

    private static string AppendVoiceRemediation(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        var lower = message.ToLowerInvariant();
        if (lower.Contains("manifest_missing", StringComparison.Ordinal) ||
            lower.Contains("artifact_", StringComparison.Ordinal))
        {
            return message +
                   " Install voice assets via ./dev/install-kokoro-assets.ps1 " +
                   "or switch settings.voice.ttsEngine to 'windows'.";
        }

        if (lower.Contains("runtime_not_configured", StringComparison.Ordinal))
        {
            return message +
                   " Qwen3-ASR is scaffolded but not promoted yet; keep settings.voice.sttEngine='faster-whisper' " +
                   "until runtime wiring is completed.";
        }

        if (lower.Contains("tts_voice_id_required", StringComparison.Ordinal))
        {
            return message + " Set settings.voice.ttsVoiceId to an installed Kokoro voice pack id.";
        }

        return message;
    }

    private string ResolveVoiceHostPath()
    {
        const string exeName = "SirThaddeus.VoiceHost.exe";
        var baseDir = AppContext.BaseDirectory;

        var adjacent = Path.Combine(baseDir, exeName);
        if (File.Exists(adjacent))
            return adjacent;

        var voiceHostBinDebug = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..",
            "voice-host", "SirThaddeus.VoiceHost",
            "bin", "Debug"));

        if (Directory.Exists(voiceHostBinDebug))
        {
            string? newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var tfmDir in Directory.GetDirectories(voiceHostBinDebug))
            {
                var candidate = Path.Combine(tfmDir, exeName);
                if (!File.Exists(candidate))
                    continue;

                var writeTime = File.GetLastWriteTimeUtc(candidate);
                if (writeTime > newestTime)
                {
                    newest = candidate;
                    newestTime = writeTime;
                }
            }

            if (newest is not null)
                return newest;
        }

        return Path.GetFullPath(Path.Combine(
            voiceHostBinDebug,
            "net8.0",
            exeName));
    }

    private bool HasManagedProcessExited()
    {
        lock (_processGate)
        {
            return _managedProcess is not null && _managedProcess.HasExited;
        }
    }

    private bool HasManagedProcessAlive()
    {
        lock (_processGate)
        {
            return _managedProcess is not null && !_managedProcess.HasExited;
        }
    }

    private bool IsManagedProcessAliveOnPort(int port)
    {
        lock (_processGate)
        {
            return _managedProcess is not null &&
                   !_managedProcess.HasExited &&
                   _managedProcessPort == port;
        }
    }

    private int? TryGetManagedProcessId()
    {
        lock (_processGate)
        {
            if (_managedProcess is null)
                return null;
            try
            {
                return _managedProcess.Id;
            }
            catch
            {
                return null;
            }
        }
    }

    private void StopManagedProcessIfAny()
    {
        lock (_processGate)
        {
            if (_managedProcess is null)
            {
                _managedProcessPort = null;
                return;
            }

            try
            {
                if (!_managedProcess.HasExited)
                    _managedProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }
            finally
            {
                _managedProcess.Dispose();
                _managedProcess = null;
                _managedProcessPort = null;
            }
        }
    }

    private void PersistSessionState(string baseUrl, int port, int? processId)
    {
        try
        {
            var dir = Path.GetDirectoryName(_sessionStatePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var payload = JsonSerializer.Serialize(new
            {
                baseUrl,
                port,
                pid = processId,
                updatedAtUtc = DateTimeOffset.UtcNow
            });
            File.WriteAllText(_sessionStatePath, payload);
        }
        catch
        {
            // diagnostics-only write
        }
    }

    private void TryReapStaleSessionProcess()
    {
        if (Interlocked.Exchange(ref _staleSessionReaped, 1) == 1)
            return;

        // If another runtime instance is alive, do not reap shared voice infra.
        if (HasAnotherDesktopRuntimeAlive())
            return;

        try
        {
            if (!File.Exists(_sessionStatePath))
                return;

            var json = File.ReadAllText(_sessionStatePath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pid", out var pidElem))
                return;
            if (!pidElem.TryGetInt32(out var pid) || pid <= 0)
                return;

            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return;

            string name;
            try
            {
                name = process.ProcessName;
            }
            catch
            {
                return;
            }

            if (!name.Contains("VoiceHost", StringComparison.OrdinalIgnoreCase))
                return;

            process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);

            WriteAudit("VOICEHOST_STALE_PROCESS_REAPED", "ok", new Dictionary<string, object>
            {
                ["pid"] = pid,
                ["processName"] = name
            });

            try { File.Delete(_sessionStatePath); } catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            WriteAudit("VOICEHOST_STALE_PROCESS_REAP_FAILED", "error", new Dictionary<string, object>
            {
                ["message"] = ex.Message
            });
        }
    }

    private static bool HasAnotherDesktopRuntimeAlive()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName("SirThaddeus.DesktopRuntime"))
            {
                using (process)
                {
                    if (process.Id == currentPid)
                        continue;

                    if (!process.HasExited)
                        return true;
                }
            }
        }
        catch
        {
            // Best effort detection only.
        }

        return false;
    }

    private static bool ReadBool(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return false;
        return value.ValueKind == JsonValueKind.True;
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private void WriteAudit(string action, string result, Dictionary<string, object>? details = null)
    {
        try
        {
            _auditLogger.Append(new AuditEvent
            {
                Actor = "voice",
                Action = action,
                Result = result,
                Details = details
            });
        }
        catch
        {
            // Diagnostics must never block voice host supervision.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _ensureLock.WaitAsync();
        try
        {
            StopManagedProcessIfAny();
            _httpClient.Dispose();
        }
        finally
        {
            _ensureLock.Release();
            _ensureLock.Dispose();
        }
    }
}

public sealed record VoiceHostEnsureResult
{
    public required bool Success { get; init; }
    public string? BaseUrl { get; init; }
    public string? ErrorCode { get; init; }
    public string UserMessage { get; init; } = "";

    public static VoiceHostEnsureResult Ok(string baseUrl) => new()
    {
        Success = true,
        BaseUrl = baseUrl
    };

    public static VoiceHostEnsureResult Failure(string errorCode, string message) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        UserMessage = message
    };
}

public sealed record VoiceHostHealthResult(
    bool Reachable,
    bool Ready,
    string Status,
    bool AsrReady,
    bool TtsReady,
    string Version,
    string ErrorCode,
    string Message)
{
    public static VoiceHostHealthResult Unreachable(
        string errorCode = "",
        string message = "")
        => new(false, false, "", false, false, "", errorCode, message);
}
