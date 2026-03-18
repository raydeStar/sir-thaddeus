using System.Text.RegularExpressions;

namespace SirThaddeus.DocumentReader.Readers;

public sealed class RtfDocumentReader : IDocumentReader
{
    public async Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var rtf = await File.ReadAllTextAsync(path, cancellationToken);

        var text = rtf;
        text = Regex.Replace(text, @"\\par[d]?", "\n");
        text = Regex.Replace(text, @"\\'[0-9a-fA-F]{2}", string.Empty);
        text = Regex.Replace(text, @"\\[a-zA-Z]+-?\d* ?", string.Empty);
        text = Regex.Replace(text, "[{}]", string.Empty);
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

        return new DocumentContent(
            Title: Path.GetFileName(path),
            Author: null,
            PageCount: null,
            TextContent: text,
            Metadata: null,
            Format: DocumentFormat.Rtf);
    }
}
