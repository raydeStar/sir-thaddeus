using System.ComponentModel;
using System.IO;
using System.Windows;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Core;
using SirThaddeus.DesktopRuntime.Services;
using SirThaddeus.DesktopRuntime.ViewModels;
using SirThaddeus.Invocation;
using SirThaddeus.LlmClient;
using SirThaddeus.LocalTools.Playwright;
using SirThaddeus.PermissionBroker;
using SirThaddeus.Memory.Sqlite;
using SirThaddeus.ToolRunner;
using SirThaddeus.ToolRunner.Tools;

namespace SirThaddeus.DesktopRuntime;

/// <summary>
/// Composition root. Loads config, spawns the MCP server, creates the LLM client,
/// builds the agent orchestrator, and wires everything to the UI surface.
/// </summary>
public partial class App : System.Windows.Application
{
    // ─────────────────────────────────────────────────────────────────────
    // Infrastructure
    // ─────────────────────────────────────────────────────────────────────

    private AppSettings? _settings;
    private JsonLineAuditLogger? _auditLogger;
    private RuntimeController? _runtimeController;
    private InMemoryPermissionBroker? _permissionBroker;
    private EnforcingToolRunner? _toolRunner;
    private PlaywrightBrowserNavigateTool? _playwrightTool;

    // ─────────────────────────────────────────────────────────────────────
    // Layer 3 + 4: LLM Client & MCP Tool Server
    // ─────────────────────────────────────────────────────────────────────

    private LmStudioClient? _llmClient;
    private McpProcessClient? _mcpClient;
    private AuditedMcpToolClient? _auditedMcpClient;
    private WpfPermissionGate? _permissionGate;

    // ─────────────────────────────────────────────────────────────────────
    // Layer 2: Agent Orchestrator
    // ─────────────────────────────────────────────────────────────────────

    private AgentOrchestrator? _orchestrator;
    private IDialogueStatePersistence? _dialogueStatePersistence;

    // ─────────────────────────────────────────────────────────────────────
    // Layer 1: UI Surface & Audio
    // ─────────────────────────────────────────────────────────────────────

    private GlobalHotkeyService? _hotkeyService;
    private Window? _hotkeyOwnerWindow;
    private PushToTalkService? _pttService;
    private TextToSpeechService? _ttsService;
    private TrayIconService? _trayIcon;
    private MainWindow? _overlayWindow;
    private OverlayViewModel? _overlayViewModel;
    private CommandPaletteWindow? _commandPaletteWindow;
    private int _commandPaletteHotkeyId = -1;
    private bool _isShuttingDown;
    private bool _isHeadless;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Parse CLI args ───────────────────────────────────────────
        _isHeadless = e.Args.Any(a =>
            a.Equals("--headless", StringComparison.OrdinalIgnoreCase));

        // ── 1. Load config ───────────────────────────────────────────
        _settings = SettingsManager.Load();

        // ── 2. Core infrastructure ───────────────────────────────────
        _auditLogger = JsonLineAuditLogger.CreateDefault();
        _runtimeController = new RuntimeController(_auditLogger, AssistantState.Idle);
        _permissionBroker = new InMemoryPermissionBroker(_auditLogger);
        _toolRunner = new EnforcingToolRunner(_permissionBroker, _auditLogger);
        RegisterTools(_toolRunner);
        _runtimeController.StateChanged += OnRuntimeStateChanged;

        LogStartup();

        // ── 3. Create LLM client (Layer 3) ──────────────────────────
        var llmOptions = new LlmClientOptions
        {
            BaseUrl = _settings.Llm.BaseUrl,
            Model = _settings.Llm.Model,
            MaxTokens = _settings.Llm.MaxTokens,
            Temperature = _settings.Llm.Temperature
        };
        _llmClient = new LmStudioClient(llmOptions);

