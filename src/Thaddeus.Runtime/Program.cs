using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using Thaddeus.Runtime.Api;
using Thaddeus.Runtime.Activity;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Routines;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.Hosting;
using Thaddeus.Runtime.Ipc;
using Thaddeus.Runtime.Memory;
using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.State;
using Thaddeus.Runtime.Tools;
using Thaddeus.Runtime.Voice;
using Thaddeus.Runtime.Wiki;
using Thaddeus.Runtime.Ws;
using Thaddeus.SharedTypes;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.RuntimeHost;
using SirThaddeus.Wiki;
using SirThaddeus.Wiki.Storage;

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
        var lockFilePath = parsed.LockFilePath ?? LockFileService.GetDefaultPath();
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
            builder.Services.AddSingleton<StubSpeechToTextProvider>();
            builder.Services.AddSingleton<WhisperCppSpeechToTextProvider>(sp =>
                new WhisperCppSpeechToTextProvider(
                    sp.GetRequiredService<WhisperCppOptions>(),
                    sp.GetRequiredService<IExternalProcessRunner>(),
                    sp.GetRequiredService<ILogger<WhisperCppSpeechToTextProvider>>()));
            builder.Services.AddSingleton<IAudioPlayer, DefaultAudioPlayer>();
            builder.Services.AddSingleton<PiperOptions>(_ =>
                builder.Configuration.GetSection("Voice:Tts").Get<PiperOptions>() ?? new PiperOptions());
            builder.Services.AddSingleton<StubTextToSpeechProvider>();
            builder.Services.AddSingleton<PiperTextToSpeechProvider>(sp =>
                new PiperTextToSpeechProvider(
                    sp.GetRequiredService<PiperOptions>(),
                    sp.GetRequiredService<IExternalProcessRunner>(),
                    sp.GetRequiredService<IAudioPlayer>(),
                    sp.GetRequiredService<ILogger<PiperTextToSpeechProvider>>()));
            builder.Services.AddSingleton<ISpeechToTextProvider, SettingsDrivenSpeechToTextProvider>();
            builder.Services.AddSingleton<ITextToSpeechProvider, SettingsDrivenTextToSpeechProvider>();
            builder.Services.AddSingleton<VoiceModeController>();
            builder.Services.AddSingleton<VoiceHostProcessSupervisor>();
            builder.Services.AddSingleton<VoiceRuntimeStatusService>();
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
            builder.Services.AddSingleton(sp =>
            {
                var lockDir = Path.GetDirectoryName(options.LockFilePath)!;
                var turnsDir = builder.Configuration.GetValue<string>("Chat:TurnsDirectory")
                    ?? Path.Combine(lockDir, "turns");
                return new TurnTraceWriter(
                    sp.GetRequiredService<IEventBus>(),
                    sp.GetRequiredService<ILogger<TurnTraceWriter>>(),
                    turnsDir);
            });
            builder.Services.AddSingleton<StubAssistant>();
            builder.Services.AddSingleton<IAssistant, AssistantRouter>();
            builder.Services.AddSingleton<IActivityLog>(_ => new InMemoryActivityLog(capacity: 500));
            // The SQLite-backed semantic memory store. Previously this was
            // instantiated ad-hoc by the MCP server and the headless host —
            // the desktop runtime had no DI registration, so /api/memory
            // had nothing to talk to. Wire it once here; reuse the same env
            // var the MCP child reads so all three surfaces hit the same DB.
            // Manual-trigger reflection pass over the semantic memory.
            // Dedupes facts whose normalized triple matches. No automatic
            // scheduling in v1 — invoked from the audit UI button.
            builder.Services.AddSingleton<Thaddeus.Runtime.Memory.MemoryReflectionService>();
            builder.Services.AddSingleton<SirThaddeus.Memory.IMemoryStore>(sp =>
            {
                var dbPath = RuntimeMcpEnvironmentBuilder.ResolveMemoryDbPathFromEnvironment();
                var dbDir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrWhiteSpace(dbDir))
                    Directory.CreateDirectory(dbDir);
                var store = new SirThaddeus.Memory.Sqlite.SqliteMemoryStore(dbPath);
                // Block briefly so the first API request doesn't race the
                // schema-init and 500. EnsureSchemaAsync is idempotent and
                // cheap (a handful of CREATE-IF-NOT-EXISTS statements);
                // running it synchronously on the DI factory keeps the
                // contract simple — every store handed out is ready to use.
                store.EnsureSchemaAsync().GetAwaiter().GetResult();
                return store;
            });
            builder.Services.AddSingleton<ISettingsStore>(sp =>
            {
                var lockDir = Path.GetDirectoryName(options.LockFilePath)!;
                var settingsPath = builder.Configuration.GetValue<string>("Settings:FilePath")
                    ?? Path.Combine(lockDir, "runtime-settings.json");
                return new JsonFileSettingsStore(
                    settingsPath,
                    sp.GetRequiredService<ILogger<JsonFileSettingsStore>>());
            });
            builder.Services.AddSingleton<IMemoStore>(sp =>
            {
                var lockDir = Path.GetDirectoryName(options.LockFilePath)!;
                var memosDir = builder.Configuration.GetValue<string>("Memory:Directory")
                    ?? Path.Combine(lockDir, "memos");
                return new JsonFileMemoStore(
                    memosDir,
                    sp.GetRequiredService<ILogger<JsonFileMemoStore>>());
            });
            builder.Services.AddSingleton<IRoutineStore>(sp =>
            {
                var lockDir = Path.GetDirectoryName(options.LockFilePath)!;
                var dir = builder.Configuration.GetValue<string>("Routines:Directory")
                    ?? Path.Combine(lockDir, "routines");
                return new JsonFileRoutineStore(
                    dir,
                    sp.GetRequiredService<ILogger<JsonFileRoutineStore>>());
            });
            builder.Services.AddSingleton<IWikiStore>(sp =>
            {
                var libraryDir = builder.Configuration.GetValue<string>("Wiki:LibraryDirectory");
                if (string.IsNullOrWhiteSpace(libraryDir))
                {
                    // Test mode must never share storage with the user's real wiki —
                    // otherwise pages created via the API in one Playwright run
                    // accumulate across runs and cause strict-mode locator collisions.
                    // Scope the directory to the lock file so concurrent or
                    // sequential test runtimes get independent sandboxes.
                    if (options.TestMode)
                    {
                        var lockDir2 = Path.GetDirectoryName(options.LockFilePath)!;
                        var lockName = Path.GetFileNameWithoutExtension(options.LockFilePath);
                        libraryDir = Path.Combine(lockDir2, $"{lockName}-wiki");
                    }
                    else
                    {
                        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                        libraryDir = string.IsNullOrWhiteSpace(documents)
                            ? Path.Combine(Path.GetDirectoryName(options.LockFilePath)!, "wiki-library")
                            : Path.Combine(documents, "Sir Thaddeus Wiki");
                    }
                }

                return new LocalWikiStore(
                    libraryDir,
                    sp.GetRequiredService<ILogger<LocalWikiStore>>());
            });
                builder.Services.AddSingleton<WikiChatContextService>();
                builder.Services.AddSingleton<WikiPageRetrieverService>();
                builder.Services.AddSingleton<WikiPageAssistantService>();
            builder.Services.AddHostedService<RoutineSeeder>();
            // MCP tool client. Spawns the SirThaddeus.McpServer child process,
            // handshakes asynchronously, and exposes IMcpToolClient to the
            // assistant. Registered as singleton + hosted so DI consumers get
            // the same instance and shutdown disposes the child cleanly.
            var auditPath = Path.Combine(
                Path.GetDirectoryName(options.LockFilePath)!, "logs", "audit.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            builder.Services.AddSingleton<IAuditLogger>(_ => new JsonLineAuditLogger(auditPath));
            builder.Services.AddSingleton<McpClientHost>();
            builder.Services.AddSingleton<IMcpToolClient>(sp => sp.GetRequiredService<McpClientHost>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<McpClientHost>());
            builder.Services.AddSingleton<RuntimeStopAllService>();
            builder.Services.AddSingleton<VoicePttEventHub>();

            // Gate that wraps every MCP call with the user's permission policy.
            builder.Services.AddSingleton<ToolPermissionGate>();

            // One-shot, idempotent migrator that copies any legacy memos
            // (JSON files in the legacy memo directory) into the user's wiki. Runs
            // once at startup, writes a sentinel, and never touches the
            // source files. See MemosToWikiMigrator for the safety
            // contract. The sentinel keeps it harmless for installs that
            // have already migrated.
            builder.Services.AddHostedService<Thaddeus.Runtime.Memory.MemosToWikiMigrator>();

            builder.Services.AddHostedService<ActivityEventBridge>();
            builder.Services.AddHostedService<StateMachineEventBridge>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<WebSocketBroadcaster>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<TurnTraceWriter>());
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
            app.MapTurnsApi();
            app.MapRuntimeLogsApi();
            app.MapFilesApi();
            app.MapSettingsApi();
            app.MapMemoryAuditApi();
            app.MapRoutinesApi();
            app.MapWikiApi();
            app.MapAudioApi();
            app.MapVoiceApi();
            app.MapPermissionsApi();
            app.MapHarnessApi();
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
    internal sealed record StartupArgs(bool TestMode, int? ParentPid, string? LockFilePath)
    {
        public static StartupArgs Parse(string[] args)
        {
            var testMode = false;
            int? parentPid = null;
            string? lockFilePath = null;
            foreach (var a in args)
            {
                if (a.Equals("--test-mode", StringComparison.OrdinalIgnoreCase)) testMode = true;
                else if (a.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(a["--parent-pid=".Length..], out var pid))
                {
                    parentPid = pid;
                }
                else if (a.StartsWith("--lock-file=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = a["--lock-file=".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        lockFilePath = Path.GetFullPath(value);
                    }
                }
            }
            return new StartupArgs(testMode, parentPid, lockFilePath);
        }
    }
}
