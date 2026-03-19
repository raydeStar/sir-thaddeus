using System.Text;

namespace SirThaddeus.DocumentReader.Readers;

public sealed class PlainTextReader : IDocumentReader
{
    private readonly DocumentFormat _format;

    public PlainTextReader(DocumentFormat format = DocumentFormat.PlainText)
    {
        _format = format;
    }

    public async Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken);

        return new DocumentContent(
            Title: Path.GetFileName(path),
            Author: null,
            PageCount: null,
            TextContent: text,
            Metadata: null,
            Format: _format);
    }
}
