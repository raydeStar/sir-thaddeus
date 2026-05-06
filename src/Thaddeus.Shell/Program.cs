using Microsoft.Extensions.Logging;
using System.Text.Json;
using Thaddeus.Shell.Ipc;
using Thaddeus.Shell.Platform;
using Thaddeus.Shell.Platform.Windows;
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
        var tray = OperatingSystem.IsWindows()
            ? (ITrayAdapter)new WindowsTrayAdapter(loggerFactory.CreateLogger<WindowsTrayAdapter>())
            : new StubTrayAdapter(loggerFactory.CreateLogger<StubTrayAdapter>());

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
            var compactUrl = $"http://127.0.0.1:{lockFile.Port}/compact";
            var window = new WorkspaceWindow(loggerFactory.CreateLogger<WorkspaceWindow>());
            ShellSessionController? shellSession = null;

            // When the supervised runtime process exits — whether from the web
            // "kill app" button, a crash, or a tray Stop All — pull the shell
            // window down with it. Without this the workspace stays open
            // pointing at a dead backend.
            supervisor.RuntimeExited += (_, _) =>
            {
                try { shellSession?.ExitAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { log.LogWarning(ex, "shell.runtime_exit_handler_failed"); }

                // Safety net: if the workspace window does not actually
                // tear down within a couple of seconds (e.g. Photino's
                // message loop is blocked, the close was suppressed, or we
                // never wired the supervisor at all), force the whole
                // shell process to exit. The runtime is already gone, so
                // there's nothing useful to keep alive.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    log.LogWarning("shell.runtime_exit.force_exit");
                    Environment.Exit(0);
                });
            };

            // Phase 2.4 builds the compact panel launcher; Phase 2.5 wires it to a
            // real Windows global shortcut (Ctrl+Shift+Space → toggle). Other OSes
            // fall back to the stub adapter and the env-var smoke-test path.
            CompactPanelLauncher? compactLauncher = null;
            var autoShowCompact = string.Equals(
                Environment.GetEnvironmentVariable("THADDEUS_COMPACT_AUTOSHOW"),
                "1",
                StringComparison.Ordinal);
            var startMinimized = string.Equals(
                Environment.GetEnvironmentVariable("THADDEUS_START_MINIMIZED"),
                "1",
                StringComparison.Ordinal);

            async Task RequestStopAllAsync()
            {
                try
                {
                    using var resp = await RequestRuntimePostAsync(
                        lockFile.Port,
                        lockFile.Token,
                        "/api/stop-all",
                        TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    log.LogInformation("shell.stop_all status={Status}", (int)resp.StatusCode);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "shell.stop_all.failed");
                }
            }

            async Task RequestPushToTalkAsync(string phase)
            {
                try
                {
                    using var resp = await RequestRuntimePostAsync(
                        lockFile.Port,
                        lockFile.Token,
                        $"/api/voice/ptt/{phase}",
                        TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    log.LogInformation("shell.ptt phase={Phase} status={Status}", phase, (int)resp.StatusCode);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "shell.ptt.failed phase={Phase}", phase);
                }
            }

            IGlobalShortcutAdapter shortcuts = OperatingSystem.IsWindows()
                ? new Thaddeus.Shell.Platform.Windows.WindowsGlobalShortcutAdapter(
                    loggerFactory.CreateLogger<Thaddeus.Shell.Platform.Windows.WindowsGlobalShortcutAdapter>())
                : new StubGlobalShortcutAdapter(loggerFactory.CreateLogger<StubGlobalShortcutAdapter>());
            using var _shortcuts = shortcuts;

            window.ShowBlocking(workspaceUrl, lockFile.Version, onReady: parent =>
            {
                var surface = new PhotinoCompactWindowSurface(
                    parent,
                    loggerFactory.CreateLogger<PhotinoCompactWindowSurface>());
                compactLauncher = new CompactPanelLauncher(
                    surface,
                    loggerFactory.CreateLogger<CompactPanelLauncher>());
                shellSession = new ShellSessionController(
                    window,
                    tray,
                    RequestStopAllAsync,
                    loggerFactory.CreateLogger<ShellSessionController>(),
                    closeCompactWindow: () => compactLauncher?.Close());
                window.ClosingRequested += shellSession.HandleWorkspaceClosing;
                using var trayCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                shellSession.InitializeAsync(startMinimized, trayCts.Token).GetAwaiter().GetResult();

                if (autoShowCompact)
                {
                    log.LogInformation("shell.compact.auto_show");
                    compactLauncher.Show(compactUrl);
                }

                if (shortcuts.IsSupported)
                {
                    shortcuts.Triggered += (_, id) =>
                    {
                        if (id == "compact-toggle")
                        {
                            try { compactLauncher?.Toggle(compactUrl); }
                            catch (Exception ex) { log.LogWarning(ex, "shell.compact.toggle_failed"); }
                        }
                        else if (id == "stop-all")
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await RequestStopAllAsync().ConfigureAwait(false); }
                                catch (Exception ex) { log.LogWarning(ex, "shell.stop_all.failed"); }
                            });
                        }
                        else if (id == "push-to-talk")
                        {
                            _ = RequestPushToTalkAsync("down");
                        }
                    };
                    shortcuts.Released += (_, id) =>
                    {
                        if (id == "push-to-talk")
                        {
                            _ = RequestPushToTalkAsync("up");
                        }
                    };
                    using var registerCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var compactOk = shortcuts.RegisterAsync(
                        "compact-toggle",
                        new KeyChord("Space", KeyModifiers.Control | KeyModifiers.Shift),
                        registerCts.Token).GetAwaiter().GetResult();
                    log.LogInformation("shell.shortcut.register id=compact-toggle ok={Ok}", compactOk);
                    var stopOk = shortcuts.RegisterAsync(
                        "stop-all",
                        new KeyChord("Escape", KeyModifiers.Control | KeyModifiers.Alt),
                        registerCts.Token).GetAwaiter().GetResult();
                    log.LogInformation("shell.shortcut.register id=stop-all ok={Ok}", stopOk);
                    var pttChord = ResolvePushToTalkChordAsync(lockFile.Port, lockFile.Token, log)
                        .GetAwaiter().GetResult();
                    if (SameChord(pttChord, new KeyChord("Space", KeyModifiers.Control | KeyModifiers.Shift)) ||
                        SameChord(pttChord, new KeyChord("Space", KeyModifiers.Control | KeyModifiers.Alt)))
                        pttChord = new KeyChord("M", KeyModifiers.Control | KeyModifiers.Alt);
                    var pttOk = shortcuts.RegisterAsync(
                        "push-to-talk",
                        pttChord,
                        registerCts.Token).GetAwaiter().GetResult();
                    log.LogInformation("shell.shortcut.register id=push-to-talk chord={Chord} ok={Ok}", pttChord, pttOk);
                }
            });

            try { compactLauncher?.Close(); } catch { /* drain */ }

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
            try { tray.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* drain */ }
            try { ipc.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* drain */ }
            try { supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* drain */ }
        }
    }

    private static async Task<HttpResponseMessage> RequestRuntimePostAsync(
        int port,
        string token,
        string path,
        TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = timeout };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}");
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<KeyChord> ResolvePushToTalkChordAsync(int port, string token, ILogger log)
    {
        var fallback = new KeyChord("M", KeyModifiers.Control | KeyModifiers.Alt);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/settings");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return fallback;

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("shortcuts", out var shortcuts) &&
                shortcuts.TryGetProperty("pushToTalk", out var pushToTalk) &&
                pushToTalk.ValueKind == JsonValueKind.String &&
                TryParseShortcut(pushToTalk.GetString(), out var chord))
            {
                return chord;
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "shell.shortcut.ptt_settings_failed");
        }

        return fallback;
    }

    private static bool TryParseShortcut(string? value, out KeyChord chord)
    {
        chord = new KeyChord("M", KeyModifiers.Control | KeyModifiers.Alt);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var modifiers = KeyModifiers.None;
        string? key = null;
        foreach (var part in parts)
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                case "alt":
                case "option":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "super":
                case "win":
                case "windows":
                case "cmd":
                case "command":
                    modifiers |= KeyModifiers.Super;
                    break;
                default:
                    key = NormalizeShortcutKey(part);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(key))
            return false;

        chord = new KeyChord(key, modifiers);
        return true;
    }

    private static string NormalizeShortcutKey(string key)
    {
        var trimmed = key.Trim();
        if (string.Equals(trimmed, "Esc", StringComparison.OrdinalIgnoreCase)) return "Escape";
        if (trimmed.Length == 1) return trimmed.ToUpperInvariant();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static bool SameChord(KeyChord left, KeyChord right) =>
        string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase) && left.Modifiers == right.Modifiers;

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
