namespace SirThaddeus.AuditLog;

/// <summary>
/// In-memory audit logger for unit testing purposes.
/// Stores events in a list for inspection without file I/O.
/// </summary>
public sealed class TestAuditLogger : IAuditLogger
{
    private readonly List<AuditEvent> _events = [];
    private readonly object _lock = new();

    /// <summary>
    /// Gets all logged events.
    /// </summary>
    public IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToList().AsReadOnly();
            }
        }
    }

    /// <inheritdoc />
    public void Append(AuditEvent auditEvent)
    {
        lock (_lock)
        {
            _events.Add(auditEvent);
        }
    }

    /// <inheritdoc />
    public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Append(auditEvent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEvent> ReadTail(int maxEvents)
    {
        lock (_lock)
        {
            return _events
                .Skip(Math.Max(0, _events.Count - maxEvents))
                .ToList()
                .AsReadOnly();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int maxEvents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadTail(maxEvents));
    }

    /// <summary>
    /// Clears all logged events.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }

    /// <summary>
    /// Gets events matching the specified action.
    /// </summary>
    public IReadOnlyList<AuditEvent> GetByAction(string action)
    {
        lock (_lock)
        {
            return _events.Where(e => e.Action == action).ToList().AsReadOnly();
        }
    }
}
