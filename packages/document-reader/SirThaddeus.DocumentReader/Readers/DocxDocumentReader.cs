using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace SirThaddeus.DocumentReader.Readers;

public sealed class DocxDocumentReader : IDocumentReader
{
    public Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document.Body;
        var text = body is null
            ? string.Empty
            : string.Join("\n", body.Descendants<Text>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

        var props = doc.PackageProperties;

        return Task.FromResult(new DocumentContent(
            Title: props.Title ?? Path.GetFileName(path),
            Author: props.Creator,
            PageCount: null,
            TextContent: text,
            Metadata: new Dictionary<string, string>
            {
                ["subject"] = props.Subject ?? string.Empty,
                ["keywords"] = props.Keywords ?? string.Empty
            },
            Format: DocumentFormat.Docx));
    }
}
