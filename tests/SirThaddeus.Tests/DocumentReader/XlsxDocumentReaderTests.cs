using SirThaddeus.DocumentReader;
using SirThaddeus.DocumentReader.Readers;

namespace SirThaddeus.Tests;

public sealed class XlsxDocumentReaderTests
{
    [Fact]
    public async Task ReadAsync_ExtractsSheetNameAndCellValues()
    {
        var reader = new XlsxDocumentReader();
        var fixture = DocumentFixturePaths.Resolve("sample.xlsx");

        var content = await reader.ReadAsync(fixture);

        Assert.Equal(SirThaddeus.DocumentReader.DocumentFormat.Xlsx, content.Format);
        Assert.Contains("SheetOne", content.TextContent);
        Assert.Contains("Hello", content.TextContent);
        Assert.Contains("World", content.TextContent);
    }
}
