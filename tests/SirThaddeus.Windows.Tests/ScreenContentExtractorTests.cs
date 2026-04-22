using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Windows.Tests;

public sealed class ScreenContentExtractorTests
{
    // ─── Content type detection ──────────────────────────────────────

    [Theory]
    [InlineData("chrome", "", "WebPage")]
    [InlineData("msedge", "", "WebPage")]
    [InlineData("firefox", "", "WebPage")]
    [InlineData("Code", "", "Code")]
    [InlineData("devenv", "", "Code")]
    [InlineData("notepad", "", "Document")]
    [InlineData("WINWORD", "", "Document")]
    [InlineData("WindowsTerminal", "", "Terminal")]
    [InlineData("powershell", "", "Terminal")]
    [InlineData("Calculator", "", "Math")]
    [InlineData("Thaddeus.Runtime", "", "Self")]
    [InlineData("unknown", "", "Unknown")]
    [InlineData("unknown", "Project - Visual Studio Code", "Code")]
    [InlineData("unknown", "report.pdf - Adobe Reader", "Document")]
    public void DetectContentType_MatchesExpected(string process, string title, string expected)
    {
        var result = ScreenContentExtractor.DetectContentType(process, title);
        Assert.Equal(expected, result);
    }

    // ─── Chrome filtering ────────────────────────────────────────────

    [Fact]
    public void Extract_FiltersOutChromeElements()
    {
        var nodes = new List<UiaNode>
        {
            // Chrome: TitleBar
            MakeNode(50037, "TitleBar", "Sir Thaddeus", top: 0),
            // Chrome: ToolBar
            MakeNode(50021, "ToolBar", "Navigation", top: 30),
            // Chrome: Close button
            MakeNode(50000, "Button", "Close", top: 0, left: 1900),
            // Content: Text
            MakeNode(50020, "Text", "Hello World", top: 100),
            // Content: more text
            MakeNode(50020, "Text", "Welcome to the app", top: 120),
        };

        var result = ScreenContentExtractor.Extract(
            "Test Window", "testapp", 1234, nodes, null, null);

        Assert.Contains("Hello World", result.ReadableContent);
        Assert.Contains("Welcome to the app", result.ReadableContent);
        Assert.DoesNotContain("TitleBar", result.ReadableContent);
        Assert.DoesNotContain("Navigation", result.ReadableContent);
        Assert.DoesNotContain("Close", result.ReadableContent);
    }

    // ─── Framework noise filtering ───────────────────────────────────

    [Fact]
    public void Extract_FiltersFrameworkNoise()
    {
        var nodes = new List<UiaNode>
        {
            new()
            {
                ControlType = 50020, RoleLabel = "Text",
                Name = "Avalonia.Controls.StackPanel",
                BoundsTop = 50,
            },
            new()
            {
                ControlType = 50020, RoleLabel = "Text",
                Name = "Actual content here",
                BoundsTop = 100,
            },
        };

        var result = ScreenContentExtractor.Extract(
            "Test", "testapp", 1, nodes, null, null);

        Assert.Contains("Actual content here", result.ReadableContent);
        Assert.DoesNotContain("StackPanel", result.ReadableContent);
    }

    // ─── Reading order sorting ───────────────────────────────────────

    [Fact]
    public void Extract_SortsContentByReadingOrder()
    {
        var nodes = new List<UiaNode>
        {
            MakeNode(50020, "Text", "Third item", top: 200, left: 10),
            MakeNode(50020, "Text", "First item", top: 50, left: 10),
            MakeNode(50020, "Text", "Second item", top: 100, left: 10),
        };

        var result = ScreenContentExtractor.Extract(
            "Test", "testapp", 1, nodes, null, null);

        var firstIdx = result.ReadableContent.IndexOf("First item", StringComparison.Ordinal);
        var secondIdx = result.ReadableContent.IndexOf("Second item", StringComparison.Ordinal);
        var thirdIdx = result.ReadableContent.IndexOf("Third item", StringComparison.Ordinal);

        Assert.True(firstIdx < secondIdx, "First item should appear before Second");
        Assert.True(secondIdx < thirdIdx, "Second item should appear before Third");
    }

    // ─── Self-detection ──────────────────────────────────────────────

    [Fact]
    public void Extract_SelfProcess_ReturnsBriefSummary()
    {
        var nodes = new List<UiaNode>
        {
            MakeNode(50020, "Text", "Chat messages here", top: 100),
        };

        var result = ScreenContentExtractor.Extract(
            "Sir Thaddeus", "Thaddeus.Runtime", 999, nodes, null, null);

        Assert.Equal("Self", result.ContentType);
        Assert.Contains("own application window", result.ReadableContent);
        Assert.DoesNotContain("Chat messages here", result.ReadableContent);
    }

    // ─── Lock screen edge case ───────────────────────────────────────

    [Fact]
    public void Extract_LockScreen_ReturnsSecurityMessage()
    {
        var result = ScreenContentExtractor.Extract(
            "Windows Default Lock Screen", "LockApp", 100,
            new List<UiaNode>(), null, null);

        Assert.Equal("System", result.ContentType);
        Assert.Contains("login or lock screen", result.ReadableContent);
    }

