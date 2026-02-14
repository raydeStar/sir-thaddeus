using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.Config;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// Lightweight MCP stdio JSON-RPC client for harness live mode.
/// Intentionally separate from desktop runtime to avoid UI/WPF coupling.
/// </summary>
public sealed class HarnessMcpProcessClient : IAsyncDisposable
{
    private readonly string _serverPath;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private bool _initialized;
    private int _requestId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public HarnessMcpProcessClient(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _serverPath = ResolveServerPath(settings.Mcp.ServerPath);
        _environment = BuildEnvironment(settings);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = _serverPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var pair in _environment)
            startInfo.Environment[pair.Key] = pair.Value;

        _process = new Process { StartInfo = startInfo };
        _process.Start();
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;

        await SendRequestAsync<JsonElement>("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "HarnessClient", version = "1.0.0" }
        }, cancellationToken);

        await SendNotificationAsync("notifications/initialized", new { }, cancellationToken);
        _initialized = true;
    }

    public async Task<JsonElement> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await SendRequestAsync<JsonElement>("tools/list", new { }, cancellationToken);
    }

    public async Task<JsonElement> CallToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        object args;
        try
        {
            args = JsonSerializer.Deserialize<object>(argumentsJson, JsonOptions) ?? new { };
        }
        catch
        {
            args = new { };
        }

        return await SendRequestAsync<JsonElement>("tools/call", new
        {
            name = toolName,
            arguments = args
        }, cancellationToken);
    }

    private async Task<T> SendRequestAsync<T>(
        string method,
        object @params,
        CancellationToken cancellationToken)
    {
        await _rpcLock.WaitAsync(cancellationToken);
        try
        {
            var id = Interlocked.Increment(ref _requestId);
            var request = new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params
            };

            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            await _stdin!.WriteLineAsync(requestJson.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await _stdout!.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("id", out var idProp))
                    continue;
                if (idProp.GetInt32() != id)
                    continue;

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var msg)
                        ? msg.GetString()
                        : "Unknown MCP error";
                    throw new InvalidOperationException(message ?? "Unknown MCP error");
                }

                if (root.TryGetProperty("result", out var result))
                    return JsonSerializer.Deserialize<T>(result.GetRawText(), JsonOptions)
                           ?? throw new InvalidOperationException("Failed to deserialize MCP result payload.");

                throw new InvalidOperationException("MCP response missing result.");
            }
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    private async Task SendNotificationAsync(
        string method,
        object @params,
        CancellationToken cancellationToken)
    {
        await _rpcLock.WaitAsync(cancellationToken);
        try
        {
            var notification = new
            {
                jsonrpc = "2.0",
                method,
                @params
            };

            var payload = JsonSerializer.Serialize(notification, JsonOptions);
            await _stdin!.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironment(AppSettings settings)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ST_ACTIVE_PROFILE_ID"] = settings.ActiveProfileId ?? ""
        };

        if (settings.Memory.Enabled)
        {
            env["ST_MEMORY_DB_PATH"] = ResolveMemoryDbPath(settings.Memory.DbPath);
            env["ST_LLM_BASEURL"] = settings.Llm.BaseUrl;
        }

        return env;
    }

    private static string ResolveMemoryDbPath(string dbPath)
    {
        if (!string.Equals(dbPath, "auto", StringComparison.OrdinalIgnoreCase))
            return dbPath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "memory.db");
    }

    private static string ResolveServerPath(string configuredPath)
    {
        if (!string.Equals(configuredPath, "auto", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(configuredPath);

        var repoRoot = Directory.GetCurrentDirectory();
        var searchRoot = Path.Combine(repoRoot, "apps", "mcp-server", "SirThaddeus.McpServer", "bin");
        if (!Directory.Exists(searchRoot))
            throw new FileNotFoundException("MCP server build output directory not found.", searchRoot);

        var candidates = Directory
            .EnumerateFiles(searchRoot, "SirThaddeus.McpServer.exe", SearchOption.AllDirectories)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ToList();

        if (candidates.Count == 0)
            throw new FileNotFoundException(
                "Unable to locate SirThaddeus.McpServer.exe in build outputs. Build mcp-server first.");

        return candidates[0];
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("MCP process client is not initialized.");
    }

    public ValueTask DisposeAsync()
    {
        _initialized = false;

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2_000);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        _stdin?.Dispose();
        _stdout?.Dispose();
        _process?.Dispose();
        _rpcLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
