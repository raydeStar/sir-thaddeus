using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;

namespace SirThaddeus.RuntimeHost;

public sealed class StdioMcpToolClient : IMcpToolClient, IAsyncDisposable
{
    private readonly string _serverPath;
    private readonly IReadOnlyList<string> _args;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string> _env;
    private readonly string _clientName;
    private readonly string _clientVersion;
    private readonly IAuditLogger _audit;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private const int MaxTransportRecoveryAttempts = 1;

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _requestId;
    private bool _initialized;

    public StdioMcpToolClient(
        string serverPath,
        IReadOnlyDictionary<string, string> env,
        string clientName,
        string clientVersion,
        IAuditLogger audit,
        IReadOnlyList<string>? args = null,
        string? workingDirectory = null)
    {
        _serverPath = serverPath ?? throw new ArgumentNullException(nameof(serverPath));
        _args = args ?? Array.Empty<string>();
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _clientName = clientName ?? throw new ArgumentNullException(nameof(clientName));
        _clientVersion = clientVersion ?? throw new ArgumentNullException(nameof(clientVersion));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _rpcLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized && IsTransportHealthy())
            {
                return;
            }

            await StartOrRestartLockedAsync("startup", cancellationToken);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var payload = await SendRequestWithRecoveryAsync<JsonElement>("tools/list", new { }, cancellationToken);
        var tools = new List<McpToolInfo>();
        if (!payload.TryGetProperty("tools", out var toolsArray) || toolsArray.ValueKind != JsonValueKind.Array)
            return tools;

