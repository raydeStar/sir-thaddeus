namespace SirThaddeus.Voice;

/// <summary>
/// Voice-specific runtime states owned by the voice orchestrator.
/// This enum is intentionally isolated from the global AssistantState
/// to avoid widening blast radius in core runtime/UI layers.
/// </summary>
public enum VoiceState
{
    Idle,
    Listening,
    Transcribing,
    Thinking,
    Speaking,
    Faulted
}

/// <summary>
/// Event args for voice state transitions.
/// </summary>
public sealed class VoiceStateChangedEventArgs : EventArgs
{
    public VoiceStateChangedEventArgs(
        VoiceState previousState,
        VoiceState currentState,
        string? reason = null)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Reason = reason;
    }

    public VoiceState PreviousState { get; }
    public VoiceState CurrentState { get; }
    public string? Reason { get; }
}

/// <summary>
/// Read-only source for voice state snapshots and transitions.
/// Useful for UI mapping without coupling UI to orchestrator internals.
/// </summary>
public interface IVoiceStateSource
{
    VoiceState CurrentState { get; }
    event EventHandler<VoiceStateChangedEventArgs>? StateChanged;
}