        _auditLogger.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "LLM_CLIENT_CREATED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["baseUrl"] = _settings.Llm.BaseUrl,
                ["model"] = _settings.Llm.Model
            }
        });

        // ── 4. Spawn MCP server (Layer 4) ────────────────────────────
        var mcpServerPath = ResolveMcpServerPath(_settings.Mcp.ServerPath);
        var mcpEnvVars    = BuildMcpEnvironmentVariables(_settings);
        _mcpClient = new McpProcessClient(mcpServerPath, _auditLogger, mcpEnvVars);

        _auditLogger.Append(new AuditEvent
        {
            Actor  = "runtime",
            Action = "MCP_SERVER_PATH_RESOLVED",
            Result = File.Exists(mcpServerPath) ? "ok" : "file_not_found",
            Details = new Dictionary<string, object>
            {
                ["resolvedPath"] = mcpServerPath,
                ["exists"]       = File.Exists(mcpServerPath)
            }
        });

        try
        {
            await _mcpClient.StartAsync();
        }
        catch (Exception ex)
        {
            _auditLogger.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "MCP_SERVER_START_FAILED",
                Result = "error",
                Details = new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["path"]  = mcpServerPath
                }
            });

            // Continue without MCP — orchestrator will degrade gracefully
            // but the chat UI will now show "0 tools (MCP offline)"
        }

        // ── 5. Wrap MCP client with audit + permission gate ──────────
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var wpfPrompter = new WpfPermissionPrompter(this);
        _permissionGate = new WpfPermissionGate(
            _permissionBroker!, wpfPrompter, _auditLogger, _settings);
        _auditedMcpClient = new AuditedMcpToolClient(
            _mcpClient, _auditLogger, _permissionGate, sessionId);

        // "Allow always" from the permission prompt — persist the
        // group policy change to settings.json and swap the snapshot
        _permissionGate.PersistGroupAsAlways += group =>
        {
            Dispatcher.InvokeAsync(() => PersistGroupPolicyAsAlways(group));
        };

        // ── 6. Create Agent Orchestrator (Layer 2) ───────────────────
        if (_settings.Dialogue.PersistenceEnabled)
        {
            var dialoguePath = ResolveDialogueStatePath(_settings);
            _dialogueStatePersistence = new FileDialogueStatePersistence(dialoguePath, _auditLogger);
        }

        _orchestrator = new AgentOrchestrator(
            _llmClient,
            _auditedMcpClient,
            _auditLogger,
            _settings.Llm.SystemPrompt,
            geocodeMismatchMode: _settings.Dialogue.GeocodeMismatchMode);

        // Seed the orchestrator with the active profile from settings
        // so it can pass it through to MCP tool calls at runtime.
        _orchestrator.ActiveProfileId = _settings.ActiveProfileId;

        // Propagate memory master off — when disabled, orchestrator
        // skips retrieval and filters out memory_* tool definitions
        _orchestrator.MemoryEnabled = _settings.Memory.Enabled;

        if (_dialogueStatePersistence is not null)
        {
            var seededState = await _dialogueStatePersistence.LoadAsync();
            if (seededState is not null)
            {
                _orchestrator.SeedDialogueState(seededState);
                _orchestrator.ContextLocked = seededState.ContextLocked;
            }
        }

        _auditLogger.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "AGENT_ORCHESTRATOR_CREATED",
            Result = "ok"
        });

        // ── 6. UI surface (Layer 1) ──────────────────────────────────
        if (!_isHeadless && _settings.Ui.ShowOverlay)
        {
            InitializeOverlayWindow(showImmediately: !_settings.Ui.StartMinimized);
        }

        // Tray icon is always active (headless or not)
        _trayIcon = new TrayIconService(
            _auditLogger,
            isOverlayVisible: () => _overlayWindow?.IsVisible == true,
            toggleOverlay: ToggleOverlayVisibility,
            showCommandPalette: ShowCommandPalette,
            stopAll: StopAllAndShutdown,
            exit: RequestShutdown);

        // ── 7. TTS + PTT pipeline ────────────────────────────────────
        _ttsService = new TextToSpeechService(_auditLogger, _settings.Audio.TtsEnabled);

        var pttActivationKey = ParseVirtualKey(_settings.Audio.PttKey, defaultKey: 0x7C);
        _pttService = new PushToTalkService(
            _auditLogger,
            _runtimeController,
            activationKey: pttActivationKey,
            onRecordingComplete: OnPttRecordingCompleteAsync);
        _pttService.Start();

        // Hotkeys need a window handle; use the overlay if available, otherwise use a hidden helper window.
        _hotkeyOwnerWindow = _overlayWindow ?? CreateHiddenHotkeyWindow();
        InitializeHotkeys(_hotkeyOwnerWindow);
    }

    // ─────────────────────────────────────────────────────────────────────
    // PTT -> Transcribe -> Orchestrator -> TTS Pipeline
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when PTT recording stops. This is the entry point for the
    /// audio -> transcribe -> orchestrator -> TTS response pipeline.
    /// Transcription is currently a placeholder; the file path is logged
    /// and a stub message is sent to the orchestrator.
    /// </summary>
    private async Task OnPttRecordingCompleteAsync(string audioFilePath)
    {
        if (_orchestrator == null)
            return;

        _auditLogger?.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "PTT_PIPELINE_START",
            Result = "ok",
            Details = new Dictionary<string, object> { ["audioFile"] = audioFilePath }
        });

        // Placeholder transcription path until local Whisper is wired in.
        // Keeps the voice pipeline testable end-to-end in the meantime.
        var transcribedText = "[Voice input received - transcription pending integration]";

        _runtimeController?.SetState(Core.AssistantState.Thinking, "Processing voice input");

        try
        {
            var response = await _orchestrator.ProcessAsync(transcribedText);

            if (response.Success && _ttsService != null)
            {
                _ttsService.Speak(response.Text);
            }

            _runtimeController?.SetState(Core.AssistantState.Idle, "Response delivered");
        }
        catch (Exception ex)
        {
            _auditLogger?.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "PTT_PIPELINE_ERROR",
                Result = "error",
                Details = new Dictionary<string, object> { ["error"] = ex.Message }
            });
            _runtimeController?.SetState(Core.AssistantState.Idle, "Pipeline error");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Overlay Window Setup
    // ─────────────────────────────────────────────────────────────────────

    private void InitializeOverlayWindow(bool showImmediately = true)
    {
        if (_overlayWindow != null)
            return;

        _overlayWindow = new MainWindow();
        _overlayViewModel = new OverlayViewModel(
            _runtimeController!,
            _auditLogger!,
            _permissionBroker!,
            _toolRunner!,
            RequestShutdown);

        _overlayWindow.SetViewModel(_overlayViewModel);
        _overlayWindow.Closing += OverlayWindow_Closing;
        MainWindow = _overlayWindow;

        if (showImmediately)
        {
            _overlayWindow.Show();
            _overlayWindow.Activate();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // MCP Server Path Resolution
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the MCP server executable path. "auto" searches the build
    /// output directory tree so we don't break when the TFM changes.
    ///
    /// Path geometry from AppContext.BaseDirectory (bin/Debug/net8.0-windows):
    ///   .. (×5)  → apps/
    ///   .. (×6)  → repo root
    ///
    /// The MCP server lives at:
    ///   apps/mcp-server/SirThaddeus.McpServer/bin/Debug/{tfm}/
    /// </summary>
    private static string ResolveMcpServerPath(string configured)
    {
        if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            return configured;

        const string exeName = "SirThaddeus.McpServer.exe";
        var baseDir = AppContext.BaseDirectory;

        // 1. Adjacent to the running desktop runtime (publish / single-dir output)
        var adjacent = Path.Combine(baseDir, exeName);
        if (File.Exists(adjacent))
            return adjacent;

        // 2. Scan the MCP server's build output — works regardless of TFM folder name.
        //    5 parent jumps from bin/Debug/net8.0-windows lands at apps/,
        //    so we navigate directly to the sibling mcp-server project.
        //    Multiple TFM directories may exist (e.g. stale net8.0 + current
        //    net8.0-windows10.0.19041.0), so pick the most recently built binary.
        var mcpBinDebug = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..",
            "mcp-server", "SirThaddeus.McpServer",
            "bin", "Debug"));

        if (Directory.Exists(mcpBinDebug))
        {
            string? newest = null;
            var newestTime = DateTime.MinValue;

            foreach (var tfmDir in Directory.GetDirectories(mcpBinDebug))
            {
                var candidate = Path.Combine(tfmDir, exeName);
                if (!File.Exists(candidate)) continue;

                var writeTime = File.GetLastWriteTimeUtc(candidate);
                if (writeTime > newestTime)
                {
                    newest     = candidate;
                    newestTime = writeTime;
                }
            }

            if (newest != null)
                return newest;
        }

        // 3. Fallback: return the expected path so the error message is actionable
        return Path.GetFullPath(Path.Combine(mcpBinDebug, "net8.0-windows10.0.19041.0", exeName));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Memory / MCP Environment Variables
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the environment variable dictionary that the MCP server child
    /// process needs for memory, profile, and weather tools.
    /// </summary>
    private static Dictionary<string, string> BuildMcpEnvironmentVariables(AppSettings settings)
    {
        var env = new Dictionary<string, string>();

        // Active profile: always set so the MCP server can distinguish
        // "not configured" (env var absent) from "no profile selected"
        // (env var present but empty). Empty = don't load any profile.
        env["ST_ACTIVE_PROFILE_ID"] = settings.ActiveProfileId ?? "";

        // Memory env vars are only needed when memory is enabled.
        if (settings.Memory.Enabled)
        {
            env["ST_MEMORY_DB_PATH"] = ResolveMemoryDbPath(settings);
            env["ST_LLM_BASEURL"]    = settings.Llm.BaseUrl;

            // Embeddings model: use explicit setting, fall back to the chat model
            if (settings.Memory.UseEmbeddings)
            {
                var embModel = string.IsNullOrWhiteSpace(settings.Memory.EmbeddingsModel)
                    ? settings.Llm.Model
                    : settings.Memory.EmbeddingsModel;
                env["ST_LLM_EMBEDDINGS_MODEL"] = embModel;
            }
        }

        // Weather stack settings
        env["ST_WEATHER_PROVIDER_MODE"] = settings.Weather.ProviderMode;
        env["ST_WEATHER_FORECAST_CACHE_MINUTES"] =
            Math.Clamp(settings.Weather.ForecastCacheMinutes, 10, 30).ToString();
        env["ST_WEATHER_GEOCODE_CACHE_MINUTES"] =
            Math.Max(60, settings.Weather.GeocodeCacheMinutes).ToString();
        env["ST_WEATHER_PLACE_MEMORY_ENABLED"] =
            settings.Weather.PlaceMemoryEnabled ? "true" : "false";
        env["ST_WEATHER_PLACE_MEMORY_PATH"] =
            ResolveWeatherPlaceMemoryPath(settings);
        env["ST_WEATHER_USER_AGENT"] =
            string.IsNullOrWhiteSpace(settings.Weather.UserAgent)
                ? "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)"
                : settings.Weather.UserAgent.Trim();

        return env;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Tool Registration (for the legacy tool runner, kept for permission demos)
    // ─────────────────────────────────────────────────────────────────────

    private void RegisterTools(EnforcingToolRunner runner)
    {
        runner.RegisterTool(new ScreenCaptureTool());
        runner.RegisterTool(new FileReadTool());
        runner.RegisterTool(new SystemCommandTool());

        _playwrightTool = new PlaywrightBrowserNavigateTool();
        runner.RegisterTool(_playwrightTool);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Hotkeys
    // ─────────────────────────────────────────────────────────────────────

    private void InitializeHotkeys(Window owner)
    {
        _hotkeyService = new GlobalHotkeyService(owner);

        _commandPaletteHotkeyId = _hotkeyService.Register(
            GlobalHotkeyService.Modifiers.Control,
            GlobalHotkeyService.VirtualKeys.Space,
            ShowCommandPalette);

        _auditLogger?.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "HOTKEY_REGISTERED",
            Result = _commandPaletteHotkeyId > 0 ? "ok" : "failed",
            Details = new Dictionary<string, object>
            {
                ["hotkey"] = "Ctrl+Space",
                ["action"] = "Command Palette"
            }
        });
    }

    private static Window CreateHiddenHotkeyWindow()
    {
        // Hidden WPF window used only to host a Win32 handle for RegisterHotKey.
        var window = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Visibility = Visibility.Hidden
        };

        // EnsureHandle() is called by GlobalHotkeyService, but showing once makes
        // window lifetime more predictable in practice.
        window.Show();
        window.Hide();
        return window;
    }

    private static uint ParseVirtualKey(string? configuredKey, uint defaultKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
            return defaultKey;

        var key = configuredKey.Trim();

        // Hex form: 0x7C
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            return hex;
        }

        // Function keys: F1..F24
        if ((key.StartsWith('F') || key.StartsWith('f')) &&
            int.TryParse(key[1..], out var fn) &&
            fn is >= 1 and <= 24)
        {
            // VK_F1 = 0x70
            return (uint)(0x6F + fn);
        }

        return defaultKey;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Command Palette
    // ─────────────────────────────────────────────────────────────────────

    private void ShowCommandPalette()
    {
        Dispatcher.Invoke(() =>
        {
            if (_commandPaletteWindow == null || !_commandPaletteWindow.IsLoaded)
            {
                _commandPaletteWindow = CreateCommandPaletteWindow();
            }

            if (_commandPaletteWindow.IsVisible)
            {
                _commandPaletteWindow.Activate();
            }
            else
            {
                _commandPaletteWindow.Reset();
                _commandPaletteWindow.Show();
            }

            _auditLogger?.Append(new AuditEvent
            {
                Actor = "user",
                Action = "COMMAND_PALETTE_OPENED",
                Result = "ok"
            });
        });
    }

    private CommandPaletteWindow CreateCommandPaletteWindow()
    {
        var window = new CommandPaletteWindow();
        var host = new RuntimeControllerHost(_runtimeController!);

        var viewModel = new CommandPaletteViewModel(
            _orchestrator!,
            _llmClient!,
            host,
            _auditLogger!,
            closeWindow: () => window.Hide(),
            dialogueStatePersistence: _dialogueStatePersistence);

        window.SetViewModel(viewModel);

        // Clear session-scoped permission grants on "New Chat"
        viewModel.ConversationCleared += () =>
        {
            _permissionGate?.ClearSessionGrants();
            if (_dialogueStatePersistence is not null)
                _ = _dialogueStatePersistence.ClearAsync();
        };

        // ── Memory + Profile Browsers ────────────────────────────────
        // Open a direct connection to the SQLite memory DB for the
        // user's browsing panels. This is user-initiated data management,
        // not agent-driven, so it bypasses MCP intentionally.
        if (_settings!.Memory.Enabled)
        {
            try
            {
                var dbPath = ResolveMemoryDbPath(_settings);
                var store  = new SqliteMemoryStore(dbPath);
                var memVm  = new MemoryBrowserViewModel(store, _auditLogger!);
                window.SetMemoryBrowserViewModel(memVm);

                // Profile Browser shares the same DB connection
                var profVm = new ProfileBrowserViewModel(store, _auditLogger!);
                window.SetProfileBrowserViewModel(profVm);

                // Settings panel — surfaces config values + profile dropdown
                var settingsVm = new SettingsViewModel(_settings, store, _auditLogger!);
                settingsVm.ActiveProfileChanged += profileId =>
                {
                    // Propagate to orchestrator for runtime tool calls
                    // (env vars can't cross process boundaries at runtime,
                    // so we pass the profile ID in the tool call args instead)
                    if (_orchestrator is not null)
                        _orchestrator.ActiveProfileId = profileId;

                    // Also set env var for future MCP restarts
                    Environment.SetEnvironmentVariable(
                        "ST_ACTIVE_PROFILE_ID", profileId ?? "");
                };

                // Propagate settings changes to the permission gate
                // so the immutable policy snapshot is swapped atomically
                settingsVm.SettingsChanged += updated =>
                {
                    _permissionGate?.UpdateSettings(updated);

                    // Propagate memory master off to orchestrator
                    if (_orchestrator is not null)
                        _orchestrator.MemoryEnabled = updated.Memory.Enabled;
                };

                window.SetSettingsViewModel(settingsVm);
            }
            catch (Exception ex)
            {
                _auditLogger?.Append(new AuditEvent
                {
                    Actor  = "system",
                    Action = "MEMORY_BROWSER_INIT_FAILED",
                    Result = ex.Message
                });
            }
        }

        return window;
    }

    // ─────────────────────────────────────────────────────────────────────
    // "Allow always" from permission prompt
    //
    // Persists a single group's policy change to settings.json,
    // swaps the gate's snapshot, and refreshes the SettingsViewModel
    // so the UI reflects the new value.
    // ─────────────────────────────────────────────────────────────────────

    private void PersistGroupPolicyAsAlways(string group)
    {
        if (_settings is null) return;

        var perms = _settings.Mcp.Permissions;
        var updated = _settings with
        {
            Mcp = _settings.Mcp with
            {
                Permissions = group switch
                {
                    "screen"      => perms with { Screen      = "always" },
                    "files"       => perms with { Files       = "always" },
                    "system"      => perms with { System      = "always" },
                    "web"         => perms with { Web         = "always" },
                    "memoryRead"  => perms with { MemoryRead  = "always" },
                    "memoryWrite" => perms with { MemoryWrite = "always" },
                    _             => perms
                }
            }
        };

        SettingsManager.Save(updated);
        _settings = updated;
        _permissionGate?.UpdateSettings(updated);

        _auditLogger?.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "SETTINGS_SAVED",
            Result = $"group={group} policy=always (via permission prompt)"
        });
    }

    /// <summary>
    /// Resolves the memory DB path from settings. "auto" maps to
    /// %LOCALAPPDATA%\SirThaddeus\memory.db. Ensures the parent
    /// directory exists.
    /// </summary>
    private static string ResolveMemoryDbPath(AppSettings settings)
    {
        var dbPath = settings.Memory.DbPath;
        if (string.Equals(dbPath, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            dbPath = Path.Combine(localAppData, "SirThaddeus", "memory.db");
        }

        // Ensure the directory exists
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return dbPath;
    }

    /// <summary>
    /// Resolves weather place-memory path from settings. "auto" maps to
    /// %LOCALAPPDATA%\SirThaddeus\weather-places.json and ensures parent
    /// directory exists.
    /// </summary>
    private static string ResolveWeatherPlaceMemoryPath(AppSettings settings)
    {
        var path = settings.Weather.PlaceMemoryPath;
        if (string.Equals(path, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            path = Path.Combine(localAppData, "SirThaddeus", "weather-places.json");
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        return path;
    }

    /// <summary>
    /// Resolves optional dialogue-state persistence path. "auto" maps to
    /// %LOCALAPPDATA%\SirThaddeus\dialogue-state.json.
    /// </summary>
    private static string ResolveDialogueStatePath(AppSettings settings)
    {
        var path = settings.Dialogue.PersistencePath;
        if (string.Equals(path, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            path = Path.Combine(localAppData, "SirThaddeus", "dialogue-state.json");
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        return path;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Overlay Visibility
    // ─────────────────────────────────────────────────────────────────────

    private void ToggleOverlayVisibility()
    {
        Dispatcher.Invoke(() =>
        {
            if (_overlayWindow == null)
            {
                // Headless mode: lazily create the overlay on first toggle
                InitializeOverlayWindow();
                _overlayWindow!.Show();
                _overlayWindow.Activate();
                return;
            }

            if (_overlayWindow.IsVisible)
            {
                _overlayWindow.Hide();
                _auditLogger?.Append(new AuditEvent
                {
                    Actor = "user",
                    Action = "OVERLAY_HIDDEN",
                    Result = "ok"
                });
            }
            else
            {
                _overlayWindow.Show();
                _overlayWindow.Activate();
                _auditLogger?.Append(new AuditEvent
                {
                    Actor = "user",
                    Action = "OVERLAY_SHOWN",
                    Result = "ok"
                });
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // STOP ALL / Shutdown
    // ─────────────────────────────────────────────────────────────────────

    private void StopAllAndShutdown()
    {
        _runtimeController?.StopAll();
        RequestShutdown();
    }

    private void OverlayWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isShuttingDown)
            return;

        e.Cancel = true;
        _overlayWindow?.Hide();
        _auditLogger?.Append(new AuditEvent
        {
            Actor = "user",
            Action = "OVERLAY_HIDDEN",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["reason"] = "Window close intercepted (tray mode)"
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // State Change Handler
    // ─────────────────────────────────────────────────────────────────────

    private void OnRuntimeStateChanged(object? sender, StateChangedEventArgs e)
    {
        if (e.NewState == AssistantState.Off)
        {
            if (_permissionBroker != null)
            {
                var revokedCount = _permissionBroker.RevokeAll("STOP ALL triggered");
                if (revokedCount > 0)
                {
                    _auditLogger?.Append(new AuditEvent
                    {
                        Actor = "runtime",
                        Action = "STOP_ALL_TOKENS_REVOKED",
                        Result = "ok",
                        Details = new Dictionary<string, object>
                        {
                            ["tokensRevoked"] = revokedCount
                        }
                    });
                }
            }

            if (_hotkeyService != null)
            {
                _hotkeyService.UnregisterAll();
                _commandPaletteHotkeyId = -1;

                _auditLogger?.Append(new AuditEvent
                {
                    Actor = "runtime",
                    Action = "HOTKEYS_UNREGISTERED",
                    Result = "ok",
                    Details = new Dictionary<string, object>
                    {
                        ["reason"] = "STOP ALL triggered"
                    }
                });
            }

            Dispatcher.Invoke(() => { _commandPaletteWindow?.Hide(); });
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Shutdown
    // ─────────────────────────────────────────────────────────────────────

    protected override async void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;

        if (_runtimeController != null)
            _runtimeController.StateChanged -= OnRuntimeStateChanged;

        if (_permissionBroker is { ActiveTokenCount: > 0 })
            _permissionBroker.RevokeAll("Application shutdown");

        _pttService?.Dispose();
        _pttService = null;

        _ttsService?.Dispose();
        _ttsService = null;

        if (_hotkeyOwnerWindow != null && _hotkeyOwnerWindow != _overlayWindow)
        {
            try { _hotkeyOwnerWindow.Close(); } catch { /* best effort */ }
        }
        _hotkeyOwnerWindow = null;

        _hotkeyService?.Dispose();
        _hotkeyService = null;

        _trayIcon?.Dispose();
        _trayIcon = null;

        _commandPaletteWindow?.Close();
        _commandPaletteWindow = null;

        // Tear down orchestrator layers in reverse order
        _llmClient?.Dispose();
        _mcpClient?.Dispose();

        _auditLogger?.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "APP_SHUTDOWN",
            Result = "ok"
        });

        if (_playwrightTool != null)
            await _playwrightTool.DisposeAsync();

        _runtimeController?.Dispose();
        _auditLogger?.Dispose();

        base.OnExit(e);
    }

    private void RequestShutdown()
    {
        _isShuttingDown = true;
        Dispatcher.BeginInvoke(
            () => Shutdown(0),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Startup Audit
    // ─────────────────────────────────────────────────────────────────────

    private void LogStartup()
    {
        _auditLogger?.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "APP_STARTUP",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["version"] = "0.2.0",
                ["auditPath"] = JsonLineAuditLogger.GetDefaultPath(),
                ["headless"] = _isHeadless,
                ["llmBaseUrl"] = _settings?.Llm.BaseUrl ?? "n/a",
                ["llmModel"] = _settings?.Llm.Model ?? "n/a",
                ["commandPalette"] = "Ctrl+Space"
            }
        });
    }
}
