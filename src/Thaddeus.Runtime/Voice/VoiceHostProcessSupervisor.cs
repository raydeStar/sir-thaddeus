using System.Diagnostics;
using System.Globalization;
using System.Net;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Voice;

public sealed class VoiceHostProcessSupervisor : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(120);

    private readonly ILogger<VoiceHostProcessSupervisor> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private bool _disposed;

    public VoiceHostProcessSupervisor(ILogger<VoiceHostProcessSupervisor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new HttpClient { Timeout = ProbeTimeout };
    }

    public async Task<VoiceHostEnsureResult> EnsureResponsiveAsync(
        Uri voiceHostEndpoint,
        VoiceSettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (await ProbeAsync(voiceHostEndpoint, cancellationToken).ConfigureAwait(false))
            return VoiceHostEnsureResult.Ok();

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ProbeAsync(voiceHostEndpoint, cancellationToken).ConfigureAwait(false))
                return VoiceHostEnsureResult.Ok();

            if (_process is not null && !_process.HasExited)
            {
                return await WaitForHealthAsync(voiceHostEndpoint, _process, ResolveStartupTimeout(settings), cancellationToken).ConfigureAwait(false);
            }

            if (!TryBuildStartInfo(voiceHostEndpoint, settings, out var startInfo, out var error))
            {
                return VoiceHostEnsureResult.Failure("voice_host_start_failed", error);
            }

            Process process;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Process.Start returned null.");
            }
            catch (Exception ex)
            {
                return VoiceHostEnsureResult.Failure("voice_host_start_failed", ex.Message);
            }

            _process = process;
            AttachLogging(process);
            _logger.LogInformation("voicehost.started pid={Pid} command={Command}", process.Id, startInfo.FileName);

            return await WaitForHealthAsync(voiceHostEndpoint, process, ResolveStartupTimeout(settings), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _http.Dispose();
        _startLock.Dispose();

        StopProcess();
    }

    public bool StopHost()
    {
        return StopProcess();
    }

    private bool StopProcess()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return false;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "voicehost.dispose_failed pid={Pid}", SafePid(process));
        }
        finally
        {
            process.Dispose();
        }

        return false;
    }

    private async Task<VoiceHostEnsureResult> WaitForHealthAsync(
        Uri voiceHostEndpoint,
        Process process,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + startupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ProbeAsync(voiceHostEndpoint, cancellationToken).ConfigureAwait(false))
                return VoiceHostEnsureResult.Ok();

            if (process.HasExited)
            {
                return VoiceHostEnsureResult.Failure(
                    "voice_host_exited",
                    $"VoiceHost exited before /health responded (exit code {SafeExitCode(process)}).");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return VoiceHostEnsureResult.Failure(
            "voice_host_start_timeout",
            $"VoiceHost did not respond to /health within {(int)startupTimeout.TotalSeconds} seconds.");
    }

    private async Task<bool> ProbeAsync(Uri voiceHostEndpoint, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            using var response = await _http.GetAsync(BuildHealthUri(voiceHostEndpoint), timeoutCts.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private bool TryBuildStartInfo(
        Uri voiceHostEndpoint,
        VoiceSettings settings,
        out ProcessStartInfo startInfo,
        out string error)
    {
        startInfo = new ProcessStartInfo();
        error = string.Empty;

        if (!TryResolveLaunchTarget(out var target, out error))
            return false;

        var bindHost = string.Equals(voiceHostEndpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : voiceHostEndpoint.Host;

        startInfo = new ProcessStartInfo
        {
            FileName = target.Command,
            WorkingDirectory = target.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in target.PrefixArguments)
            startInfo.ArgumentList.Add(arg);

        startInfo.ArgumentList.Add("--bind");
        startInfo.ArgumentList.Add(bindHost);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(voiceHostEndpoint.Port.ToString(CultureInfo.InvariantCulture));
        AddOptionalArgument(startInfo, "--tts-engine", settings.TtsProvider);
        AddOptionalArgument(startInfo, "--tts-model-id", ResolveTtsModelArgument(settings));
        AddOptionalArgument(startInfo, "--tts-voice-id", settings.TtsVoiceId);
        AddOptionalArgument(startInfo, "--stt-engine", settings.SttProvider);
        AddOptionalArgument(startInfo, "--stt-model-id", settings.SttModelId);
        AddOptionalArgument(startInfo, "--stt-language", settings.SttLanguage);

        return true;
    }

    private static bool TryResolveLaunchTarget(out VoiceHostLaunchTarget target, out string error)
    {
        var baseDir = AppContext.BaseDirectory;
        var ext = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var publishedCandidates = new[]
        {
            Path.Combine(baseDir, "SirThaddeus.VoiceHost" + ext),
            Path.Combine(baseDir, "voice-host", "SirThaddeus.VoiceHost" + ext)
        };

        foreach (var candidate in publishedCandidates)
        {
            if (File.Exists(candidate))
            {
                target = new VoiceHostLaunchTarget(
                    candidate,
                    Array.Empty<string>(),
                    Path.GetDirectoryName(candidate) ?? baseDir);
                error = string.Empty;
                return true;
            }
        }

        var repoRoot = FindRepoRoot(baseDir);
        if (repoRoot is not null)
        {
            var projectPath = Path.Combine(
                repoRoot,
                "apps",
                "voice-host",
                "SirThaddeus.VoiceHost",
                "SirThaddeus.VoiceHost.csproj");
            if (File.Exists(projectPath))
            {
                target = new VoiceHostLaunchTarget(
                    "dotnet",
                    new[]
                    {
                        "run",
                        "--project",
                        projectPath,
                        "--configuration",
                        ResolveConfiguration(baseDir),
                        "--no-build",
                        "--"
                    },
                    repoRoot);
                error = string.Empty;
                return true;
            }
        }

        target = default!;
        error = "Could not locate SirThaddeus.VoiceHost.exe or the VoiceHost project file.";
        return false;
    }

    private void AttachLogging(Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
            _logger.LogInformation("voicehost.exited pid={Pid} exitCode={ExitCode}", SafePid(process), SafeExitCode(process));

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                _logger.LogInformation("[VoiceHost] {Data}", eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                _logger.LogWarning("[VoiceHost] {Data}", eventArgs.Data);
        };

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "voicehost.attach_logging_failed pid={Pid}", SafePid(process));
        }
    }

    private static Uri BuildHealthUri(Uri voiceHostEndpoint)
        => new UriBuilder(voiceHostEndpoint)
        {
            Path = "/health",
            Query = string.Empty
        }.Uri;

    private static void AddOptionalArgument(ProcessStartInfo startInfo, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value.Trim());
    }

    private static TimeSpan ResolveStartupTimeout(VoiceSettings settings)
    {
        var configuredMs = settings.VoiceHostStartupTimeoutMs;
        if (configuredMs <= 0)
            return DefaultStartupTimeout;

        var clampedMs = Math.Clamp(configuredMs, 30_000, 300_000);
        return TimeSpan.FromMilliseconds(clampedMs);
    }

    private static string? ResolveTtsModelArgument(VoiceSettings settings)
    {
        if (string.Equals(settings.TtsProvider, "piper", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.PiperVoicePath))
            return settings.PiperVoicePath;

        return settings.TtsModelId;
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SirThaddeus.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    private static string ResolveConfiguration(string baseDir)
        => baseDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, "Release", StringComparison.OrdinalIgnoreCase))
            ? "Release"
            : "Debug";

    private static int SafePid(Process process)
    {
        try { return process.Id; } catch { return 0; }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }
}

public sealed record VoiceHostEnsureResult(bool Success, string ErrorCode, string Message)
{
    public static VoiceHostEnsureResult Ok() => new(true, string.Empty, string.Empty);

    public static VoiceHostEnsureResult Failure(string errorCode, string message) => new(false, errorCode, message);
}

internal sealed record VoiceHostLaunchTarget(
    string Command,
    IReadOnlyList<string> PrefixArguments,
    string WorkingDirectory);
