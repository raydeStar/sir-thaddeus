namespace SirThaddeus.AuditLog;

/// <summary>
/// Interface for the audit logging system.
/// Implementations must be append-only and local-first.
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// Appends an event to the audit log.
    /// </summary>
    void Append(AuditEvent auditEvent);

    /// <summary>
    /// Appends an event to the audit log asynchronously.
    /// </summary>
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the most recent events from the audit log.
    /// </summary>
    /// <param name="maxEvents">Maximum number of events to return.</param>
    /// <returns>Events in chronological order (oldest first).</returns>
    IReadOnlyList<AuditEvent> ReadTail(int maxEvents);

    /// <summary>
    /// Reads the most recent events from the audit log asynchronously.
    /// </summary>
    /// <param name="maxEvents">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Events in chronological order (oldest first).</returns>
    Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int maxEvents, CancellationToken cancellationToken = default);
}
