namespace SirThaddeus.DocumentReader;

/// <summary>
/// Extracts text content from a file in a format-specific way.
/// </summary>
public interface IDocumentReader
{
    /// <summary>
    /// Reads and extracts text from the document at the given path.
    /// </summary>
    /// <param name="path">Absolute path to the file to read.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Extracted content including metadata and text.</returns>
    Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default);
}
