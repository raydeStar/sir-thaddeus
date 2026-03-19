using UglyToad.PdfPig;

namespace SirThaddeus.DocumentReader.Readers;

public sealed class PdfDocumentReader : IDocumentReader
{
    public Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(path);

        var pages = document.GetPages().ToArray();
        var text = string.Join("\n\n", pages.Select(p => p.Text?.Trim()).Where(t => !string.IsNullOrWhiteSpace(t)));

        return Task.FromResult(new DocumentContent(
            Title: Path.GetFileName(path),
            Author: document.Information?.Author,
            PageCount: document.NumberOfPages,
            TextContent: text,
            Metadata: new Dictionary<string, string>
            {
                ["producer"] = document.Information?.Producer ?? string.Empty,
                ["creator"] = document.Information?.Creator ?? string.Empty
            },
            Format: DocumentFormat.Pdf));
    }
}
