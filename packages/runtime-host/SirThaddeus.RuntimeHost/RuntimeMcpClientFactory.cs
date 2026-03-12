using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;

namespace SirThaddeus.RuntimeHost;

public sealed record RuntimeMcpClientHandle(
    IMcpToolClient Client,
    IAsyncDisposable Scope,
    bool ToolsAvailable,
    string Message);

public static class RuntimeMcpClientFactory
{
    private static readonly TimeSpan DefaultDegradedStartupTimeout = TimeSpan.FromSeconds(10);

    public static async Task<RuntimeMcpClientHandle> CreateAsync(
        bool enableTools,
        bool allowDegradedStartup,
        string? overrideServerPath,
        AppSettings settings,
        IAuditLogger audit,
        string baseDirectory,
        string clientName,
        string clientVersion,
        CancellationToken cancellationToken = default,
        TimeSpan? degradedStartupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);

        if (!enableTools)
        {
            return new RuntimeMcpClientHandle(
                new NoToolsMcpClient(),
                AsyncNoopDisposable.Instance,
                ToolsAvailable: false,
                Message: "MCP tools are disabled.");
        }

        var serverPath = string.IsNullOrWhiteSpace(overrideServerPath)
            ? RuntimePathResolver.ResolveMcpServerPath(settings.Mcp.ServerPath, baseDirectory)
            : Path.GetFullPath(overrideServerPath.Trim());
        var env = RuntimeMcpEnvironmentBuilder.Build(settings);
        var client = new StdioMcpToolClient(serverPath, env, clientName, clientVersion, audit);

        CancellationTokenSource? startupTimeoutCts = null;
        var startupToken = cancellationToken;

        if (allowDegradedStartup)
        {
            startupTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeoutCts.CancelAfter(degradedStartupTimeout ?? DefaultDegradedStartupTimeout);
            startupToken = startupTimeoutCts.Token;
        }

        try
        {
            await client.StartAsync(startupToken);
            return new RuntimeMcpClientHandle(
                client,
                client,
                ToolsAvailable: true,
                Message: "MCP tools are ready.");
        }
        catch (OperationCanceledException) when (
            allowDegradedStartup &&
            startupTimeoutCts is not null &&
            startupTimeoutCts.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            await DisposeClientSilentlyAsync(client);

            var timeout = degradedStartupTimeout ?? DefaultDegradedStartupTimeout;
            var message = $"MCP startup timed out after {timeout.TotalSeconds:0.#}s; continuing without tools.";
            audit.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "MCP_STARTUP_DEGRADED",
                Target = serverPath,
                Result = "warning",
                Details = new Dictionary<string, object>
                {
                    ["reason"] = "timeout",
                    ["timeout_ms"] = (int)timeout.TotalMilliseconds
                }
            });

            return new RuntimeMcpClientHandle(
                new NoToolsMcpClient(),
                AsyncNoopDisposable.Instance,
                ToolsAvailable: false,
                Message: message);
        }
        catch (Exception ex) when (allowDegradedStartup)
        {
            await DisposeClientSilentlyAsync(client);

            var message = $"MCP startup failed ({ex.Message}); continuing without tools.";
            audit.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "MCP_STARTUP_DEGRADED",
                Target = serverPath,
                Result = "warning",
                Details = new Dictionary<string, object>
                {
                    ["reason"] = ex.Message
                }
            });

            return new RuntimeMcpClientHandle(
                new NoToolsMcpClient(),
                AsyncNoopDisposable.Instance,
                ToolsAvailable: false,
                Message: message);
        }
        finally
        {
            startupTimeoutCts?.Dispose();
        }
    }

    private static async Task DisposeClientSilentlyAsync(IAsyncDisposable client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}

file sealed class NoToolsMcpClient : IMcpToolClient
{
    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);

    public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
        => Task.FromResult($"Error: Tool '{toolName}' is unavailable because MCP tools are not ready.");
}

file sealed class AsyncNoopDisposable : IAsyncDisposable
{
    public static readonly AsyncNoopDisposable Instance = new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
