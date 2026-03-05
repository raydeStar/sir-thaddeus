using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;

namespace SirThaddeus.RuntimeHost;

public sealed class StdioMcpToolClient : IMcpToolClient, IAsyncDisposable
{
    private readonly string _serverPath;
    private readonly IReadOnlyDictionary<string, string> _env;
    private readonly string _clientName;
    private readonly string _clientVersion;
    private readonly IAuditLogger _audit;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

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
        IAuditLogger audit)
    {
        _serverPath = serverPath ?? throw new ArgumentNullException(nameof(serverPath));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _clientName = clientName ?? throw new ArgumentNullException(nameof(clientName));
        _clientVersion = clientVersion ?? throw new ArgumentNullException(nameof(clientVersion));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
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

        foreach (var pair in _env)
            startInfo.Environment[pair.Key] = pair.Value;

        _process = new Process { StartInfo = startInfo };
        _process.Start();
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;

        _audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "MCP_SERVER_STARTED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["path"] = _serverPath,
                ["pid"] = _process.Id
            }
        });

        await SendRequestAsync<JsonElement>("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = _clientName, version = _clientVersion }
        }, cancellationToken);
        await SendNotificationAsync("notifications/initialized", new { }, cancellationToken);

        _initialized = true;
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var payload = await SendRequestAsync<JsonElement>("tools/list", new { }, cancellationToken);
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

        var payload = await SendRequestAsync<JsonElement>("tools/call", new
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

    private async Task<T> SendRequestAsync<T>(string method, object @params, CancellationToken cancellationToken)
    {
        await _rpcLock.WaitAsync(cancellationToken);
        try
        {
            var id = Interlocked.Increment(ref _requestId);
            var req = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
            await _stdin!.WriteLineAsync(req.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);

            while (true)
            {
                var line = await _stdout!.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
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
        finally
        {
            _rpcLock.Release();
        }
    }

    private async Task SendNotificationAsync(string method, object @params, CancellationToken cancellationToken)
    {
        await _rpcLock.WaitAsync(cancellationToken);
        try
        {
            var req = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params });
            await _stdin!.WriteLineAsync(req.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
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

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
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
        _rpcLock.Dispose();

        return ValueTask.CompletedTask;
    }
}
