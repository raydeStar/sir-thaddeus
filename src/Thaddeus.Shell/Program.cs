using Microsoft.Extensions.Logging;
using Thaddeus.Shell.Ipc;
using Thaddeus.Shell.Platform;
using Thaddeus.Shell.Runtime;
using Thaddeus.Shell.Windows;

namespace Thaddeus.Shell;

/// <summary>
/// Entry point for the shell. Spawns or attaches to the runtime, opens the workspace
/// window pointing at it, and shuts the runtime down cleanly when the window closes.
/// Phase 1 deliberately avoids tray, shortcuts, and the compact panel — those land
/// in Phase 2.
/// </summary>
public static class Program
{
    /// <summary>Application entry point.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
            b.AddDebug();
            b.SetMinimumLevel(LogLevel.Information);
        });
        var log = loggerFactory.CreateLogger("Shell");

        var supervisor = new RuntimeProcessSupervisor(loggerFactory.CreateLogger<RuntimeProcessSupervisor>());
        var ipc = new IpcClient(loggerFactory.CreateLogger<IpcClient>());

        try
        {
            using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var lockFile = supervisor.EnsureRunningAsync(startupCts.Token).GetAwaiter().GetResult();

            try
            {
                ipc.ConnectAndHandshakeAsync(lockFile.IpcEndpoint!, startupCts.Token).GetAwaiter().GetResult();
            }
            catch (IpcVersionMismatchException ex)
            {
                log.LogCritical(ex, "shell.version_mismatch");
                ShowFatalDialog(
                    "Sir Thaddeus version mismatch",
                    $"This shell cannot talk to the installed runtime.\n\n{ex.Message}\n\nPlease reinstall or update so the shell and runtime versions match.");
                return 2;
            }
            catch (Exception ex)
            {
                log.LogCritical(ex, "shell.ipc_handshake_failed");
                ShowFatalDialog(
                    "Sir Thaddeus could not connect to its runtime",
                    $"The runtime started but did not respond to the shell handshake.\n\n{ex.Message}");
                return 3;
            }

            var workspaceUrl = $"http://127.0.0.1:{lockFile.Port}/";
            var window = new WorkspaceWindow(loggerFactory.CreateLogger<WorkspaceWindow>());
            window.ShowBlocking(workspaceUrl, lockFile.Version);

            // Window closed → tell the runtime to shut down.
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ipc.RequestShutdownAsync(shutdownCts.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "shell.fatal");
            ShowFatalDialog("Sir Thaddeus failed to start", ex.Message);
            return 1;
        }
        finally
        {
            try { ipc.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* drain */ }
            try { supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* drain */ }
        }
    }

    private static void ShowFatalDialog(string title, string message)
    {
        // Phase 1 keeps this minimal: console + a single Photino info dialog, falling
        // back to console-only if the GUI cannot be reached. Real platform dialogs
        // arrive with the platform adapter implementations in later phases.
        Console.Error.WriteLine($"[{title}] {message}");
        try
        {
            new Photino.NET.PhotinoWindow()
                .SetTitle(title)
                .SetSize(520, 220)
                .SetResizable(false)
                .Load($"data:text/html;charset=utf-8,{Uri.EscapeDataString(BuildFatalHtml(title, message))}")
                .WaitForClose();
        }
        catch
        {
            // The console message above is the fallback.
        }
    }

    private static string BuildFatalHtml(string title, string message) =>
        $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>{{System.Net.WebUtility.HtmlEncode(title)}}</title>
        <style>body { font-family: -apple-system, Segoe UI, sans-serif; padding: 1.5rem; color: #1f2937; }
        h1 { font-size: 1.1rem; margin: 0 0 0.5rem; color: #b91c1c; }
        p { margin: 0; white-space: pre-wrap; }</style></head>
        <body><h1>{{System.Net.WebUtility.HtmlEncode(title)}}</h1>
        <p>{{System.Net.WebUtility.HtmlEncode(message)}}</p></body></html>
        """;
}
