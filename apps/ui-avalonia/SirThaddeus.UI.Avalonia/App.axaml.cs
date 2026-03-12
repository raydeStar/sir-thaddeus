using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using System.ComponentModel;

namespace SirThaddeus.UI.Avalonia;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;
    private TrayIcon? _trayIcon;
    private bool _isExiting;

    public bool MinimizeToTrayEnabled { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktopLifetime = desktop;

            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            desktop.Exit += (_, _) => DisposeTray();
            var startupOptions = AppStartupOptions.Current;

            if (startupOptions.HeadlessMode)
            {
                mainWindow.ShowInTaskbar = false;
                mainWindow.WindowState = WindowState.Minimized;
            }

            if (startupOptions.SmokeTestMode)
            {
                MinimizeToTrayEnabled = false;
                mainWindow.ConfigureTrayUi(trayAvailable: false, minimizeToTrayEnabled: false);

                Dispatcher.UIThread.Post(
                    () =>
                    {
                        _isExiting = true;
                        desktop.Shutdown(0);
                    },
                    DispatcherPriority.ApplicationIdle);
            }
            else
            {
                ConfigureTray(mainWindow);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureTray(MainWindow mainWindow)
    {
        MinimizeToTrayEnabled = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://SirThaddeus.UI.Avalonia/Assets/Icons/sir-thaddeus-tray.ico"));

            _trayIcon = new TrayIcon
            {
                ToolTipText = "Sir Thaddeus",
                Icon = new WindowIcon(stream),
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => ShowMainWindow();

            var menu = new NativeMenu();
            var openItem = new NativeMenuItem("Open Sir Thaddeus");
            openItem.Click += (_, _) => ShowMainWindow();

            var hideItem = new NativeMenuItem("Hide");
            hideItem.Click += (_, _) => HideMainWindow();

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) => RequestShutdown();

            menu.Add(openItem);
            menu.Add(hideItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);
            _trayIcon.Menu = menu;

            var icons = new TrayIcons();
            icons.Add(_trayIcon);
            SetValue(TrayIcon.IconsProperty, icons);

            mainWindow.Closing += MainWindow_Closing;
            mainWindow.PropertyChanged += MainWindow_PropertyChanged;
            mainWindow.ConfigureTrayUi(trayAvailable: true, minimizeToTrayEnabled: MinimizeToTrayEnabled);
        }
        catch
        {
            DisposeTray();
            MinimizeToTrayEnabled = false;
            mainWindow.ConfigureTrayUi(trayAvailable: false, minimizeToTrayEnabled: false);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting || sender is not Window window || !ShouldMinimizeToTray())
        {
            return;
        }

        e.Cancel = true;
        window.Hide();
    }

    private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isExiting || sender is not Window window || e.Property != Window.WindowStateProperty)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized && ShouldMinimizeToTray())
        {
            window.Hide();
        }
    }

    private bool ShouldMinimizeToTray() => MinimizeToTrayEnabled && _trayIcon is not null;

    private void ShowMainWindow()
    {
        if (_desktopLifetime?.MainWindow is not Window window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void HideMainWindow()
    {
        if (_desktopLifetime?.MainWindow is not Window window)
        {
            return;
        }

        window.Hide();
    }

    public void RequestShutdown()
    {
        _isExiting = true;
        _desktopLifetime?.Shutdown();
    }

    private void DisposeTray()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
        SetValue(TrayIcon.IconsProperty, null);
    }
}

