using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.State;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Events;

/// <summary>
/// Bridges <see cref="RuntimeStateMachine"/> transitions to the <see cref="IEventBus"/>
/// and updates the <see cref="StateSnapshot"/>. Registered as an
/// <see cref="IHostedService"/>; lives for the runtime's lifetime.
/// </summary>
public sealed class StateMachineEventBridge : IHostedService
{
    private readonly RuntimeStateMachine _machine;
    private readonly IEventBus _bus;
    private readonly StateSnapshot _snapshot;
    private readonly ILogger<StateMachineEventBridge> _logger;

    /// <summary>Wires the bridge.</summary>
    public StateMachineEventBridge(
        RuntimeStateMachine machine,
        IEventBus bus,
        StateSnapshot snapshot,
        ILogger<StateMachineEventBridge> logger)
    {
        _machine = machine;
        _bus = bus;
        _snapshot = snapshot;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _machine.Transitioned += OnTransitioned;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _machine.Transitioned -= OnTransitioned;
        return Task.CompletedTask;
    }

    private void OnTransitioned(RuntimeState from, RuntimeState to, StateTrigger trigger)
    {
        var payload = new RuntimeStateEvent
        {
            State = to,
            Timestamp = DateTimeOffset.UtcNow,
        };
        _snapshot.Set(payload);
        _logger.LogInformation("state.transition from={From} to={To} trigger={Trigger}", from, to, trigger);
        _ = _bus.PublishAsync("runtime.state", payload);
    }
}
