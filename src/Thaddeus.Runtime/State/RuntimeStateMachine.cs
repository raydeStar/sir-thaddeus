using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.State;

/// <summary>
/// Triggers that can move the runtime between top-level states. Mirrors the table
/// in spec §7.4. Illegal transitions are logged at warning level and ignored.
/// </summary>
public enum StateTrigger
{
    /// <summary>User submitted typed text input.</summary>
    UserTextSubmitted,

    /// <summary>User pressed the push-to-talk binding.</summary>
    UserPttPress,

    /// <summary>User released the push-to-talk binding (with sufficient capture).</summary>
    UserPttReleaseCaptured,

    /// <summary>User released the push-to-talk binding too quickly to count as a capture.</summary>
    UserPttReleaseSilent,

    /// <summary>STT finished and produced a non-empty transcript.</summary>
    SttDoneTranscript,

    /// <summary>STT finished but the transcript was empty.</summary>
    SttDoneEmpty,

    /// <summary>The current plan requires a permission grant.</summary>
    PlanRequiresPermission,

    /// <summary>The current plan has tool calls to execute.</summary>
    PlanToolCalls,

    /// <summary>The current plan is text-only and ready to compose.</summary>
    PlanTextOnly,

    /// <summary>Tool execution completed.</summary>
    ToolsDone,

    /// <summary>Permission grant was approved.</summary>
    PermissionGranted,

    /// <summary>Permission grant was denied.</summary>
    PermissionDenied,

    /// <summary>TTS playback queue drained.</summary>
    TtsDone,

    /// <summary>User asked to stop everything.</summary>
    UserStopAll,

    /// <summary>Cancellation drained successfully and the runtime is back at rest.</summary>
    StoppingComplete,
}

/// <summary>
/// Single source of truth for the runtime state machine (spec §7.1, §7.4). The shell
/// and workspace project this state via events; they never compute it themselves.
/// </summary>
public sealed class RuntimeStateMachine
{
    private readonly object _lock = new();
    private readonly ILogger<RuntimeStateMachine> _logger;
    private RuntimeState _current = RuntimeState.Idle;

    /// <summary>Initialises the state machine in <see cref="RuntimeState.Idle"/>.</summary>
    public RuntimeStateMachine(ILogger<RuntimeStateMachine> logger)
    {
        _logger = logger;
    }

    /// <summary>Current top-level state (snapshot).</summary>
    public RuntimeState Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>
    /// Raised whenever the state changes (only on actual transitions). Listeners are
    /// invoked while the internal lock is held — keep handlers fast and non-blocking.
    /// </summary>
    public event Action<RuntimeState, RuntimeState, StateTrigger>? Transitioned;

    /// <summary>
    /// Attempts a transition. Returns true if it was applied, false if the trigger is
    /// invalid for the current state (in which case it is logged and discarded).
    /// </summary>
    public bool TryTransition(StateTrigger trigger, bool voiceMode = false)
    {
        lock (_lock)
        {
            var from = _current;
            var to = ResolveTransition(from, trigger, voiceMode);
            if (to is null)
            {
                _logger.LogWarning("state.illegal_transition from={From} trigger={Trigger}", from, trigger);
                return false;
            }
            if (to == from) return true;
            _current = to.Value;
            Transitioned?.Invoke(from, to.Value, trigger);
            return true;
        }
    }

    /// <summary>Forces the state to <see cref="RuntimeState.Idle"/>. Reserved for tests and recovery.</summary>
    internal void ForceIdle()
    {
        lock (_lock)
        {
            if (_current == RuntimeState.Idle) return;
            var from = _current;
            _current = RuntimeState.Idle;
            Transitioned?.Invoke(from, RuntimeState.Idle, StateTrigger.StoppingComplete);
        }
    }

    private static RuntimeState? ResolveTransition(RuntimeState from, StateTrigger trigger, bool voiceMode)
    {
        // Spec §7.4 transition table. Stop-all is allowed from any state.
        if (trigger == StateTrigger.UserStopAll)
        {
            return from == RuntimeState.Idle ? RuntimeState.Idle : RuntimeState.Stopping;
        }
        if (trigger == StateTrigger.StoppingComplete)
        {
            return from == RuntimeState.Stopping ? RuntimeState.Idle : null;
        }

        return (from, trigger) switch
        {
            (RuntimeState.Idle, StateTrigger.UserTextSubmitted) => RuntimeState.Thinking,
            (RuntimeState.Idle, StateTrigger.UserPttPress) => RuntimeState.Listening,

            (RuntimeState.Listening, StateTrigger.UserPttReleaseCaptured) => RuntimeState.Transcribing,
            (RuntimeState.Listening, StateTrigger.UserPttReleaseSilent) => RuntimeState.Idle,

            (RuntimeState.Transcribing, StateTrigger.SttDoneTranscript) => RuntimeState.Thinking,
            (RuntimeState.Transcribing, StateTrigger.SttDoneEmpty) => RuntimeState.Idle,

            (RuntimeState.Thinking, StateTrigger.PlanRequiresPermission) => RuntimeState.AwaitingPermission,
            (RuntimeState.Thinking, StateTrigger.PlanToolCalls) => RuntimeState.ExecutingTools,
            (RuntimeState.Thinking, StateTrigger.PlanTextOnly) => voiceMode ? RuntimeState.Speaking : RuntimeState.Idle,

            (RuntimeState.ExecutingTools, StateTrigger.ToolsDone) => RuntimeState.Thinking,

            (RuntimeState.AwaitingPermission, StateTrigger.PermissionGranted) => RuntimeState.Thinking,
            (RuntimeState.AwaitingPermission, StateTrigger.PermissionDenied) => RuntimeState.Idle,

            (RuntimeState.Speaking, StateTrigger.TtsDone) => RuntimeState.Idle,

            _ => null,
        };
    }
}
