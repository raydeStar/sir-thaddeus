namespace Thaddeus.SharedTypes;

/// <summary>
/// Top-level runtime state machine values shared between the runtime, shell, and
/// workspace. The runtime is the sole producer; consumers must not invent their own
/// states. Mirrors <c>packages/shared-schemas/runtime-state.schema.json</c>.
/// </summary>
public enum RuntimeState
{
    /// <summary>No active turn, voice idle, ready for input.</summary>
    Idle,

    /// <summary>Microphone is capturing audio prior to STT.</summary>
    Listening,

    /// <summary>Captured audio is being transcribed.</summary>
    Transcribing,

    /// <summary>LLM orchestration is underway.</summary>
    Thinking,

    /// <summary>Blocked on a native permission prompt.</summary>
    AwaitingPermission,

    /// <summary>Tools are executing on behalf of the current turn.</summary>
    ExecutingTools,

    /// <summary>TTS is emitting audio.</summary>
    Speaking,

    /// <summary>User-initiated pause.</summary>
    Paused,

    /// <summary>An error has surfaced and is being shown to the user.</summary>
    Error,

    /// <summary>Transient state while cancellation drains. Resolves to <see cref="Idle"/>.</summary>
    Stopping,
}
