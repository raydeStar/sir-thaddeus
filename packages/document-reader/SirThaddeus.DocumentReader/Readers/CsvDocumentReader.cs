namespace SirThaddeus.DocumentReader.Readers;

public sealed class CsvDocumentReader : IDocumentReader
{
    public async Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);

        return new DocumentContent(
            Title: Path.GetFileName(path),
            Author: null,
            PageCount: null,
            TextContent: text,
            Metadata: null,
            Format: DocumentFormat.Csv);
    }
}
