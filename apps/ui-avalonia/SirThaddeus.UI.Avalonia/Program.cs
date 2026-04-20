using Avalonia;
using Serilog;
using SirThaddeus.Logging;
using System;

namespace SirThaddeus.UI.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Log.Logger = LoggingBootstrap.BuildSerilogLogger(new LoggingOptions
        {
            ComponentName = "ui-avalonia",
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();

        try
        {
            var appArgs = AppStartupOptions.Initialize(args);
            Log.Information("UI starting (args={ArgCount})", appArgs.Length);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(appArgs);
        }
        catch (Exception ex)
        {
            // Log.Fatal goes through the same rolling file + console sinks as
            // everything else, so a crash shows up in the standard log path
            // rather than a stray crash.log next to whatever cwd was at launch.
            Log.Fatal(ex, "UI crashed during startup");
            Log.CloseAndFlush();
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
