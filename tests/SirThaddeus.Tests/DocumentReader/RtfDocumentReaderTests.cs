using SirThaddeus.DocumentReader;
using SirThaddeus.DocumentReader.Readers;

namespace SirThaddeus.Tests;

public sealed class RtfDocumentReaderTests
{
    [Fact]
    public async Task ReadAsync_StripsRtfFormatting()
    {
        var reader = new RtfDocumentReader();
        var fixture = DocumentFixturePaths.Resolve("sample.rtf");

        var content = await reader.ReadAsync(fixture);

        Assert.Equal(SirThaddeus.DocumentReader.DocumentFormat.Rtf, content.Format);
        Assert.Contains("Hello RTF fixture", content.TextContent);
        Assert.Contains("sample document", content.TextContent);
        Assert.DoesNotContain("\\rtf1", content.TextContent);
        Assert.DoesNotContain("\\fonttbl", content.TextContent);
    }

    [Fact]
    public async Task ReadAsync_SetsTitleToFileName()
    {
        var reader = new RtfDocumentReader();
        var fixture = DocumentFixturePaths.Resolve("sample.rtf");

        var content = await reader.ReadAsync(fixture);

        Assert.Equal("sample.rtf", content.Title);
    }

    [Fact]
    public async Task ReadAsync_RemovesBraces()
    {
        var reader = new RtfDocumentReader();
        var fixture = DocumentFixturePaths.Resolve("sample.rtf");

        var content = await reader.ReadAsync(fixture);

        Assert.DoesNotContain("{", content.TextContent);
        Assert.DoesNotContain("}", content.TextContent);
    }
}
