using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Windows.Tests;

public sealed class ClipboardToolsTests
{
    [Fact]
    public async Task ClipboardRead_WhenClipboardHasText_ReturnsText()
    {
        var fake = new FakeClipboardAccessor
        {
            HasText = true,
            Text = "hello"
        };

        var original = ClipboardTools.Accessor;
        ClipboardTools.Accessor = fake;
        try
        {
            var result = await ClipboardTools.ClipboardRead();

            Assert.Equal("hello", result);
        }
        finally
        {
            ClipboardTools.Accessor = original;
        }
    }

    [Fact]
    public async Task ClipboardRead_WhenClipboardIsEmpty_ReturnsClearMessage()
    {
        var fake = new FakeClipboardAccessor
        {
            HasText = false,
            Text = string.Empty
        };

        var original = ClipboardTools.Accessor;
        ClipboardTools.Accessor = fake;
        try
        {
            var result = await ClipboardTools.ClipboardRead();

            Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ClipboardTools.Accessor = original;
        }
    }

    [Fact]
    public async Task ClipboardWrite_WritesTextAndReturnsConfirmation()
    {
        var fake = new FakeClipboardAccessor();

        var original = ClipboardTools.Accessor;
        ClipboardTools.Accessor = fake;
        try
        {
            var result = await ClipboardTools.ClipboardWrite("set this");

            Assert.Equal("Clipboard updated.", result);
            Assert.Equal("set this", fake.Text);
        }
        finally
        {
            ClipboardTools.Accessor = original;
        }
    }

    [Fact]
    public async Task ClipboardAccess_RunsOnStaThread()
    {
        var fake = new FakeClipboardAccessor { HasText = true, Text = "x" };

        var original = ClipboardTools.Accessor;
        ClipboardTools.Accessor = fake;
        try
        {
            _ = await ClipboardTools.ClipboardRead();
            _ = await ClipboardTools.ClipboardWrite("y");

            Assert.All(fake.ApartmentStates, state => Assert.Equal(ApartmentState.STA, state));
            Assert.True(fake.ApartmentStates.Count >= 2);
        }
        finally
        {
            ClipboardTools.Accessor = original;
        }
    }

    private sealed class FakeClipboardAccessor : ClipboardTools.IClipboardAccessor
    {
        public bool HasText { get; set; }

        public string Text { get; set; } = string.Empty;

        public List<ApartmentState> ApartmentStates { get; } = [];

        public bool ContainsText()
        {
            ApartmentStates.Add(Thread.CurrentThread.GetApartmentState());
            return HasText;
        }

        public string GetText()
        {
            ApartmentStates.Add(Thread.CurrentThread.GetApartmentState());
            return Text;
        }

        public void SetText(string text)
        {
            ApartmentStates.Add(Thread.CurrentThread.GetApartmentState());
            Text = text;
            HasText = !string.IsNullOrEmpty(text);
        }
    }
}
