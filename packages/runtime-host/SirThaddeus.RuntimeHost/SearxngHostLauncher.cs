using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using SirThaddeus.Config;

namespace SirThaddeus.RuntimeHost;

public enum SearxngLaunchStatus
{
    NotRequired,
    Disabled,
    AlreadyRunning,
    Started,
    InvalidBaseUrl,
    NotLocalAddress,
    NotFound,
    FailedToStart,
    FailedHealthCheck
}

public sealed record SearxngLaunchResult(
    SearxngLaunchStatus Status,
    string Message,
    string? BaseUrl = null);

public sealed class SearxngHostLauncher : IDisposable
{
    private Process? _managedProcess;
    private Uri? _managedBaseUri;

    public bool IsManagedSearxngRunning => _managedProcess is { HasExited: false };

    public async Task<SearxngLaunchResult> EnsureRunningAsync(
        WebSearchSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var mode = NormalizeMode(settings.Mode);
        if (mode is not ("auto" or "searxng"))
        {
            StopManagedSearxng();
            return new SearxngLaunchResult(
                SearxngLaunchStatus.NotRequired,
                $"webSearch.mode '{mode}' does not require SearxNG auto-start.");
        }

        if (!settings.SearxngAutoStart)
        {
            StopManagedSearxng();
            return new SearxngLaunchResult(
                SearxngLaunchStatus.Disabled,
                "SearxNG auto-start is disabled in settings.");
        }

        var rawBaseUrl = string.IsNullOrWhiteSpace(settings.SearxngBaseUrl)
            ? "http://localhost:8080"
            : settings.SearxngBaseUrl.Trim();

        if (!Uri.TryCreate(rawBaseUrl, UriKind.Absolute, out var baseUri))
        {
            StopManagedSearxng();
            return new SearxngLaunchResult(
                SearxngLaunchStatus.InvalidBaseUrl,
                $"Invalid SearxNG base URL: {rawBaseUrl}");
        }

        if (!IsLoopback(baseUri))
        {
            StopManagedSearxng();
            return new SearxngLaunchResult(
                SearxngLaunchStatus.NotLocalAddress,
                "Managed SearxNG auto-start only supports localhost addresses.",
                baseUri.ToString().TrimEnd('/'));
        }

        if (await ProbeReadyAsync(baseUri, cancellationToken))
        {
            return new SearxngLaunchResult(
                SearxngLaunchStatus.AlreadyRunning,
                "SearxNG is already running.",
                baseUri.ToString().TrimEnd('/'));
        }

        if (IsManagedSearxngRunning &&
            _managedBaseUri is not null &&
            Uri.Compare(
                _managedBaseUri,
                baseUri,
                UriComponents.AbsoluteUri,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            return new SearxngLaunchResult(
                SearxngLaunchStatus.AlreadyRunning,
                "Managed SearxNG is already starting or running.",
                baseUri.ToString().TrimEnd('/'));
        }

        var candidates = BuildLaunchCandidates(settings, baseUri);
        if (candidates.Count == 0)
        {
            return new SearxngLaunchResult(
                SearxngLaunchStatus.NotFound,
                "No SearxNG launch candidates were found.",
                baseUri.ToString().TrimEnd('/'));
        }

        StopManagedSearxng();

        var timeoutMs = Math.Clamp(settings.SearxngStartupTimeoutMs, 2_000, 180_000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        var failures = new List<string>();
        var attemptedLaunch = false;

        foreach (var candidate in candidates)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            if (!TryStartCandidate(candidate, out var process, out var failureMessage, out var missingBinary))
            {
                var marker = missingBinary ? "missing executable" : "failed";
                failures.Add($"{candidate.DisplayName}: {marker} ({failureMessage})");
                continue;
            }

            attemptedLaunch = true;
            _managedProcess = process;
            _managedBaseUri = baseUri;

            var healthy = await WaitForReadyAsync(baseUri, remaining, cancellationToken);
            if (healthy)
            {
                return new SearxngLaunchResult(
                    SearxngLaunchStatus.Started,
                    $"Managed SearxNG started via {candidate.DisplayName}.",
                    baseUri.ToString().TrimEnd('/'));
            }

            var exitState = process.HasExited ? $"exit code {process.ExitCode}" : "process still running";
            failures.Add($"{candidate.DisplayName}: health check failed ({exitState})");
            StopManagedSearxng();
        }

        var detail = failures.Count == 0
            ? "Launch timeout reached."
            : string.Join("; ", failures.Take(3));

        if (attemptedLaunch)
        {
            return new SearxngLaunchResult(
                SearxngLaunchStatus.FailedHealthCheck,
                $"SearxNG did not become healthy within {timeoutMs}ms. {detail}",
                baseUri.ToString().TrimEnd('/'));
        }

        var hasNonMissingStartFailure = failures.Any(f =>
            !f.Contains("missing executable", StringComparison.OrdinalIgnoreCase));

        return new SearxngLaunchResult(
            hasNonMissingStartFailure ? SearxngLaunchStatus.FailedToStart : SearxngLaunchStatus.NotFound,
            $"No working SearxNG launcher found. {detail}",
            baseUri.ToString().TrimEnd('/'));
    }

    public void StopManagedSearxng()
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
        StopManagedSearxng();
    }

