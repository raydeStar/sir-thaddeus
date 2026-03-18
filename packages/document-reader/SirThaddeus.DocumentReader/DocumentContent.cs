namespace SirThaddeus.DocumentReader;

public sealed record DocumentContent(
    string? Title,
    string? Author,
    int? PageCount,
    string TextContent,
    IReadOnlyDictionary<string, string>? Metadata,
    DocumentFormat Format);
