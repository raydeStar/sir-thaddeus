using SirThaddeus.DocumentReader;
using SirThaddeus.DocumentReader.Readers;

namespace SirThaddeus.Tests;

public sealed class DocxDocumentReaderTests
{
    [Fact]
    public async Task ReadAsync_ExtractsDocxText()
    {
        var reader = new DocxDocumentReader();
        var fixture = DocumentFixturePaths.Resolve("sample.docx");

        var content = await reader.ReadAsync(fixture);

        Assert.Equal(SirThaddeus.DocumentReader.DocumentFormat.Docx, content.Format);
        Assert.Contains("Hello DOCX fixture", content.TextContent);
    }
}
