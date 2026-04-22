using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Shell.Windows;
using Xunit;

namespace Thaddeus.Shell.Tests;

public sealed class CompactPanelLauncherTests
{
    [Fact]
    public void Show_opens_surface_when_closed()
    {
        var surface = new FakeSurface();
        var launcher = new CompactPanelLauncher(surface, NullLogger<CompactPanelLauncher>.Instance);

        launcher.Show("http://127.0.0.1:42/compact");

        Assert.Equal(1, surface.OpenCount);
        Assert.Equal(0, surface.ShowCount);
        Assert.Equal("http://127.0.0.1:42/compact", surface.LastOpenUrl);
        Assert.True(launcher.IsVisible);
    }

    [Fact]
    public void Show_when_already_open_just_restores()
    {
        var surface = new FakeSurface();
        var launcher = new CompactPanelLauncher(surface, NullLogger<CompactPanelLauncher>.Instance);

        launcher.Show("http://127.0.0.1:42/compact");
        launcher.Show("http://127.0.0.1:42/compact");

        Assert.Equal(1, surface.OpenCount);
        Assert.Equal(1, surface.ShowCount);
        Assert.True(launcher.IsVisible);
    }

    [Fact]
    public void Hide_when_open_minimises_and_clears_visible_flag()
    {
        var surface = new FakeSurface();
        var launcher = new CompactPanelLauncher(surface, NullLogger<CompactPanelLauncher>.Instance);

        launcher.Show("http://127.0.0.1:42/compact");
        launcher.Hide();

        Assert.Equal(1, surface.HideCount);
        Assert.False(launcher.IsVisible);
        Assert.True(surface.IsOpen); // still open, just hidden
    }

    [Fact]
    public void Hide_when_never_opened_is_noop()
    {
        var surface = new FakeSurface();
        var launcher = new CompactPanelLauncher(surface, NullLogger<CompactPanelLauncher>.Instance);

        launcher.Hide();

        Assert.Equal(0, surface.HideCount);
        Assert.False(launcher.IsVisible);
    }

    [Fact]
    public void Toggle_alternates_visibility_across_repeated_calls()
    {
        var surface = new FakeSurface();
        var launcher = new CompactPanelLauncher(surface, NullLogger<CompactPanelLauncher>.Instance);

        launcher.Toggle("http://127.0.0.1:42/compact"); // -> show (open)
        Assert.True(launcher.IsVisible);
        Assert.Equal(1, surface.OpenCount);

        launcher.Toggle("http://127.0.0.1:42/compact"); // -> hide
        Assert.False(launcher.IsVisible);
        Assert.Equal(1, surface.HideCount);

        launcher.Toggle("http://127.0.0.1:42/compact"); // -> restore (already open)
        Assert.True(launcher.IsVisible);
        Assert.Equal(1, surface.OpenCount);
        Assert.Equal(1, surface.ShowCount);
    }

    [Fact]
    public void Close_tears_down_surface_and_resets_state()
    {
        var surface = new FakeSurface();
        var launcher = new CompactPanelLauncher(surface, NullLogger<CompactPanelLauncher>.Instance);

        launcher.Show("http://127.0.0.1:42/compact");
        launcher.Close();

        Assert.Equal(1, surface.CloseCount);
        Assert.False(launcher.IsVisible);
        Assert.False(surface.IsOpen);

        // After close, Show should reopen.
        launcher.Show("http://127.0.0.1:42/compact");
        Assert.Equal(2, surface.OpenCount);
        Assert.True(launcher.IsVisible);
    }

    [Fact]
    public void Show_throws_on_blank_url()
    {
        var launcher = new CompactPanelLauncher(new FakeSurface(), NullLogger<CompactPanelLauncher>.Instance);
        Assert.Throws<ArgumentException>(() => launcher.Show(""));
    }

    private sealed class FakeSurface : ICompactWindowSurface
    {
        public bool IsOpen { get; private set; }
        public int OpenCount { get; private set; }
        public int ShowCount { get; private set; }
        public int HideCount { get; private set; }
        public int CloseCount { get; private set; }
        public string? LastOpenUrl { get; private set; }

        public void Open(string url)
        {
            OpenCount++;
            LastOpenUrl = url;
            IsOpen = true;
        }
        public void Show()
        {
            if (!IsOpen) throw new InvalidOperationException();
            ShowCount++;
        }
        public void Hide()
        {
            if (!IsOpen) throw new InvalidOperationException();
            HideCount++;
        }
        public void Close()
        {
            CloseCount++;
            IsOpen = false;
        }
    }
}