    // ─── Browser content preference ──────────────────────────────────

    [Fact]
    public void Extract_PrefersBrowserPageContent_OverUiaNodes()
    {
        var nodes = new List<UiaNode>
        {
            MakeNode(50020, "Text", "Tab bar noise", top: 30),
        };

        var result = ScreenContentExtractor.Extract(
            "Khan Academy - Chrome", "chrome", 555, nodes,
            "https://khanacademy.org",
            "The quadratic formula: x = (-b ± √(b² - 4ac)) / 2a");

        Assert.Equal("WebPage", result.ContentType);
        Assert.Contains("quadratic formula", result.ReadableContent);
        Assert.DoesNotContain("Tab bar noise", result.ReadableContent);
    }

    // ─── OCR fallback ────────────────────────────────────────────────

    [Fact]
    public void ExtractFromOcr_ReturnsContentWithLimitation()
    {
        var result = ScreenContentExtractor.ExtractFromOcr(
            "Notepad", "notepad", 42,
            "This is OCR text from the screen.",
            null);

        Assert.Equal("Document", result.ContentType);
        Assert.Contains("OCR text from the screen", result.ReadableContent);
        Assert.Contains("OCR", result.Limitations);
    }

    [Fact]
    public void ExtractFromOcr_EmptyOcr_ReportsLimitation()
    {
        var result = ScreenContentExtractor.ExtractFromOcr(
            "App", "myapp", 1, null, null);

        Assert.Empty(result.ReadableContent);
        Assert.Contains("No readable text", result.Limitations);
    }

    // ─── Empty desktop ───────────────────────────────────────────────

    [Fact]
    public void EmptyDesktop_ReturnsSystemContentType()
    {
        var result = ScreenContentExtractor.EmptyDesktop();

        Assert.Equal("System", result.ContentType);
        Assert.Contains("No application window", result.ReadableContent);
    }

    // ─── Truncation ──────────────────────────────────────────────────

    [Fact]
    public void Extract_TruncatesLongContent()
    {
        var nodes = new List<UiaNode>();
        for (var i = 0; i < 200; i++)
        {
            nodes.Add(MakeNode(50020, "Text", $"Line {i}: " + new string('x', 50), top: i * 20));
        }

        var result = ScreenContentExtractor.Extract(
            "Test", "testapp", 1, nodes, null, null);

        Assert.Contains("[Content truncated", result.ReadableContent);
    }

    // ─── ScreenReadResult.ToPromptText ───────────────────────────────

    [Fact]
    public void ToPromptText_ProducesWellFormedOutput()
    {
        var result = new ScreenReadResult
        {
            WindowContext = "Chrome — \"Khan Academy\"",
            ContentType = "WebPage",
            ReadableContent = "The quadratic formula is x = (-b ± √(b² - 4ac)) / 2a",
            Limitations = "Page contains 2 images that could not be read.",
        };

        var text = result.ToPromptText();

        Assert.StartsWith("[Screen Read]", text);
        Assert.Contains("Window: Chrome — \"Khan Academy\"", text);
        Assert.Contains("Content Type: WebPage", text);
        Assert.Contains("Content:", text);
        Assert.Contains("quadratic formula", text);
        Assert.Contains("Limitations:", text);
    }

    [Fact]
    public void ToPromptText_EmptyContent_ShowsPlaceholder()
    {
        var result = new ScreenReadResult
        {
            ContentType = "Unknown",
            Limitations = "No elements could be read.",
        };

        var text = result.ToPromptText();

        Assert.Contains("(no readable text content detected)", text);
    }

    // ─── Actions summary ─────────────────────────────────────────────

    [Fact]
    public void Extract_SummarizesAvailableActions()
    {
        var nodes = new List<UiaNode>
        {
            MakeNode(50020, "Text", "Main content", top: 100),
            MakeNode(50000, "Button", "Submit", top: 200),
            MakeNode(50000, "Button", "Cancel", top: 200, left: 100),
            MakeNode(50005, "Link", "Learn more", top: 250),
        };

        var result = ScreenContentExtractor.Extract(
            "Form", "testapp", 1, nodes, null, null);

        Assert.Contains("Submit", result.AvailableActions);
        Assert.Contains("Cancel", result.AvailableActions);
        Assert.Contains("Learn more", result.AvailableActions);
    }

    // ─── Window context formatting ───────────────────────────────────

    [Fact]
    public void Extract_WebPage_IncludesUrl()
    {
        var result = ScreenContentExtractor.Extract(
            "Google", "chrome", 1,
            new List<UiaNode> { MakeNode(50020, "Text", "Search", top: 100) },
            "https://google.com", null);

        Assert.Contains("https://google.com", result.WindowContext);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static UiaNode MakeNode(
        int controlType, string roleLabel, string name,
        int top = 0, int left = 0) => new()
    {
        ControlType = controlType,
        RoleLabel = roleLabel,
        Name = name,
        BoundsTop = top,
        BoundsLeft = left,
        BoundsRight = left + 100,
        BoundsBottom = top + 20,
    };
}
