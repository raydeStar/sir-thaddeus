namespace SirThaddeus.Contracts;

public sealed record MemoryBrowseResponse(
    IReadOnlyList<MemoryFactItemDto> Facts,
    IReadOnlyList<MemoryEventItemDto> Events,
    IReadOnlyList<MemoryChunkItemDto> Chunks,
    IReadOnlyList<MemoryNuggetItemDto> Nuggets,
    int TotalFacts,
    int TotalEvents,
    int TotalChunks,
    int TotalNuggets);

public sealed record MemoryFactItemDto(
    string MemoryId,
    string? ProfileId,
    string Subject,
    string Predicate,
    string Object,
    double Confidence,
    DateTimeOffset UpdatedAtUtc,
    string? SourceRef);

public sealed record MemoryEventItemDto(
    string EventId,
    string? ProfileId,
    string Type,
    string Title,
    string? Summary,
    DateTimeOffset? WhenUtc,
    double Confidence,
    DateTimeOffset UpdatedAtUtc,
    string? SourceRef);

public sealed record MemoryChunkItemDto(
    string ChunkId,
    string SourceType,
    string? SourceRef,
    string Text,
    DateTimeOffset? WhenUtc);

public sealed record MemoryNuggetItemDto(
    string NuggetId,
    string Text,
    string? Tags,
    double Weight,
    int PinLevel,
    int UseCount,
    DateTimeOffset UpdatedAtUtc);
