using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Shell.Platform;
using Thaddeus.Shell.Windows;
using Xunit;

namespace Thaddeus.Shell.Tests;

public sealed class ShellSessionControllerTests
{
    [Fact]
    public async Task InitializeAsync_with_supported_tray_populates_menu_and_hides_when_start_minimized()
    {
        var workspace = new FakeWorkspaceWindow();
        var tray = new FakeTrayAdapter(isSupported: true);
        var sut = new ShellSessionController(
            workspace,
            tray,
            () => Task.CompletedTask,
            NullLogger<ShellSessionController>.Instance);

        await sut.InitializeAsync(startMinimized: true, CancellationToken.None);

        Assert.NotNull(tray.Menu);
        Assert.Equal(3, tray.Menu!.Items.Count);
        Assert.Equal(1, workspace.HideCount);
        Assert.False(workspace.IsVisible);
    }

    [Fact]
    public async Task HandleWorkspaceClosing_hides_to_tray_after_successful_init()
    {
        var workspace = new FakeWorkspaceWindow();
        var tray = new FakeTrayAdapter(isSupported: true);
        var sut = new ShellSessionController(
            workspace,
            tray,
            () => Task.CompletedTask,
            NullLogger<ShellSessionController>.Instance);

        await sut.InitializeAsync(startMinimized: false, CancellationToken.None);

        var cancelled = sut.HandleWorkspaceClosing();

        Assert.True(cancelled);
        Assert.Equal(1, workspace.HideCount);
        Assert.False(workspace.IsVisible);
    }

    [Fact]
    public void HandleWorkspaceClosing_without_tray_allows_exit()
    {
        var workspace = new FakeWorkspaceWindow();
        var tray = new FakeTrayAdapter(isSupported: false);
        var sut = new ShellSessionController(
            workspace,
            tray,
            () => Task.CompletedTask,
            NullLogger<ShellSessionController>.Instance);

        var cancelled = sut.HandleWorkspaceClosing();

        Assert.False(cancelled);
        Assert.Equal(0, workspace.HideCount);
    }

    [Fact]
    public async Task Tray_open_menu_restores_workspace()
    {
        var workspace = new FakeWorkspaceWindow { IsVisible = false };
        var tray = new FakeTrayAdapter(isSupported: true);
        var sut = new ShellSessionController(
            workspace,
            tray,
            () => Task.CompletedTask,
            NullLogger<ShellSessionController>.Instance);

        await sut.InitializeAsync(startMinimized: false, CancellationToken.None);
        var open = Assert.Single(tray.Menu!.Items, x => x.Id == ShellSessionController.OpenWorkspaceMenuId);

        await open.Invoke();

        Assert.Equal(1, workspace.ShowCount);
        Assert.True(workspace.IsVisible);
    }

    [Fact]
    public async Task Tray_stop_all_menu_invokes_callback()
    {
        var workspace = new FakeWorkspaceWindow();
        var tray = new FakeTrayAdapter(isSupported: true);
        var calls = 0;
        var sut = new ShellSessionController(
            workspace,
            tray,
            () =>
            {
                calls++;
                return Task.CompletedTask;
            },
            NullLogger<ShellSessionController>.Instance);

        await sut.InitializeAsync(startMinimized: false, CancellationToken.None);
        var stopAll = Assert.Single(tray.Menu!.Items, x => x.Id == ShellSessionController.StopAllMenuId);

        await stopAll.Invoke();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Tray_menu_uses_clear_command_labels()
    {
        var workspace = new FakeWorkspaceWindow();
        var tray = new FakeTrayAdapter(isSupported: true);
        var sut = new ShellSessionController(
            workspace,
            tray,
            () => Task.CompletedTask,
            NullLogger<ShellSessionController>.Instance);

        await sut.InitializeAsync(startMinimized: false, CancellationToken.None);

        Assert.Contains(tray.Menu!.Items, item =>
            item.Id == ShellSessionController.OpenWorkspaceMenuId && item.Label == "Open Sir Thaddeus");
        Assert.Contains(tray.Menu!.Items, item =>
            item.Id == ShellSessionController.StopAllMenuId && item.Label == "Stop All Processes");
        Assert.Contains(tray.Menu!.Items, item =>
            item.Id == ShellSessionController.ExitMenuId && item.Label == "Exit Sir Thaddeus");
    }

    [Fact]
    public async Task ExitAsync_closes_compact_and_allows_real_close()
    {
        var workspace = new FakeWorkspaceWindow();
        var tray = new FakeTrayAdapter(isSupported: true);
        var compactClosed = 0;
        var sut = new ShellSessionController(
            workspace,
            tray,
            () => Task.CompletedTask,
            NullLogger<ShellSessionController>.Instance,
            closeCompactWindow: () => compactClosed++);

        await sut.InitializeAsync(startMinimized: false, CancellationToken.None);
        await sut.ExitAsync();

        Assert.Equal(1, compactClosed);
        Assert.Equal(1, workspace.CloseCount);
        Assert.False(sut.HandleWorkspaceClosing());
    }

    private sealed class FakeTrayAdapter : ITrayAdapter
    {
        public FakeTrayAdapter(bool isSupported)
        {
            IsSupported = isSupported;
        }

        public bool IsSupported { get; }

        public TrayMenu? Menu { get; private set; }

        public Task InitializeAsync(TrayMenu menu, CancellationToken ct)
        {
            Menu = menu;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWorkspaceWindow : IWorkspaceWindowSurface
    {
        public bool IsVisible { get; set; } = true;

        public int ShowCount { get; private set; }

        public int HideCount { get; private set; }

        public int CloseCount { get; private set; }

        public event WorkspaceWindowClosingHandler? ClosingRequested;

        public void Show()
        {
            ShowCount++;
            IsVisible = true;
        }

        public void Hide()
        {
            HideCount++;
            IsVisible = false;
        }

        public void Close()
        {
            CloseCount++;
        }

        public bool RaiseClosingRequested()
        {
            if (ClosingRequested is null)
            {
                return false;
            }

            return ClosingRequested();
        }
    }
}
