using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Tests;

public class ContentExtractorTests
{
    [Fact]
    public async Task ExtractAsync_InvalidUrl_ReturnsInvalidUrlError()
    {
        var result = await ContentExtractor.ExtractAsync("not-a-valid-url");

        Assert.Equal("not-a-valid-url", result.Url);
        Assert.Equal("Invalid URL", result.Error);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExtractManyAsync_ReturnsOneResultPerInput_ForInvalidUrls()
    {
        var urls = new[] { "notaurl", "ftp://example.com/file.txt" };

        var results = await ContentExtractor.ExtractManyAsync(urls, maxConcurrency: 2);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("Invalid URL", r.Error));
    }

    [Fact]
    public void Truncate_UnderLimit_ReturnsOriginalText()
    {
        var text = "short text";

        var truncated = ContentExtractor.Truncate(text, maxChars: 50);

        Assert.Equal(text, truncated);
    }

    [Fact]
    public void Truncate_OverLimit_AppendsEllipsis()
    {
        var text = "one two three four five six";

        var truncated = ContentExtractor.Truncate(text, maxChars: 12);

        Assert.EndsWith("...", truncated);
        Assert.True(truncated.Length <= 15);
        Assert.StartsWith("one two", truncated, StringComparison.Ordinal);
    }
}
