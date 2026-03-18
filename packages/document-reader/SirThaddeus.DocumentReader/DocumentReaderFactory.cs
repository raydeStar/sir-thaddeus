using SirThaddeus.DocumentReader.Readers;

namespace SirThaddeus.DocumentReader;

public sealed class DocumentReaderFactory : IDocumentReader
{
    private static readonly Dictionary<string, Func<IDocumentReader>> ReaderByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = () => new PdfDocumentReader(),
            [".docx"] = () => new DocxDocumentReader(),
            [".xlsx"] = () => new XlsxDocumentReader(),
            [".csv"] = () => new CsvDocumentReader(),
            [".rtf"] = () => new RtfDocumentReader(),
            [".md"] = () => new PlainTextReader(DocumentFormat.Markdown),
            [".txt"] = () => new PlainTextReader(DocumentFormat.PlainText)
        };

    public IDocumentReader Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return new PlainTextReader(DocumentFormat.Unknown);
        }

        return ReaderByExtension.TryGetValue(extension, out var factory)
            ? factory()
            : new PlainTextReader(DocumentFormat.Unknown);
    }

    public Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
        => Resolve(path).ReadAsync(path, cancellationToken);
}
