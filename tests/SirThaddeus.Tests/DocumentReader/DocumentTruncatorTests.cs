using SirThaddeus.DocumentReader;

namespace SirThaddeus.Tests;

public sealed class DocumentTruncatorTests
{
    [Fact]
    public void TruncateWithNotice_NoTruncationUnderLimit()
    {
        const string input = "hello world";

        var output = DocumentTruncator.TruncateWithNotice(input, 100);

        Assert.Equal(input, output);
    }

    [Fact]
    public void TruncateWithNotice_TruncatesAtBoundaryWithSuffix()
    {
        const string input = "abcdefghijklmnopqrstuvwxyz";

        var output = DocumentTruncator.TruncateWithNotice(input, 10);

        Assert.StartsWith("abcdefghij", output);
        Assert.Contains("truncated", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("26 chars total", output);
        Assert.Contains("showing first 10", output);
    }
}
