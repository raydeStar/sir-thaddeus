using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Spawns the MCP server as a child process and communicates via stdin/stdout
/// using the JSON-RPC 2.0 protocol defined by MCP.
///
/// Lifecycle:
///   1. StartAsync() launches the server process.
///   2. Initialize() exchanges capabilities.
///   3. ListToolsAsync() / CallToolAsync() proxy to the child process.
///   4. Dispose() kills the child process.
/// </summary>
public sealed class McpProcessClient : IMcpToolClient, IDisposable
{
    private readonly string _serverPath;
    private readonly IAuditLogger _audit;

    private Process? _serverProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private int _requestId;
    private bool _initialized;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public McpProcessClient(string serverPath, IAuditLogger audit)
    {
        _serverPath = serverPath ?? throw new ArgumentNullException(nameof(serverPath));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>
    /// Launches the MCP server child process and performs the initialize handshake.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_serverProcess != null)
            return;

        _serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _serverPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        _serverProcess.Start();
        _stdin = _serverProcess.StandardInput;
        _stdout = _serverProcess.StandardOutput;

        _audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "MCP_SERVER_STARTED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["pid"] = _serverProcess.Id,
                ["path"] = _serverPath
            }
        });

        // Send MCP initialize request
        var initResult = await SendRequestAsync<JsonElement>("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "DesktopRuntime", version = "0.1.0" }
        }, ct);

        // Send initialized notification (no response expected)
        await SendNotificationAsync("notifications/initialized", new { }, ct);

        _initialized = true;

        _audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "MCP_SERVER_INITIALIZED",
            Result = "ok"
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        EnsureInitialized();

        var result = await SendRequestAsync<JsonElement>("tools/list", new { }, ct);

        var tools = new List<McpToolInfo>();
        if (result.TryGetProperty("tools", out var toolsArray))
        {
            foreach (var tool in toolsArray.EnumerateArray())
            {
                tools.Add(new McpToolInfo
                {
                    Name = tool.GetProperty("name").GetString() ?? "",
                    Description = tool.GetProperty("description").GetString() ?? "",
                    InputSchema = tool.TryGetProperty("inputSchema", out var schema)
                        ? JsonSerializer.Deserialize<object>(schema.GetRawText(), JsonOpts) ?? new { }
                        : new { type = "object", properties = new { } }
                });
            }
        }

        return tools;
    }

    /// <inheritdoc />
    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        EnsureInitialized();

        object args;
        try
        {
            args = JsonSerializer.Deserialize<object>(argumentsJson, JsonOpts) ?? new { };
        }
        catch
        {
            args = new { };
        }

        var result = await SendRequestAsync<JsonElement>("tools/call", new
        {
            name = toolName,
            arguments = args
        }, ct);

        // Extract text content from the MCP tool result
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                    texts.Add(text.GetString() ?? "");
            }
            return string.Join("\n", texts);
        }

        return result.GetRawText();
    }

    // ─────────────────────────────────────────────────────────────────────
    // JSON-RPC Transport
    // ─────────────────────────────────────────────────────────────────────

    private async Task<T> SendRequestAsync<T>(string method, object @params, CancellationToken ct)
    {
        await _rpcLock.WaitAsync(ct);
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

            var json = JsonSerializer.Serialize(request, JsonOpts);
            await _stdin!.WriteLineAsync(json.AsMemory(), ct);
            await _stdin.FlushAsync(ct);

            // Read lines until we get a JSON-RPC response with our ID
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await _stdout!.ReadLineAsync(ct);

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Skip notifications (no "id" field)
                if (!root.TryGetProperty("id", out var idProp))
                    continue;

                if (idProp.GetInt32() != id)
                    continue;

                // Check for error
                if (root.TryGetProperty("error", out var error))
                {
                    var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                    throw new InvalidOperationException($"MCP error: {msg}");
                }

                if (root.TryGetProperty("result", out var result))
                {
                    return JsonSerializer.Deserialize<T>(result.GetRawText(), JsonOpts)!;
                }

                throw new InvalidOperationException("MCP response missing 'result' field.");
            }
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    private async Task SendNotificationAsync(string method, object @params, CancellationToken ct)
    {
        await _rpcLock.WaitAsync(ct);
        try
        {
            var notification = new
            {
                jsonrpc = "2.0",
                method,
                @params
            };

            var json = JsonSerializer.Serialize(notification, JsonOpts);
            await _stdin!.WriteLineAsync(json.AsMemory(), ct);
            await _stdin.FlushAsync(ct);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("MCP client is not initialized. Call StartAsync() first.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_serverProcess is { HasExited: false })
            {
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.WaitForExit(3000);
            }
        }
        catch { /* Best effort cleanup */ }

        _stdin?.Dispose();
        _stdout?.Dispose();
        _serverProcess?.Dispose();
        _rpcLock.Dispose();
    }
}
