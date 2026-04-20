namespace Thaddeus.SharedTypes;

/// <summary>
/// Envelope for every event broadcast on the WebSocket bus and mirrored over IPC.
/// Mirrors <c>packages/shared-schemas/runtime-event.schema.json</c>.
/// </summary>
/// <typeparam name="TPayload">Type of the event payload.</typeparam>
public sealed record RuntimeEvent<TPayload>
{
    /// <summary>Dotted namespace, e.g. <c>runtime.state</c>, <c>permission.granted</c>.</summary>
    public required string Type { get; init; }

    /// <summary>ULID identifying this event uniquely.</summary>
    public required string Id { get; init; }

    /// <summary>UTC timestamp when the event was produced.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Optional correlation ID tying this event to a turn or task.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Event-specific payload.</summary>
    public required TPayload Payload { get; init; }
}