    private static bool TryStartCandidate(
        LaunchCandidate candidate,
        out Process process,
        out string failureMessage,
        out bool missingBinary)
    {
        process = null!;
        failureMessage = "";
        missingBinary = false;

        var startInfo = new ProcessStartInfo
        {
            FileName = candidate.FileName,
            WorkingDirectory = candidate.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in candidate.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            var started = Process.Start(startInfo);
            if (started is null)
            {
                failureMessage = "Process.Start returned null.";
                return false;
            }

            process = started;
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                // Reader hooks are best effort only.
            }

            return true;
        }
        catch (Exception ex)
        {
            missingBinary = LooksLikeMissingBinary(ex);
            failureMessage = ex.Message;
            return false;
        }
    }

    private static bool LooksLikeMissingBinary(Exception ex)
    {
        if (ex is Win32Exception win32 && win32.NativeErrorCode is 2 or 3)
            return true;

        var message = ex.Message ?? "";
        return message.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("file not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback)
            return true;

        var host = uri.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitForReadyAsync(Uri baseUri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            if (await ProbeReadyAsync(baseUri, timeoutCts.Token))
                return true;

            try
            {
                await Task.Delay(450, timeoutCts.Token);
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
        using var http = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(2)
        };

        try
        {
            using var searchResponse = await http.GetAsync("/search?q=thaddeus&format=json", cancellationToken);
            if (searchResponse.IsSuccessStatusCode)
            {
                return true;
            }
        }
        catch
        {
            // Probe failures mean the server is not ready.
        }

        try
        {
            using var rootResponse = await http.GetAsync("/", cancellationToken);
            return rootResponse.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeMode(string mode)
    {
        var normalized = (mode ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" => "auto",
            "searxng" => "searxng",
            "search_api" => "search_api",
            "api" => "search_api",
            "ddg_html" => "ddg_html",
            "google_news" => "google_news",
            "manual" => "manual",
            _ => "auto"
        };
    }

    private static List<LaunchCandidate> BuildLaunchCandidates(WebSearchSettings settings, Uri baseUri)
    {
        var command = (settings.SearxngLaunchCommand ?? "auto").Trim();
        var rawArgs = (settings.SearxngLaunchArguments ?? "auto").Trim();

        if (!string.Equals(command, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (command.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                var launchArguments = BuildPowerShellArguments(command, rawArgs, baseUri);
                var launchWorkingDirectory = ResolveWorkingDirectory(command);
                return [new LaunchCandidate("powershell", launchArguments, launchWorkingDirectory, command)];
            }

            var directLaunchArguments = BuildArgumentsForCommand(command, rawArgs, baseUri);
            var directLaunchWorkingDirectory = ResolveWorkingDirectory(command);
            return [new LaunchCandidate(command, directLaunchArguments, directLaunchWorkingDirectory, command)];
        }

        var candidates = new List<LaunchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(
            string fileName,
            IReadOnlyList<string> args,
            string displayName,
            string? workingDirectoryOverride = null)
        {
            var key = BuildCandidateKey(fileName, args);
            if (!seen.Add(key))
                return;

            var workingDirectory = string.IsNullOrWhiteSpace(workingDirectoryOverride)
                ? ResolveWorkingDirectory(fileName)
                : workingDirectoryOverride;
            candidates.Add(new LaunchCandidate(fileName, args, workingDirectory, displayName));
        }

        foreach (var bundledPath in EnumerateBundledExecutableCandidates())
        {
            if (!File.Exists(bundledPath))
                continue;

            Add(bundledPath, BuildDefaultArgs("searxng", baseUri), bundledPath);
            Add(bundledPath, Array.Empty<string>(), $"{bundledPath} (no args)");
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var bundledScript in EnumerateBundledScriptCandidates())
            {
                if (!File.Exists(bundledScript))
                    continue;

                Add("powershell", BuildPowerShellArguments(bundledScript, "auto", baseUri), bundledScript);
            }
        }

        Add("searxng", BuildDefaultArgs("searxng", baseUri), "searxng");
        if (OperatingSystem.IsWindows())
            Add("searxng.exe", BuildDefaultArgs("searxng", baseUri), "searxng.exe");

        Add("python", BuildDefaultArgs("python", baseUri), "python -m searx.webapp");
        if (!OperatingSystem.IsWindows())
            Add("python3", BuildDefaultArgs("python", baseUri), "python3 -m searx.webapp");

        return candidates;
    }

    private static IEnumerable<string> EnumerateBundledExecutableCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateFileNames = new[]
        {
            "SirThaddeus.Searxng.exe",
            "SirThaddeus.Searxng",
            "searxng.exe",
            "searxng"
        };

        foreach (var root in EnumerateBundledCandidateRoots())
        {
            foreach (var fileName in candidateFileNames)
            {
                var candidate = Path.Combine(root, fileName);
                if (seen.Add(candidate))
                    yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateBundledScriptCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateFileNames = new[]
        {
            "start-searxng.ps1"
        };

        foreach (var root in EnumerateBundledCandidateRoots())
        {
            if (!HasUsableBundledScriptPayload(root))
                continue;

            foreach (var fileName in candidateFileNames)
            {
                var candidate = Path.Combine(root, fileName);
                if (seen.Add(candidate))
                    yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateBundledCandidateRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "search");
        yield return Path.Combine(AppContext.BaseDirectory, "bin", "search");
        yield return Path.Combine(AppContext.BaseDirectory, "bin", "searxng");

        var repoRoot = FindRepoRoot();
        if (string.IsNullOrWhiteSpace(repoRoot))
            yield break;

        yield return Path.Combine(repoRoot, "apps", "searxng", "package");
        yield return Path.Combine(repoRoot, "artifacts", "searxng", "win-x64", "package");
        yield return Path.Combine(repoRoot, "artifacts", "stage", "win-x64", "search");
        yield return Path.Combine(repoRoot, "apps", "searxng", "dist");
        yield return Path.Combine(repoRoot, "apps", "searxng");
    }

    private static bool HasUsableBundledScriptPayload(string root)
    {
        if (!File.Exists(Path.Combine(root, "start-searxng.ps1")))
            return false;

        if (!Directory.Exists(Path.Combine(root, "deps", "site-packages")))
            return false;

        if (!File.Exists(Path.Combine(root, "source", "searxng-upstream", "searx", "webapp.py")))
            return false;

        if (!File.Exists(Path.Combine(root, "settings.template.yml")))
            return false;

        return EnumerateBundledPythonCandidates(root).Any(File.Exists);
    }

    private static IEnumerable<string> EnumerateBundledPythonCandidates(string root)
    {
        yield return Path.Combine(root, "runtime", "python", "python.exe");
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
                    return current.FullName;

                current = current.Parent;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildArgumentsForCommand(string command, string rawArgs, Uri baseUri)
    {
        if (string.Equals(rawArgs, "auto", StringComparison.OrdinalIgnoreCase))
            return BuildDefaultArgs(command, baseUri);

        var expanded = ExpandLaunchTokens(rawArgs, baseUri);
        return SplitArguments(expanded);
    }

    private static IReadOnlyList<string> BuildDefaultArgs(string command, Uri baseUri)
    {
        var host = baseUri.Host;
        var port = baseUri.Port.ToString();
        if (command.Contains("python", StringComparison.OrdinalIgnoreCase))
        {
            return ["-m", "searx.webapp", "--host", host, "--port", port];
        }

        return ["--host", host, "--port", port];
    }

    private static IReadOnlyList<string> BuildPowerShellArguments(string scriptPath, string rawArgs, Uri baseUri)
    {
        var host = baseUri.Host;
        var port = baseUri.Port.ToString();
        var expandedTail = string.Equals(rawArgs, "auto", StringComparison.OrdinalIgnoreCase)
            ? new List<string> { "-BindHost", host, "-Port", port }
            : SplitArguments(ExpandLaunchTokens(rawArgs, baseUri));

        var args = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath
        };
        args.AddRange(expandedTail);
        return args;
    }

    private static string ExpandLaunchTokens(string value, Uri baseUri)
    {
        return value
            .Replace("{host}", baseUri.Host, StringComparison.OrdinalIgnoreCase)
            .Replace("{port}", baseUri.Port.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{baseUrl}", baseUri.ToString().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitArguments(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return result;

        var current = new StringBuilder();
        var inQuotes = false;
        char quoteChar = '\0';

        foreach (var ch in input)
        {
            if (inQuotes)
            {
                if (ch == quoteChar)
                {
                    inQuotes = false;
                    quoteChar = '\0';
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                inQuotes = true;
                quoteChar = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static string BuildCandidateKey(string fileName, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return fileName;
        return fileName + "|" + string.Join('\u001F', args);
    }

    private static string ResolveWorkingDirectory(string command)
    {
        if (!string.IsNullOrWhiteSpace(command) && Path.IsPathRooted(command))
        {
            var dir = Path.GetDirectoryName(command);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }

        return Directory.GetCurrentDirectory();
    }

    private sealed record LaunchCandidate(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        string DisplayName);
}
