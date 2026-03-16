using Avalonia;
using System;

namespace SirThaddeus.UI.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            var appArgs = AppStartupOptions.Initialize(args);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(appArgs);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("crash.log", ex.ToString());
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
