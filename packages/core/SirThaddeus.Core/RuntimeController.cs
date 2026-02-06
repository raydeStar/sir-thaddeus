using SirThaddeus.AuditLog;

namespace SirThaddeus.Core;

/// <summary>
/// Central controller for the assistant runtime.
/// Owns state transitions, cancellation, and audit event emission.
/// </summary>
public sealed class RuntimeController : IDisposable
{
    private readonly IAuditLogger _auditLogger;
    private readonly object _stateLock = new();
    private CancellationTokenSource _cts;
    private AssistantState _currentState;
    private bool _disposed;
    private bool _stopped;

    /// <summary>
    /// Raised when the state changes.
    /// </summary>
    public event EventHandler<StateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised when STOP ALL is triggered.
    /// </summary>
    public event EventHandler? StopAllTriggered;

    /// <summary>
    /// Creates a new runtime controller.
    /// </summary>
    /// <param name="auditLogger">The audit logger for recording events.</param>
    /// <param name="initialState">The initial state of the runtime.</param>
    public RuntimeController(IAuditLogger auditLogger, AssistantState initialState = AssistantState.Idle)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _currentState = initialState;
        _cts = new CancellationTokenSource();

        // Log the initial state
        LogStateChange(AssistantState.Off, initialState, "Runtime initialized");
    }

    /// <summary>
    /// Gets the current state of the assistant.
    /// </summary>
    public AssistantState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    /// <summary>
    /// Gets a cancellation token that is cancelled when STOP ALL is triggered.
    /// All long-running operations should observe this token.
    /// </summary>
    public CancellationToken RuntimeToken => _cts.Token;

    /// <summary>
    /// Gets whether the runtime has been stopped.
    /// </summary>
    public bool IsStopped
    {
        get
        {
            lock (_stateLock)
            {
                return _stopped;
            }
        }
    }

    /// <summary>
    /// Transitions to a new state.
    /// </summary>
    /// <param name="newState">The target state.</param>
    /// <param name="reason">Optional reason for the transition (for audit).</param>
    /// <returns>True if the transition was successful; false if rejected.</returns>
    public bool SetState(AssistantState newState, string? reason = null)
    {
        lock (_stateLock)
        {
            if (_stopped)
            {
                // Cannot transition after STOP ALL
                _auditLogger.Append(new AuditEvent
                {
                    Actor = "runtime",
                    Action = "STATE_CHANGE_REJECTED",
                    Target = newState.ToString(),
                    Result = "denied",
                    Details = new Dictionary<string, object>
                    {
                        ["from"] = _currentState.ToString(),
                        ["to"] = newState.ToString(),
                        ["reason"] = "Runtime has been stopped"
                    }
                });
                return false;
            }

            var previousState = _currentState;
            if (previousState == newState)
            {
                // No-op transition; don't log noise
                return true;
            }

            _currentState = newState;
            LogStateChange(previousState, newState, reason);
            
            StateChanged?.Invoke(this, new StateChangedEventArgs(previousState, newState));
            return true;
        }
    }

    /// <summary>
    /// Executes the STOP ALL command.
    /// Cancels all operations, sets state to Off, and prevents further transitions.
    /// </summary>
    public void StopAll()
    {
        lock (_stateLock)
        {
            if (_stopped)
                return;

            _stopped = true;
            var previousState = _currentState;
            _currentState = AssistantState.Off;

            // Cancel all outstanding operations
            _cts.Cancel();

            _auditLogger.Append(new AuditEvent
            {
                Actor = "user",
                Action = "STOP_ALL",
                Target = null,
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["previousState"] = previousState.ToString(),
                    ["message"] = "All operations terminated by user"
                }
            });

            StateChanged?.Invoke(this, new StateChangedEventArgs(previousState, AssistantState.Off));
            StopAllTriggered?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Resets the runtime after a STOP ALL, allowing it to be restarted.
    /// </summary>
    public void Reset()
    {
        lock (_stateLock)
        {
            if (!_stopped)
                return;

            _cts.Dispose();
            _cts = new CancellationTokenSource();
            _stopped = false;
            _currentState = AssistantState.Idle;

            _auditLogger.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "RUNTIME_RESET",
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["message"] = "Runtime reset and ready"
                }
            });

            StateChanged?.Invoke(this, new StateChangedEventArgs(AssistantState.Off, AssistantState.Idle));
        }
    }

    private void LogStateChange(AssistantState from, AssistantState to, string? reason)
    {
        var details = new Dictionary<string, object>
        {
            ["from"] = from.ToString(),
            ["to"] = to.ToString()
        };

        if (!string.IsNullOrEmpty(reason))
        {
            details["reason"] = reason;
        }

        _auditLogger.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "STATE_CHANGE",
            Target = to.ToString(),
            Result = "ok",
            Details = details
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Dispose();
    }
}

/// <summary>
/// Event args for state change events.
/// </summary>
public sealed class StateChangedEventArgs : EventArgs
{
    /// <summary>
    /// The previous state.
    /// </summary>
    public AssistantState PreviousState { get; }

    /// <summary>
    /// The new state.
    /// </summary>
    public AssistantState NewState { get; }

    /// <summary>
    /// Creates new state changed event args.
    /// </summary>
    public StateChangedEventArgs(AssistantState previousState, AssistantState newState)
    {
        PreviousState = previousState;
        NewState = newState;
    }
}
