using Avalonia;
using System;

namespace SirThaddeus.UI.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var appArgs = AppStartupOptions.Initialize(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(appArgs);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
