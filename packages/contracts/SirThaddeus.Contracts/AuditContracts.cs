namespace SirThaddeus.Contracts;

public sealed record AuditEntryDto(
    string Id,
    string Category,
    string Message,
    DateTimeOffset TimestampUtc,
    string? CorrelationId = null,
    string? MetadataJson = null);

public sealed record HealthResponse(
    string Status,
    string Version,
    string Runtime,
    DateTimeOffset UtcNow);
