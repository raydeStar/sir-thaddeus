using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using SirThaddeus.Config;

namespace SirThaddeus.UI.Avalonia;

internal enum VoiceHostLaunchStatus
{
    Started,
    AlreadyRunning,
    NotSupported,
    NotLocalAddress,
    InvalidBaseUrl,
    NotFound,
    FailedToStart,
    FailedHealthCheck
}

internal sealed record VoiceHostLaunchResult(
    VoiceHostLaunchStatus Status,
    string Message,
    string? BaseUrl = null);

internal sealed class VoiceHostLauncher : IDisposable
{
    private Process? _managedProcess;
    private Uri? _managedBaseUri;

    public bool IsManagedVoiceHostRunning => _managedProcess is { HasExited: false };

    public async Task<VoiceHostLaunchResult> EnsureRunningAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!OperatingSystem.IsWindows())
        {
            return new VoiceHostLaunchResult(
                VoiceHostLaunchStatus.NotSupported,
                "VoiceHost auto-start is currently implemented for Windows only.");
        }

        if (!Uri.TryCreate(settings.GetVoiceHostBaseUrl(), UriKind.Absolute, out var baseUri))
        {
            return new VoiceHostLaunchResult(
                VoiceHostLaunchStatus.InvalidBaseUrl,
                "VoiceHost base URL is invalid.");
        }

        if (!IsLoopback(baseUri))
        {
            return new VoiceHostLaunchResult(
                VoiceHostLaunchStatus.NotLocalAddress,
                "VoiceHost auto-start only supports localhost addresses.");
        }

        if (await ProbeReadyAsync(baseUri, cancellationToken))
        {
            return new VoiceHostLaunchResult(
                VoiceHostLaunchStatus.AlreadyRunning,
                "VoiceHost is already running.",
                baseUri.ToString().TrimEnd('/'));
        }

        if (IsManagedVoiceHostRunning && _managedBaseUri is not null && Uri.Compare(_managedBaseUri, baseUri, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
        {
            return new VoiceHostLaunchResult(
                VoiceHostLaunchStatus.AlreadyRunning,
                "Managed VoiceHost is already starting or running.",
                baseUri.ToString().TrimEnd('/'));
        }

        var launchConfig = ResolveLaunchConfig();
        if (launchConfig is null)
        {
            return new VoiceHostLaunchResult(
                VoiceHostLaunchStatus.NotFound,
                "Could not find apps/voice-backend/start-voice-backend.ps1.");
        }

        StopManagedVoiceHost();

        var startInfo = new ProcessStartInfo
        {
            FileName = launchConfig.StartFileName,
            WorkingDirectory = launchConfig.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        NormalizeWindowsEnvironmentKeys(startInfo);

        foreach (var argument in BuildVoiceHostArguments(settings, baseUri.Port))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            _managedProcess = Process.Start(startInfo);
            _managedBaseUri = baseUri;
            if (_managedProcess is null)
            {
                return new VoiceHostLaunchResult(
                    VoiceHostLaunchStatus.FailedToStart,
                    "Failed to start VoiceHost process.");
            }

            try
            {
                _managedProcess.BeginOutputReadLine();
                _managedProcess.BeginErrorReadLine();
            }
            catch
            {
                // Stream readers are best effort only.
            }
        }
        catch (Exception ex)
        {
            StopManagedVoiceHost();
            return new VoiceHostLaunchResult(VoiceHostLaunchStatus.FailedToStart, ex.Message);
        }

        var timeoutMs = Math.Clamp(settings.VoiceHostStartupTimeoutMs, 2_000, 180_000);
        var healthy = await WaitForReadyAsync(baseUri, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
        if (healthy)
        {
            return new VoiceHostLaunchResult(
                VoiceHostLaunchStatus.Started,
                $"VoiceHost started at {baseUri}",
                baseUri.ToString().TrimEnd('/'));
        }

        StopManagedVoiceHost();
        return new VoiceHostLaunchResult(
            VoiceHostLaunchStatus.FailedHealthCheck,
            $"VoiceHost did not become ready within {timeoutMs}ms.");
    }

    public void StopManagedVoiceHost()
    {
        if (_managedProcess is null)
        {
            _managedBaseUri = null;
            return;
        }

        try
        {
            if (!_managedProcess.HasExited)
            {
                _managedProcess.Kill(entireProcessTree: true);
                _managedProcess.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort shutdown only.
        }
        finally
        {
            _managedProcess.Dispose();
            _managedProcess = null;
            _managedBaseUri = null;
        }
    }

    public void Dispose()
    {
        StopManagedVoiceHost();
    }

    private static IEnumerable<string> BuildVoiceHostArguments(VoiceSettings settings, int port)
    {
        yield return "--port";
        yield return port.ToString();
        yield return "--bind";
        yield return "127.0.0.1";
        yield return "--mode";
        yield return "proxy-first";
        
        if (!string.IsNullOrWhiteSpace(settings.AsrEndpoint))
        {
            yield return "--asr-upstream";
            yield return settings.AsrEndpoint.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.TtsEndpoint))
        {
            yield return "--tts-upstream";
            yield return settings.TtsEndpoint.Trim();
        }

        yield return "--tts-engine";
        yield return settings.GetNormalizedTtsEngine();
        
        yield return "--stt-engine";
        yield return "faster-whisper"; // Interactive voice always uses faster-whisper

        var sttModelId = settings.GetResolvedSttModelId();
        if (!string.IsNullOrWhiteSpace(sttModelId))
        {
            yield return "--stt-model-id";
            yield return sttModelId;
        }

        var sttLanguage = settings.GetResolvedSttLanguage();
        if (!string.IsNullOrWhiteSpace(sttLanguage))
        {
            yield return "--stt-language";
            yield return sttLanguage;
        }

        var ttsModelId = settings.GetResolvedTtsModelId();
        if (!string.IsNullOrWhiteSpace(ttsModelId))
        {
            yield return "--tts-model-id";
            yield return ttsModelId;
        }

        var ttsVoiceId = settings.GetResolvedTtsVoiceId();
        if (!string.IsNullOrWhiteSpace(ttsVoiceId))
        {
            yield return "--tts-voice-id";
            yield return ttsVoiceId;
        }
    }

    private static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        var host = uri.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitForReadyAsync(Uri baseUri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            if (await ProbeReadyAsync(baseUri, timeoutCts.Token))
            {
                return true;
            }

            try
            {
                await Task.Delay(500, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return false;
    }

    private static async Task<bool> ProbeReadyAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await http.GetAsync("/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                var statusText = status.GetString() ?? string.Empty;
                return statusText.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
                       statusText.Equals("ready", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Probe failures just mean the server is not ready yet.
        }

        return false;
    }

    private static VoiceHostLaunchConfig? ResolveLaunchConfig()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var localExe = Path.Combine(AppContext.BaseDirectory, "SirThaddeus.VoiceHost.exe");
        if (File.Exists(localExe))
        {
            return new VoiceHostLaunchConfig(localExe, Path.GetDirectoryName(localExe)!);
        }

        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            return null;
        }

        var devExe = Path.Combine(repoRoot, "apps", "voice-host", "SirThaddeus.VoiceHost", "bin", "Debug", "net10.0", "SirThaddeus.VoiceHost.exe");
        if (File.Exists(devExe))
        {
            return new VoiceHostLaunchConfig(devExe, Path.GetDirectoryName(devExe)!);
        }

        var devWinX64Exe = Path.Combine(repoRoot, "apps", "voice-host", "SirThaddeus.VoiceHost", "bin", "Debug", "net10.0", "win-x64", "SirThaddeus.VoiceHost.exe");
        if (File.Exists(devWinX64Exe))
        {
            return new VoiceHostLaunchConfig(devWinX64Exe, Path.GetDirectoryName(devWinX64Exe)!);
        }

        return null;
    }

    private static void NormalizeWindowsEnvironmentKeys(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var snapshot = new List<KeyValuePair<string, string?>>(startInfo.Environment);
        var merged = new Dictionary<string, (string Key, string? Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot)
        {
            if (!merged.TryGetValue(entry.Key, out var existing))
            {
                merged[entry.Key] = (entry.Key, entry.Value);
                continue;
            }

            var preferredKey = ChoosePreferredEnvironmentKey(existing.Key, entry.Key);
            var preferredValue = string.Equals(preferredKey, entry.Key, StringComparison.Ordinal)
                ? entry.Value ?? existing.Value
                : existing.Value ?? entry.Value;
            merged[entry.Key] = (preferredKey, preferredValue);
        }

        startInfo.Environment.Clear();
        foreach (var entry in merged.Values)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }
    }

    private static string ChoosePreferredEnvironmentKey(string existingKey, string candidateKey)
    {
        if (string.Equals(existingKey, "Path", StringComparison.Ordinal))
        {
            return existingKey;
        }

        if (string.Equals(candidateKey, "Path", StringComparison.Ordinal))
        {
            return candidateKey;
        }

        return existingKey;
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

    private sealed record VoiceHostLaunchConfig(string StartFileName, string WorkingDirectory);
}