        foreach (var entry in toolsArray.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "";
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var description = entry.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
            var inputSchema = entry.TryGetProperty("inputSchema", out var schema)
                ? JsonSerializer.Deserialize<object>(schema.GetRawText(), _json) ?? new { type = "object" }
                : new { type = "object" };

            tools.Add(new McpToolInfo
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema
            });
        }

        return tools;
    }

    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        object parsedArgs;
        try
        {
            parsedArgs = JsonSerializer.Deserialize<object>(argumentsJson, _json) ?? new { };
        }
        catch
        {
            parsedArgs = new { };
        }

        var payload = await SendRequestWithRecoveryAsync<JsonElement>("tools/call", new
        {
            name = toolName,
            arguments = parsedArgs
        }, cancellationToken);

        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return payload.GetRawText();

        var lines = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var text))
                lines.Add(text.GetString() ?? "");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<T> SendRequestWithRecoveryAsync<T>(string method, object @params, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendRequestAsync<T>(method, @params, cancellationToken);
            }
            catch (Exception ex) when (attempt < MaxTransportRecoveryAttempts && IsRecoverableTransportFailure(ex))
            {
                await RecoverTransportAsync(ex, cancellationToken);
            }
        }
    }

    private async Task<T> SendRequestAsync<T>(string method, object @params, CancellationToken cancellationToken)
    {
        await _rpcLock.WaitAsync(cancellationToken);
        try
        {
            EnsureTransportBound();
            return await SendRequestLockedAsync<T>(method, @params, cancellationToken);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("MCP client is not initialized.");
    }

    public async ValueTask DisposeAsync()
    {
        await _rpcLock.WaitAsync();
        try
        {
            DisposeTransportLocked(killProcess: true);
            _initialized = false;
        }
        finally
        {
            _rpcLock.Release();
            _rpcLock.Dispose();
        }
    }

    private async Task StartOrRestartLockedAsync(string reason, CancellationToken cancellationToken)
    {
        DisposeTransportLocked(killProcess: true);

        var executable = ResolveExecutable(_serverPath);
        var processArgs = ResolveArguments(_serverPath, _args);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in processArgs)
            startInfo.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(_workingDirectory))
            startInfo.WorkingDirectory = _workingDirectory;

        foreach (var pair in _env)
            startInfo.Environment[pair.Key] = pair.Value;

        _process = new Process { StartInfo = startInfo };
        _process.Start();
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _initialized = false;

        _audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "MCP_SERVER_STARTED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["path"] = _serverPath,
                ["args"] = _args,
                ["cwd"] = _workingDirectory ?? "",
                ["pid"] = _process.Id,
                ["reason"] = reason
            }
        });

        try
        {
            await SendRequestLockedAsync<JsonElement>("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = _clientName, version = _clientVersion }
            }, cancellationToken);

            await SendNotificationLockedAsync("notifications/initialized", new { }, cancellationToken);
            _initialized = true;
        }
        catch
        {
            DisposeTransportLocked(killProcess: true);
            _initialized = false;
            throw;
        }
    }

    private async Task<T> SendRequestLockedAsync<T>(string method, object @params, CancellationToken cancellationToken)
    {
        EnsureTransportBound();

        var id = Interlocked.Increment(ref _requestId);
        var req = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
        await _stdin!.WriteLineAsync(req.AsMemory(), cancellationToken);
        await _stdin.FlushAsync(cancellationToken);

        while (true)
        {
            var line = await _stdout!.ReadLineAsync(cancellationToken);
            if (line is null)
                throw new IOException("MCP stdout stream closed unexpectedly.");

            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            using (doc)
            {
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || idProp.GetInt32() != id)
                continue;

            if (root.TryGetProperty("error", out var error))
            {
                var msg = error.TryGetProperty("message", out var message) ? message.GetString() : "Unknown MCP error";
                throw new InvalidOperationException(msg ?? "Unknown MCP error");
            }

            if (!root.TryGetProperty("result", out var result))
                throw new InvalidOperationException("MCP response missing result.");

            return JsonSerializer.Deserialize<T>(result.GetRawText(), _json)
                ?? throw new InvalidOperationException("Failed to deserialize MCP response.");
            }
        }
    }

    private async Task SendNotificationLockedAsync(string method, object @params, CancellationToken cancellationToken)
    {
        EnsureTransportBound();
        var req = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params });
        await _stdin!.WriteLineAsync(req.AsMemory(), cancellationToken);
        await _stdin.FlushAsync(cancellationToken);
    }

    private async Task RecoverTransportAsync(Exception failure, CancellationToken cancellationToken)
    {
        await _rpcLock.WaitAsync(cancellationToken);
        try
        {
            var reason = failure.Message;
            _audit.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "MCP_SERVER_RECOVERY",
                Result = "retrying",
                Details = new Dictionary<string, object>
                {
                    ["reason"] = reason
                }
            });

            await StartOrRestartLockedAsync("transport_recovery", cancellationToken);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    private bool IsTransportHealthy()
    {
        return _process is { HasExited: false } && _stdin is not null && _stdout is not null;
    }

    private void EnsureTransportBound()
    {
        if (_process is null || _process.HasExited || _stdin is null || _stdout is null)
            throw new IOException("MCP transport is unavailable.");
    }

    private static bool IsRecoverableTransportFailure(Exception ex)
    {
        if (ex is OperationCanceledException)
            return false;

        if (ex is IOException or ObjectDisposedException)
            return true;

        var message = ex.Message ?? "";
        if (message.Length == 0)
            return false;

        return message.Contains("pipe is being closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("transport is unavailable", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("stream closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("MCP client is not initialized", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExecutable(string command)
    {
        if (!OperatingSystem.IsWindows())
            return command;

        if (string.Equals(command, "npm", StringComparison.OrdinalIgnoreCase))
            return "cmd.exe";

        if (string.Equals(command, "npx", StringComparison.OrdinalIgnoreCase))
            return "cmd.exe";

        return command;
    }

    private static IReadOnlyList<string> ResolveArguments(string command, IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows())
            return args;

        if (!string.Equals(command, "npm", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(command, "npx", StringComparison.OrdinalIgnoreCase))
        {
            return args;
        }

        var line = command + " " + string.Join(" ", args.Select(QuoteForCmd));
        return ["/d", "/s", "/c", line];
    }

    private static string QuoteForCmd(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";
        return arg.Any(char.IsWhiteSpace) || arg.Contains('"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;
    }

    private void DisposeTransportLocked(bool killProcess)
    {
        try
        {
            if (killProcess && _process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch
        {
            // Best effort.
        }

        _stdin?.Dispose();
        _stdout?.Dispose();
        _process?.Dispose();

        _stdin = null;
        _stdout = null;
        _process = null;
    }
}
