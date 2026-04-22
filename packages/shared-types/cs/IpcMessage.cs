namespace Thaddeus.SharedTypes;

/// <summary>
/// NDJSON envelope used between the shell and runtime over named pipe / Unix domain
/// socket. Mirrors <c>packages/shared-schemas/ipc-message.schema.json</c>.
/// </summary>
public sealed record IpcMessage
{
    /// <summary>Caller-supplied request ID. Responses reference the same id.</summary>
    public required string Id { get; init; }

    /// <summary>Message type. See spec §6.2 and the schema for canonical values.</summary>
    public required string Type { get; init; }

    /// <summary>Optional payload. JSON-serializable.</summary>
    public object? Payload { get; init; }

    /// <summary>Populated when the recipient is reporting a failure for the message.</summary>
    public IpcError? Error { get; init; }
}

/// <summary>Structured error returned over IPC.</summary>
public sealed record IpcError
{
    /// <summary>Stable error code.</summary>
    public required string Code { get; init; }

    /// <summary>Plain-English summary.</summary>
    public required string Message { get; init; }
}
