using SirThaddeus.DocumentReader;
using SirThaddeus.DocumentReader.Readers;

namespace SirThaddeus.Tests;

public sealed class CsvDocumentReaderTests
{
    [Fact]
    public async Task ReadAsync_ExtractsCsvText()
    {
        var reader = new CsvDocumentReader();
        var fixture = DocumentFixturePaths.Resolve("sample.csv");

        var content = await reader.ReadAsync(fixture);

        Assert.Equal(SirThaddeus.DocumentReader.DocumentFormat.Csv, content.Format);
        Assert.Contains("name,role", content.TextContent);
        Assert.Contains("Alice,Engineer", content.TextContent);
        Assert.Contains("Bob,Designer", content.TextContent);
    }

    [Fact]
    public async Task ReadAsync_SetsTitleToFileName()
    {
        var reader = new CsvDocumentReader();
        var fixture = DocumentFixturePaths.Resolve("sample.csv");

        var content = await reader.ReadAsync(fixture);

        Assert.Equal("sample.csv", content.Title);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullMetadata()
    {
        var reader = new CsvDocumentReader();
        var fixture = DocumentFixturePaths.Resolve("sample.csv");

        var content = await reader.ReadAsync(fixture);

        Assert.Null(content.Author);
        Assert.Null(content.PageCount);
        Assert.Null(content.Metadata);
    }
}
