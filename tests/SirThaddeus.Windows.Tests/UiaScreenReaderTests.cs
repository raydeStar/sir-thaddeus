using System.Drawing;
using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Windows.Tests;

public sealed class UiaScreenReaderTests
{
    [Theory]
    [InlineData("https://example.com/path", "https://example.com/path")]
    [InlineData("example.com/docs", "https://example.com/docs")]
    [InlineData("localhost:8080/dashboard", "http://localhost:8080/dashboard")]
    [InlineData("127.0.0.1:3000", "http://127.0.0.1:3000/")]
    public void TryNormalizeBrowserUrl_AcceptsSupportedUrls(string raw, string expected)
    {
        var actual = UiaScreenReader.TryNormalizeBrowserUrl(raw);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("edge://settings")]
    [InlineData("chrome://extensions")]
    [InlineData("about:blank")]
    [InlineData("file://c:/temp/test.html")]
    [InlineData("not a url")]
    [InlineData("search query terms")]
    public void TryNormalizeBrowserUrl_RejectsUnsupportedUrls(string raw)
    {
        var actual = UiaScreenReader.TryNormalizeBrowserUrl(raw);
        Assert.Null(actual);
    }

    [Fact]
    public void ResolveCaptureRect_UsesActiveWindowBounds_WhenAvailable()
    {
        var bounds = new Rectangle(50, 75, 1280, 720);

        var actual = ScreenTools.ResolveCaptureRect("active_window", bounds, 1920, 1080);

        Assert.Equal(bounds, actual);
    }

    [Fact]
    public void ResolveCaptureRect_FallsBackToScreen_WhenActiveWindowBoundsMissing()
    {
        var actual = ScreenTools.ResolveCaptureRect("active_window", null, 1920, 1080);

        Assert.Equal(new Rectangle(0, 0, 1920, 1080), actual);
    }

    [Fact]
    public void ResolveCaptureRect_UsesScreenForFullScreenTarget()
    {
        var bounds = new Rectangle(10, 20, 300, 400);

        var actual = ScreenTools.ResolveCaptureRect("full_screen", bounds, 2560, 1440);

        Assert.Equal(new Rectangle(0, 0, 2560, 1440), actual);
    }
}