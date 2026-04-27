using System.Reflection;
using Microsoft.Extensions.Hosting;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.RuntimeHost;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tools;

/// <summary>
/// Starts and manages the lifetime of the MCP tool client. The inner client
/// ships from <c>SirThaddeus.RuntimeHost</c> (<see cref="StdioMcpToolClient"/>)
/// and talks to <c>SirThaddeus.McpServer</c> over stdio.
///
/// This host exposes itself as <see cref="IMcpToolClient"/> so the assistant
/// can DI it directly. Before the underlying stdio client finishes its
/// handshake, calls are routed to a no-op fallback so the assistant can keep
/// operating in text-only mode while tools warm up (or if the MCP server
/// fails to launch at all).
///
/// The env passed to the child process is derived from the current
/// <see cref="SettingsDocument"/>. When settings that affect the env change
/// (e.g. allowed file roots), the host bounces the child so new values take
/// effect without a full runtime restart.
/// </summary>
public sealed class McpClientHost : IMcpToolClient, IHostedService, IAsyncDisposable
{
    private readonly ILogger<McpClientHost> _logger;
    private readonly IAuditLogger _audit;
    private readonly ISettingsStore _settings;
    private StdioMcpToolClient? _inner;
    private Task? _startupTask;
    private volatile bool _ready;
    private string? _envFingerprint;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly TimeSpan _startupTimeout = TimeSpan.FromSeconds(12);

    public McpClientHost(
        ILogger<McpClientHost> logger,
        IAuditLogger audit,
        ISettingsStore settings)
    {
        _logger = logger;
        _audit = audit;
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>True once the stdio handshake completed.</summary>
    public bool ToolsAvailable => _ready;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startupTask = Task.Run(() => StartInnerAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task StartInnerAsync(CancellationToken ct)
    {
        try
        {
            var doc = await _settings.GetAsync(ct).ConfigureAwait(false);
            await SpawnChildAsync(doc, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mcp.client.startup_failed");
        }
    }

    private async Task SpawnChildAsync(SettingsDocument doc, CancellationToken ct)
    {
        await _restartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var serverPath = RuntimePathResolver.ResolveMcpServerPath("auto", baseDir);
            if (!File.Exists(serverPath))
            {
                _logger.LogWarning("mcp.server.missing path={Path}", serverPath);
                return;
            }

            var env = BuildEnv(doc);
            _envFingerprint = FingerprintEnv(env);

            var asm = Assembly.GetExecutingAssembly();
            var version = asm.GetName().Version?.ToString(3) ?? "0.0.0";
            var client = new StdioMcpToolClient(serverPath, env, "Thaddeus.Runtime", version, _audit);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_startupTimeout);
            await client.StartAsync(timeoutCts.Token).ConfigureAwait(false);

            // Swap in the new client + dispose the old one.
            var old = _inner;
            _inner = client;
            _ready = true;
            if (old is not null)
            {
                try { await old.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "mcp.client.old_dispose_failed"); }
            }
            _logger.LogInformation("mcp.client.ready path={Path} allowedRoots={Count}",
                serverPath, doc.Files?.AllowedRoots?.Count ?? 0);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("mcp.client.startup_timeout after={Timeout}", _startupTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mcp.client.spawn_failed");
        }
        finally
        {
            _restartGate.Release();
        }
    }

    /// <summary>
    /// Called when the settings store persists a new document. If any env
    /// value the MCP server cares about changed, we transparently bounce
    /// the child so the next tool call runs against the new policy.
    /// </summary>
    private void OnSettingsChanged(SettingsDocument doc)
    {
        var newEnv = BuildEnv(doc);
        var newFp = FingerprintEnv(newEnv);
        if (string.Equals(newFp, _envFingerprint, StringComparison.Ordinal)) return;

        _logger.LogInformation("mcp.client.env_changed restarting=true");
        _ = Task.Run(() => SpawnChildAsync(doc, CancellationToken.None));
    }

    /// <summary>
    /// Builds the env dictionary passed to the MCP server child. Today we
    /// wire file-access settings (allowed roots + kill switch); memory,
    /// weather, and web-search env will follow when those settings land.
    /// </summary>
    private static Dictionary<string, string> BuildEnv(SettingsDocument doc)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = doc.Files;
        if (files is not null)
        {
            if (files.AllowedRoots.Count > 0)
            {
                env["ST_DOCUMENT_READER_ALLOWED_ROOTS"] =
                    string.Join(Path.PathSeparator, files.AllowedRoots);
            }
            env["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] =
                files.DisableAllFileAccess ? "true" : "false";
            env["ST_DOCUMENT_READER_MAX_DEFAULT_CHARS"] =
                files.MaxDefaultCharsPerRead.ToString();
        }
        return env;
    }

    private static string FingerprintEnv(IReadOnlyDictionary<string, string> env)
    {
        // Order-stable fingerprint so re-ordering entries doesn't trigger a
        // spurious restart.
        var parts = env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value}");
        return string.Join("\n", parts);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;
        await StopChildAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> StopChildAsync(CancellationToken cancellationToken = default)
    {
        await _restartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var inner = _inner;
            _inner = null;
            _ready = false;

            if (inner is null)
            {
                return false;
            }

            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "mcp.client.stop_child_failed");
                return false;
            }
        }
        finally
        {
            _restartGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        await StopChildAsync(CancellationToken.None).ConfigureAwait(false);
        _restartGate.Dispose();
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        if (!_ready && _startupTask is { IsCompleted: false })
        {
            await Task.WhenAny(_startupTask, Task.Delay(2000, cancellationToken)).ConfigureAwait(false);
        }
        if (!_ready || _inner is null) return Array.Empty<McpToolInfo>();
        return await _inner.ListToolsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        if (!_ready || _inner is null)
        {
            return $"(Tool '{toolName}' is unavailable: MCP server is not ready.)";
        }
        return await _inner.CallToolAsync(toolName, argumentsJson, cancellationToken).ConfigureAwait(false);
    }
}
