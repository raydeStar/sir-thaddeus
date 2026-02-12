using System.Text.Json.Serialization;

namespace SirThaddeus.Voice;

public enum VoiceEventType
{
    MicDown,
    MicUp,
    Shutup,
    Fault
}

/// <summary>
/// Serialized event shape for queueing input into the orchestrator.
/// </summary>
public readonly record struct VoiceEvent(
    VoiceEventType Type,
    DateTimeOffset AtUtc,
    string Detail = "")
{
    public static VoiceEvent MicDown()
        => new(VoiceEventType.MicDown, DateTimeOffset.UtcNow);

    public static VoiceEvent MicUp()
        => new(VoiceEventType.MicUp, DateTimeOffset.UtcNow);

    public static VoiceEvent Shutup()
        => new(VoiceEventType.Shutup, DateTimeOffset.UtcNow);

    public static VoiceEvent Fault(string detail)
        => new(VoiceEventType.Fault, DateTimeOffset.UtcNow, detail);
}

public enum VoiceEndReason
{
    Interrupt,
    Shutup,
    Complete,
    Fault,
    Timeout
}

/// <summary>
/// Small transport model for captured microphone audio.
/// </summary>
public sealed record VoiceAudioClip
{
    [JsonPropertyName("audioBytes")]
    public required byte[] AudioBytes { get; init; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "audio/wav";

    [JsonPropertyName("sampleRateHz")]
    public int SampleRateHz { get; init; } = 16000;

    [JsonPropertyName("channels")]
    public int Channels { get; init; } = 1;

    [JsonPropertyName("bitsPerSample")]
    public int BitsPerSample { get; init; } = 16;
}

public sealed record VoiceAgentResponse
{
    public required string Text { get; init; }
    public bool Success { get; init; } = true;
    public string? Error { get; init; }
}

/// <summary>
/// Capture service contract used by the orchestrator.
/// </summary>
public interface IAudioCaptureService
{
    bool IsCapturing { get; }
    Task StartCaptureAsync(string sessionId, CancellationToken cancellationToken);
    Task<VoiceAudioClip?> StopCaptureAsync(string sessionId, CancellationToken cancellationToken);
    Task AbortCaptureAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Playback service contract used by the orchestrator.
/// </summary>
public interface IAudioPlaybackService
{
    bool IsPlaying { get; }
    Task PlayTextAsync(string text, string sessionId, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Speech-to-text service contract.
/// </summary>
public interface IAsrService
{
    Task<string> TranscribeAsync(
        VoiceAudioClip clip,
        string sessionId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Bridge contract for agent orchestration.
/// Must call AgentOrchestrator.ProcessAsync(...) underneath.
/// </summary>
public interface IVoiceAgentService
{
    Task<VoiceAgentResponse> ProcessAsync(
        string transcript,
        string sessionId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional progress sink for future Planning/Acting instrumentation.
/// Not required for the default black-box agent flow.
/// </summary>
public interface IAgentProgressSink
{
    void PlanningStarted(string sessionId);
    void ToolCallStarted(string sessionId, string toolName);
    void ToolCallFinished(string sessionId, string toolName, bool success);
    void FinalAnswerReady(string sessionId);
}

public sealed record VoiceSessionOrchestratorOptions
{
    public TimeSpan AsrTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan SpeakingTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan QueueDrainTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

// ─────────────────────────────────────────────────────────────────────
// Voice Progress (live transcript / debug feedback)
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Fired by the orchestrator at meaningful progress points so the UI
/// can display live transcript text, agent responses, and phase info.
/// </summary>
public sealed class VoiceProgressEventArgs : EventArgs
{
    public required VoiceProgressKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string SessionId { get; init; } = "";
}

public enum VoiceProgressKind
{
    /// <summary>ASR returned a transcript.</summary>
    TranscriptReady,

    /// <summary>Agent produced a final response.</summary>
    AgentResponseReady,

    /// <summary>Informational phase label (e.g. "Sending to agent...").</summary>
    PhaseInfo
}
