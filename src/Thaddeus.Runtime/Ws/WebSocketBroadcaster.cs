using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Thaddeus.Runtime.Events;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Ws;

/// <summary>
/// Tracks open WebSocket clients and forwards every <see cref="IEventBus"/> event to
/// all of them. New connections receive the current state snapshot immediately so
/// late-joining UIs converge without a polling round-trip.
/// </summary>
public sealed class WebSocketBroadcaster : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IEventBus _bus;
    private readonly StateSnapshot _snapshot;
    private readonly ILogger<WebSocketBroadcaster> _logger;
    private readonly ConcurrentDictionary<Guid, ClientChannel> _clients = new();
    private IDisposable? _subscription;

    /// <summary>Wires the broadcaster to the bus and snapshot.</summary>
    public WebSocketBroadcaster(IEventBus bus, StateSnapshot snapshot, ILogger<WebSocketBroadcaster> logger)
    {
        _bus = bus;
        _snapshot = snapshot;
        _logger = logger;
    }

    /// <summary>Number of currently connected WebSocket clients (for diagnostics).</summary>
    public int ClientCount => _clients.Count;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(BroadcastAsync);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        await CloseAllAsync(WebSocketCloseStatus.EndpointUnavailable, "shutdown", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles an incoming WebSocket connection. The caller is expected to have already
    /// validated bearer auth and the Origin header.
    /// </summary>
    public async Task HandleConnectionAsync(HttpContext context, WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = new ClientChannel(socket);
        _clients[id] = channel;
        _logger.LogInformation("ws.connected id={Id} remote={Remote} count={Count}",
            id, context.Connection.RemoteIpAddress, _clients.Count);

        try
        {
            // Hydrate the new client with the current state. UI joining mid-session
            // would otherwise have to poll.
            var hydrate = new RuntimeEvent<RuntimeStateEvent>
            {
                Type = "runtime.state",
                Id = NUlid.Ulid.NewUlid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Payload = _snapshot.Get(),
            };
            await SendAsync(socket, JsonSerializer.Serialize(hydrate, JsonOptions), ct).ConfigureAwait(false);

            // Drain inbound frames; the runtime currently never receives WS messages
            // from clients (all client-to-runtime traffic is HTTP). Reading still lets
            // us notice closure promptly.
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client closed", ct).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "ws.disconnect id={Id}", id);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _logger.LogInformation("ws.disconnected id={Id} count={Count}", id, _clients.Count);
        }
    }

    private async Task BroadcastAsync(RuntimeEvent<object?> evt, CancellationToken ct)
    {
        if (_clients.IsEmpty) return;
        var json = JsonSerializer.Serialize(evt, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var (id, channel) in _clients)
        {
            try
            {
                await channel.SendAsync(bytes, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ws.send_failed id={Id}", id);
                _clients.TryRemove(id, out _);
            }
        }
    }

    private static async Task SendAsync(WebSocket socket, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task CloseAllAsync(WebSocketCloseStatus status, string reason, CancellationToken ct)
    {
        foreach (var (id, channel) in _clients)
        {
            try
            {
                if (channel.Socket.State == WebSocketState.Open)
                {
                    await channel.Socket.CloseAsync(status, reason, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ws.close_failed id={Id}", id);
            }
            _clients.TryRemove(id, out _);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Per-client channel. Owns a <see cref="SemaphoreSlim"/> that serialises sends, since
    /// <see cref="WebSocket.SendAsync"/> is not safe for concurrent invocation.
    /// </summary>
    private sealed class ClientChannel
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public ClientChannel(WebSocket socket)
        {
            Socket = socket;
        }

        public WebSocket Socket { get; }

        public async Task SendAsync(byte[] payload, CancellationToken ct)
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Socket.State != WebSocketState.Open) return;
                await Socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
