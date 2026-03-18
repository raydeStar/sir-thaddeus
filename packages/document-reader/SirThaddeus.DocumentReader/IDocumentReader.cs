namespace SirThaddeus.DocumentReader;

public interface IDocumentReader
{
    Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default);
}
