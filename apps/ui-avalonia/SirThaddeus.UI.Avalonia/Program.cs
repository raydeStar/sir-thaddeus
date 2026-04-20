using Avalonia;
using Serilog;
using SirThaddeus.Config;
using SirThaddeus.Diagnostics;
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

        // ---------------------------------------------------------------
        // Sir Thaddeus v2 hybrid shell (Phase 1, 2026) supersedes this UI.
        // The Avalonia desktop client is retained for the v1 harness and
        // legacy sprint workflows, but new feature work should target the
        // hybrid runtime: src/Thaddeus.Runtime + web/. See docs/packaging.md.
        // ---------------------------------------------------------------
        Log.Warning(
            "ui-avalonia is the legacy v1 shell; new features live in the hybrid runtime (Thaddeus.Runtime + web/).");

        try
        {
            // Run startup diagnostics before Avalonia takes over. A user with a
            // broken LM Studio should see "LLM unreachable" in the log before
            // the chat window opens and swallows their first prompt.
            RunStartupDiagnostics();

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

    private static void RunStartupDiagnostics()
    {
        try
        {
            var load = SettingsManager.LoadWithDiagnostics();
            var report = StartupDiagnostics.RunAsync(load.Settings).GetAwaiter().GetResult();
            foreach (var check in report.Checks)
            {
                switch (check.Status)
                {
                    case StartupCheckStatus.Ok:
                        Log.Information("[startup] {Check}: ok — {Message}", check.Name, check.Message);
                        break;
                    case StartupCheckStatus.Skipped:
                        Log.Debug("[startup] {Check}: skipped — {Message}", check.Name, check.Message);
                        break;
                    case StartupCheckStatus.Warning:
                        Log.Warning(check.Exception, "[startup] {Check}: warning — {Message}", check.Name, check.Message);
                        break;
                    case StartupCheckStatus.Failed:
                        Log.Error(check.Exception, "[startup] {Check}: failed — {Message}", check.Name, check.Message);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // Diagnostics must never break startup.
            Log.Warning(ex, "Startup diagnostics did not complete");
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
