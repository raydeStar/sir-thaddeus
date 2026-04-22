using Microsoft.Extensions.Hosting;
using Thaddeus.Runtime.Events;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Activity;

/// <summary>
/// Hosted service that mirrors <see cref="IActivityLog"/> mutations onto the
/// runtime <see cref="IEventBus"/> as <c>activity.appended</c> /
/// <c>activity.updated</c> events. Subscribers (the WebSocket broadcaster) push
/// the events to connected clients so the Activity UI updates in real time.
/// </summary>
public sealed class ActivityEventBridge : IHostedService, IDisposable
{
    private readonly IActivityLog _log;
    private readonly IEventBus _bus;
    private readonly ILogger<ActivityEventBridge> _logger;
    private bool _disposed;

    public ActivityEventBridge(IActivityLog log, IEventBus bus, ILogger<ActivityEventBridge> logger)
    {
        _log = log;
        _bus = bus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _log.Changed += OnChanged;
        _logger.LogDebug("activity.bridge.started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(ActivityEntry entry)
    {
        // Choose the appropriate event type based on whether the entry is still
        // running. New entries land Running; updates always carry a final status
        // or completion timestamp.
        var type = entry.CompletedAt.HasValue || entry.Status != ActivityStatus.Running
            ? ActivityEvents.Updated
            : ActivityEvents.Appended;

        // Fire-and-forget; the bus serialises handlers and logs failures.
        _ = _bus.PublishAsync(type, entry, correlationId: entry.Id, CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.Changed -= OnChanged;
    }
}
