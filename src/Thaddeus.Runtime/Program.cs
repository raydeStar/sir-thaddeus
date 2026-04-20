using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using Thaddeus.Runtime.Api;
using Thaddeus.Runtime.Activity;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.Hosting;
using Thaddeus.Runtime.Ipc;
using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.State;
using Thaddeus.Runtime.Voice;
using Thaddeus.Runtime.Ws;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime;

/// <summary>
/// Entry point for the runtime host (<c>Thaddeus.Runtime</c>). Sets up Kestrel on an
/// ephemeral loopback port, registers state/event/IPC services, writes the lock file,
/// and runs until shutdown is requested via IPC or signal.
/// </summary>
public static class Program
{
    /// <summary>Application entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        var parsed = StartupArgs.Parse(args);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var bearerToken = TokenGenerator.NewToken();
        var lockFilePath = LockFileService.GetDefaultPath();
        var ipcEndpoint = IpcEndpoint.GetDefault();
        var startedAt = DateTimeOffset.UtcNow;

        // Note: port is filled in below once Kestrel binds.
        var options = new RuntimeOptions
        {
            BearerToken = bearerToken,
            Pid = Environment.ProcessId,
            Version = version,
            LockFilePath = lockFilePath,
            IpcEndpoint = ipcEndpoint,
            StartedAt = startedAt,
            TestMode = parsed.TestMode,
            ParentPid = parsed.ParentPid,
        };

        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(lockFilePath)!, "logs"));
        var logsDir = Path.Combine(Path.GetDirectoryName(lockFilePath)!, "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logsDir, "thaddeus-runtime-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Host.UseSerilog();

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                // Ephemeral loopback port; spec §6.1.
                serverOptions.Listen(IPAddress.Loopback, 0);
                serverOptions.AddServerHeader = false;
                serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
            });

            // Singleton runtime services. Options are mutated below to record the bound port.
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<AuthFailureTracker>(_ => new AuthFailureTracker());
            builder.Services.AddSingleton<RuntimeStateMachine>();
            builder.Services.AddSingleton<StateSnapshot>();
            builder.Services.AddSingleton<IEventBus, EventBus>();
            builder.Services.AddSingleton<WebSocketBroadcaster>();
            // Voice providers default to stubs in Phase 2.1; real adapters arrive
            // in Phase 2.2 (whisper.cpp) and Phase 2.3 (Piper) and override these.
            builder.Services.AddSingleton<IExternalProcessRunner, DefaultExternalProcessRunner>();
            builder.Services.AddSingleton<WhisperCppOptions>(_ =>
                builder.Configuration.GetSection("Voice:Stt").Get<WhisperCppOptions>() ?? new WhisperCppOptions());
            builder.Services.AddSingleton<ISpeechToTextProvider>(sp =>
            {
                var opts = sp.GetRequiredService<WhisperCppOptions>();
                if (string.IsNullOrEmpty(opts.BinaryPath) || string.IsNullOrEmpty(opts.ModelPath))
                {
                    return new StubSpeechToTextProvider();
                }
                return new WhisperCppSpeechToTextProvider(
                    opts,
                    sp.GetRequiredService<IExternalProcessRunner>(),
                    sp.GetRequiredService<ILogger<WhisperCppSpeechToTextProvider>>());
            });
            builder.Services.AddSingleton<ITextToSpeechProvider>(sp =>
            {
                var opts = sp.GetRequiredService<PiperOptions>();
                var player = sp.GetRequiredService<IAudioPlayer>();
                if (string.IsNullOrEmpty(opts.BinaryPath) || string.IsNullOrEmpty(opts.VoiceModelPath) || !player.IsAvailable)
                {
                    return new StubTextToSpeechProvider();
                }
                return new PiperTextToSpeechProvider(
                    opts,
                    sp.GetRequiredService<IExternalProcessRunner>(),
                    player,
                    sp.GetRequiredService<ILogger<PiperTextToSpeechProvider>>());
            });
            builder.Services.AddSingleton<IAudioPlayer, DefaultAudioPlayer>();
            builder.Services.AddSingleton<PiperOptions>(_ =>
                builder.Configuration.GetSection("Voice:Tts").Get<PiperOptions>() ?? new PiperOptions());
            builder.Services.AddSingleton<VoiceModeController>();
            builder.Services.AddSingleton<IThreadStore>(sp =>
            {
                var lockDir = Path.GetDirectoryName(options.LockFilePath)!;
                var threadsDir = builder.Configuration.GetValue<string>("Chat:ThreadsDirectory")
                    ?? Path.Combine(lockDir, "threads");
                return new JsonFileThreadStore(
                    threadsDir,
                    sp.GetRequiredService<ILogger<JsonFileThreadStore>>());
            });
            builder.Services.AddSingleton<ChatTurnPublisher>();
            builder.Services.AddSingleton<StubAssistant>();
            builder.Services.AddSingleton<IActivityLog>(_ => new InMemoryActivityLog(capacity: 500));
            builder.Services.AddSingleton<ISettingsStore>(sp =>
            {
                var lockDir = Path.GetDirectoryName(options.LockFilePath)!;
                var settingsPath = builder.Configuration.GetValue<string>("Settings:FilePath")
                    ?? Path.Combine(lockDir, "runtime-settings.json");
                return new JsonFileSettingsStore(
                    settingsPath,
                    sp.GetRequiredService<ILogger<JsonFileSettingsStore>>());
            });
            builder.Services.AddHostedService<ActivityEventBridge>();
            builder.Services.AddHostedService<StateMachineEventBridge>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<WebSocketBroadcaster>());
            builder.Services.AddHostedService<IpcServer>();
            builder.Services.AddHostedService<ParentProcessWatcher>();

            var app = builder.Build();

            // Resolve the bound port and update options + write lock file.
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var addresses = app.Urls;
                var port = ExtractLoopbackPort(addresses);
                var bound = options with { Port = port };
                // Replace the singleton's internal port via a fresh write-through copy.
                ApplyBoundPort(app.Services, bound);

                var lockContents = new RuntimeLockFile
                {
                    Pid = bound.Pid,
                    Port = bound.Port,
                    Token = bound.BearerToken,
                    Version = bound.Version,
                    IpcEndpoint = bound.IpcEndpoint,
                    StartedAt = bound.StartedAt,
                    SidecarPids = Array.Empty<int>(),
                };
                LockFileService.Write(bound.LockFilePath, lockContents);

                Log.Information("runtime.ready port={Port} pid={Pid} version={Version} testMode={TestMode}",
                    bound.Port, bound.Pid, bound.Version, bound.TestMode);
            });

            app.Lifetime.ApplicationStopping.Register(() =>
            {
                LockFileService.TryDelete(options.LockFilePath);
                Log.Information("runtime.stopping pid={Pid}", options.Pid);
            });

            // Bearer auth must come before any API/WS routing. Static-asset and bootstrap
            // paths are exempt inside the middleware itself.
            app.UseMiddleware<RuntimeBearerAuthMiddleware>();

            // WebSocket upgrade endpoint with origin check.
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });

            app.MapGet("/ws", async (HttpContext ctx, WebSocketBroadcaster broadcaster) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var origin = ctx.Request.Headers.Origin.ToString();
                if (!IsAllowedOrigin(origin, ctx))
                {
                    Log.Warning("ws.origin_rejected origin={Origin}", origin);
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await broadcaster.HandleConnectionAsync(ctx, socket, ctx.RequestAborted).ConfigureAwait(false);
            });

            app.MapRuntimeApi();
            app.MapChatApi();
            app.MapActivityApi();
            app.MapSettingsApi();
            app.MapWorkspaceHosting();

            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "runtime.fatal");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static int ExtractLoopbackPort(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Port > 0)
            {
                return u.Port;
            }
        }
        return 0;
    }

    private static void ApplyBoundPort(IServiceProvider services, RuntimeOptions bound)
    {
        // Replace the singleton via reflection on the held instance. A simpler
        // alternative would be a mutable holder, but RuntimeOptions stays an
        // immutable record by holding the new instance and copying its properties.
        var existing = services.GetRequiredService<RuntimeOptions>();
        typeof(RuntimeOptions)
            .GetProperty(nameof(RuntimeOptions.Port))!
            .SetValue(existing, bound.Port);
    }

    private static bool IsAllowedOrigin(string origin, HttpContext ctx)
    {
        // Spec §6.3: only accept loopback origins matching our port, or null/empty
        // (which Photino sometimes presents).
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        if (!IPAddress.IsLoopback(IPAddress.TryParse(u.Host, out var ip) ? ip : IPAddress.None)
            && !string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var localPort = ctx.Connection.LocalPort;
        return u.Port == localPort;
    }

    /// <summary>Parses CLI arguments. Currently very small.</summary>
    internal sealed record StartupArgs(bool TestMode, int? ParentPid)
    {
        public static StartupArgs Parse(string[] args)
        {
            var testMode = false;
            int? parentPid = null;
            foreach (var a in args)
            {
                if (a.Equals("--test-mode", StringComparison.OrdinalIgnoreCase)) testMode = true;
                else if (a.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(a["--parent-pid=".Length..], out var pid))
                {
                    parentPid = pid;
                }
            }
            return new StartupArgs(testMode, parentPid);
        }
    }
}
