namespace SirThaddeus.Agent.Memory;

/// <summary>
/// Request envelope for memory prefetch.
/// </summary>
public sealed record MemoryContextRequest
{
    public required string UserMessage { get; init; }
    public bool MemoryEnabled { get; init; } = true;
    public bool IsColdGreeting { get; init; }
    public string? ActiveProfileId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(1500);
}

/// <summary>
/// Provenance metadata for memory retrieval.
/// </summary>
public sealed record MemoryContextProvenance
{
    public string SourceTool { get; init; } = "MemoryRetrieve";
    public string RetrievalMode { get; init; } = "default";
    public bool Skipped { get; init; }
    public bool TimedOut { get; init; }
    public bool Success { get; init; }
    public long DurationMs { get; init; }
    public int Facts { get; init; }
    public int Events { get; init; }
    public int Chunks { get; init; }
    public int Nuggets { get; init; }
    public bool HasProfile { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// Typed result for memory prefetch + prompt context.
/// </summary>
public sealed record MemoryContextResult
{
    public string PackText { get; init; } = "";
    public bool OnboardingNeeded { get; init; }
    public string? Error { get; init; }
    public MemoryContextProvenance Provenance { get; init; } = new();
}

/// <summary>
/// Provides memory context for orchestrator turns.
/// </summary>
public interface IMemoryContextProvider
{
    Task<MemoryContextResult> GetContextAsync(
        MemoryContextRequest request,
        CancellationToken cancellationToken = default);
}

