using NUlid;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Events;

/// <summary>
/// Holds the most recent <see cref="RuntimeStateEvent"/>. Used by the snapshot endpoint
/// (<c>GET /api/state</c>) and the WebSocket broadcaster on new client connections.
/// </summary>
public sealed class StateSnapshot
{
    private readonly object _lock = new();
    private RuntimeStateEvent _latest;

    /// <summary>Initialises with an Idle snapshot timestamped to construction.</summary>
    public StateSnapshot()
    {
        _latest = new RuntimeStateEvent
        {
            State = RuntimeState.Idle,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Returns the most recent state event.</summary>
    public RuntimeStateEvent Get()
    {
        lock (_lock) return _latest;
    }

    /// <summary>Replaces the snapshot. Called from the state-machine listener.</summary>
    public void Set(RuntimeStateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_lock) _latest = value;
    }
}

/// <summary>
/// Generic event-bus contract. Producers (state machine, permission system, audit
/// pipeline) publish; consumers (WebSocket broadcaster, IPC mirror) subscribe.
/// </summary>
public interface IEventBus
{
    /// <summary>Subscribes to all events. Returns an <see cref="IDisposable"/> that unsubscribes.</summary>
    IDisposable Subscribe(Func<RuntimeEvent<object?>, CancellationToken, Task> handler);

    /// <summary>Publishes an event to every subscriber. Errors in individual handlers are logged.</summary>
    Task PublishAsync<T>(string type, T payload, string? correlationId = null, CancellationToken ct = default);
}

/// <summary>
/// In-memory event bus. Subscribers are invoked sequentially per publish so handlers
/// never see out-of-order delivery for a single producer. Phase 1 only needs one
/// producer (the state machine), so this is more than sufficient.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly List<Func<RuntimeEvent<object?>, CancellationToken, Task>> _handlers = new();
    private readonly object _lock = new();
    private readonly ILogger<EventBus> _logger;

    /// <summary>Constructs an empty bus.</summary>
    public EventBus(ILogger<EventBus> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Func<RuntimeEvent<object?>, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    /// <inheritdoc/>
    public async Task PublishAsync<T>(string type, T payload, string? correlationId = null, CancellationToken ct = default)
    {
        var evt = new RuntimeEvent<object?>
        {
            Type = type,
            Id = Ulid.NewUlid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
            Payload = payload,
        };

        Func<RuntimeEvent<object?>, CancellationToken, Task>[] snapshot;
        lock (_lock) snapshot = _handlers.ToArray();

        foreach (var handler in snapshot)
        {
            try
            {
                await handler(evt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "event.handler_failed type={Type}", type);
            }
        }
    }

    private void Unsubscribe(Func<RuntimeEvent<object?>, CancellationToken, Task> handler)
    {
        lock (_lock) _handlers.Remove(handler);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly EventBus _bus;
        private readonly Func<RuntimeEvent<object?>, CancellationToken, Task> _handler;
        private bool _disposed;

        public Subscription(EventBus bus, Func<RuntimeEvent<object?>, CancellationToken, Task> handler)
        {
            _bus = bus;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bus.Unsubscribe(_handler);
        }
    }
}
