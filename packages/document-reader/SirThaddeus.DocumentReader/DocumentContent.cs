namespace SirThaddeus.DocumentReader;

/// <summary>
/// Holds extracted text and metadata from a parsed document.
/// </summary>
/// <param name="Title">Document title if available.</param>
/// <param name="Author">Document author if available.</param>
/// <param name="PageCount">Total page count (PDF, DOCX) or sheet count (XLSX).</param>
/// <param name="TextContent">The extracted plain-text content.</param>
/// <param name="Metadata">Arbitrary key-value metadata pairs.</param>
/// <param name="Format">The detected document format.</param>
public sealed record DocumentContent(
    string? Title,
    string? Author,
    int? PageCount,
    string TextContent,
    IReadOnlyDictionary<string, string>? Metadata,
    DocumentFormat Format);
