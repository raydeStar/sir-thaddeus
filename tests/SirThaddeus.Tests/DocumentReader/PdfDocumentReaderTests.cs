using SirThaddeus.DocumentReader;
using SirThaddeus.DocumentReader.Readers;

namespace SirThaddeus.Tests;

public sealed class PdfDocumentReaderTests
{
    [Fact]
    public async Task ReadAsync_ExtractsPdfText()
    {
        var reader = new PdfDocumentReader();
        var fixture = DocumentFixturePaths.Resolve("sample.pdf");

        var content = await reader.ReadAsync(fixture);

        Assert.Equal(SirThaddeus.DocumentReader.DocumentFormat.Pdf, content.Format);
        Assert.Contains("Hello PDF fixture", content.TextContent);
        Assert.True(content.PageCount >= 1);
    }
}
