using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
using SirThaddeus.Voice;

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
    private AudioCaptureService? _audioCaptureService;
    private AudioPlaybackService? _audioPlaybackService;
    private LocalAsrHttpClient? _asrClient;
    private LocalTtsHttpClient? _localTtsClient;
    private VoiceHostProcessManager? _voiceHostProcessManager;
    private AgentVoiceService? _voiceAgentService;
    private VoiceSessionOrchestrator? _voiceOrchestrator;
    private TrayIconService? _trayIcon;
    private MainWindow? _overlayWindow;
    private OverlayViewModel? _overlayViewModel;
    private CommandPaletteWindow? _commandPaletteWindow;
    private CommandPaletteViewModel? _commandPaletteViewModel;
    private int _commandPaletteHotkeyId = -1;
    private int _reasoningGuardrailsHotkeyId = -1;
    private bool _isShuttingDown;
    private bool _isHeadless;
    private readonly object _liveAsrPreviewGate = new();
    private readonly SemaphoreSlim _voiceMicUpGate = new(1, 1);
    private CancellationTokenSource? _liveAsrPreviewCts;
    private Task? _liveAsrPreviewTask;
    private readonly object _liveAsrTranscriptGate = new();
    private string _latestLiveVoiceTranscript = "";
    private string _liveAsrAccumulatedTranscript = "";
    private DateTimeOffset? _liveAsrAccumulatedUpdatedAtUtc;
    private string? _lastVoiceHostFailureMessage;
    private readonly object _voiceTimelineGate = new();
    private VoiceSessionTimeline? _voiceTimeline;
    private readonly object _pendingVoiceUiGate = new();
    private readonly List<(ChatMessageRole Role, string Content)> _pendingVoiceMessages = [];
    private readonly List<(LogEntryKind Kind, string Text)> _pendingVoiceLogs = [];
    private readonly object _mcpAuditMirrorGate = new();
    private readonly HashSet<string> _mirroredMcpAuditKeys = [];
    private readonly Queue<string> _mirroredMcpAuditKeyOrder = new();
    private const int MaxMirroredMcpAuditKeys = 2_048;
    private readonly DateTimeOffset _mcpAuditMirrorCutoffUtc = DateTimeOffset.UtcNow;

    private sealed class VoiceSessionTimeline
    {
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset? FirstAudioFrameAtUtc { get; set; }
        public DateTimeOffset? MicReleasedAtUtc { get; set; }
        public DateTimeOffset? AsrStartedAtUtc { get; set; }
        public DateTimeOffset? AsrFirstTokenAtUtc { get; set; }
        public DateTimeOffset? TranscriptReadyAtUtc { get; set; }
        public DateTimeOffset? AgentStartedAtUtc { get; set; }
        public DateTimeOffset? AgentReadyAtUtc { get; set; }
        public DateTimeOffset? TtsStartedAtUtc { get; set; }
        public DateTimeOffset? SpeakingStartedAtUtc { get; set; }
        public string SessionId { get; set; } = "";
        public bool UserMessageAdded { get; set; }
        public bool AgentMessageAdded { get; set; }
    }

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
            ContextWindowTokens = _settings.Llm.ContextWindowTokens,
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
        _orchestrator.ReasoningGuardrailsMode = _settings.Ui.ReasoningGuardrails;

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

        // ── 7. Voice pipeline (PTT -> Transcribe -> Thinking -> Speak) ──
        _voiceHostProcessManager = new VoiceHostProcessManager(_auditLogger, _settings.Voice);
        _ttsService = new TextToSpeechService(_auditLogger, _settings.Audio.TtsEnabled);
        _audioCaptureService = new AudioCaptureService(_auditLogger)
        {
            DeviceNumber = AudioDeviceEnumerator.ResolveInputDeviceNumber(_settings.Audio.InputDeviceName),
            InputGain    = Math.Clamp(_settings.Audio.InputGain, 0.0, 2.0)
        };
        _audioCaptureService.FirstAudioFrameCaptured += OnCaptureFirstFrameCaptured;
        _localTtsClient = new LocalTtsHttpClient(GetVoiceHostBaseUrlForRequests, () => _settings.Voice, _auditLogger);
        _localTtsClient.TimingUpdated += OnTtsTimingUpdated;
        _audioPlaybackService = new AudioPlaybackService(
            _auditLogger,
            _ttsService,
            () => _settings.Voice,
            _localTtsClient)
        {
            OutputDeviceNumber = AudioDeviceEnumerator.ResolveOutputDeviceNumber(_settings.Audio.OutputDeviceName)
        };
        _audioPlaybackService.PlaybackStarted += OnPlaybackStarted;
        _asrClient = new LocalAsrHttpClient(GetVoiceHostBaseUrlForRequests, () => _settings.Voice, _auditLogger);
        _asrClient.TranscriptReceived += OnAsrTranscriptReceived;
        _asrClient.TimingUpdated += OnAsrTimingUpdated;
        _voiceAgentService = new AgentVoiceService(_orchestrator);
        _voiceOrchestrator = new VoiceSessionOrchestrator(
            _audioCaptureService,
            _audioPlaybackService,
            _asrClient,
            _voiceAgentService,
            _auditLogger,
            new VoiceSessionOrchestratorOptions
            {
                AsrTimeout = TimeSpan.FromMilliseconds(Math.Max(5_000, _settings.Voice.AsrTimeoutMs)),
                AgentTimeout = TimeSpan.FromMilliseconds(Math.Max(10_000, _settings.Voice.AgentTimeoutMs)),
                SpeakingTimeout = TimeSpan.FromMilliseconds(Math.Max(10_000, _settings.Voice.SpeakingTimeoutMs))
            });
        _voiceOrchestrator.StateChanged += OnVoiceStateChanged;
        _voiceOrchestrator.ProgressUpdated += OnVoiceProgressUpdated;
        await _voiceOrchestrator.StartAsync();

        RebindPushToTalkInput(_settings.Audio);

        // ── 8. UI surface (Layer 1) ──────────────────────────────────
        if (!_isHeadless && _settings.Ui.ShowOverlay)
        {
            InitializeOverlayWindow(showImmediately: !_settings.Ui.StartMinimized);
        }

        // Tray icon is always active (headless or not)
        _trayIcon = new TrayIconService(
            _auditLogger,
            isOverlayVisible: () => _overlayWindow?.IsVisible == true,
            getReasoningGuardrails: () => _settings?.Ui.ReasoningGuardrails ?? "off",
            toggleOverlay: ToggleOverlayVisibility,
            cycleReasoningGuardrails: CycleReasoningGuardrailsMode,
            showCommandPalette: ShowCommandPalette,
            stopAll: StopAllAndShutdown,
            exit: RequestShutdown);

        // Hotkeys need a window handle; use the overlay if available, otherwise use a hidden helper window.
        _hotkeyOwnerWindow = _overlayWindow ?? CreateHiddenHotkeyWindow();
        InitializeHotkeys(_hotkeyOwnerWindow);

        // Optional warm-up runs in background and never blocks app startup.
        _voiceHostProcessManager.ScheduleWarmup(TimeSpan.FromSeconds(6), startIfMissing: false);
    }

    private void OnPttMicDown()
    {
        BeginVoiceTimeline();
        _voiceOrchestrator?.EnqueueMicDown();
        ResetLiveAsrPreviewTranscript();
        _voiceOrchestrator?.SetRealtimeTranscriptHint("", DateTimeOffset.MinValue);
        PublishVoiceTranscript("");
        PublishVoiceStatus("Listening...");
        PublishVoiceActive(true);
        StartLiveAsrPreviewLoop();

        // Start readiness work as soon as the user begins holding PTT so
        // host/backend startup overlaps with mic capture instead of adding
        // full latency after mic-up.
        _ = EnsureVoiceHostReadyAsync(showUserFacingFailure: false);
    }

    private async void OnPttMicUp()
    {
        if (!await _voiceMicUpGate.WaitAsync(0))
            return;

        try
        {
            MarkVoiceMicReleased();
            _ = await TryHandleMicUpAsync(showUserFacingFailure: true);
        }
        finally
        {
            _voiceMicUpGate.Release();
        }
    }

    private void OnPttShutup()
    {
        _ = StopLiveAsrPreviewLoopAsync(waitForDrain: false);
        PublishVoiceStatus("Canceled.");
        _voiceOrchestrator?.EnqueueShutup();
        AppendVoiceActivity("Voice canceled by operator.", LogEntryKind.Info);
    }

    private async Task<bool> TryHandleMicUpAsync(bool showUserFacingFailure)
    {
        await StopLiveAsrPreviewLoopAsync(waitForDrain: true);
        PublishVoiceStatus("Transcribing...");

        var readyStopwatch = Stopwatch.StartNew();
        var ready = await EnsureVoiceHostReadyAsync(showUserFacingFailure: showUserFacingFailure);
        readyStopwatch.Stop();

        if (!ready)
        {
            LogVoiceTiming("VoiceHost readiness failed", readyStopwatch.Elapsed, GetCurrentVoiceSessionId(), LogEntryKind.Error);

            var failureMessage = "Voice unavailable.";
            if (_lastVoiceHostFailureMessage is not null)
                failureMessage = _lastVoiceHostFailureMessage;

            _voiceOrchestrator?.EnqueueFault(failureMessage);
            PublishVoiceStatus(failureMessage);
            return false;
        }

        LogVoiceTiming("VoiceHost readiness", readyStopwatch.Elapsed, GetCurrentVoiceSessionId(), LogEntryKind.Info);
        var allowRealtimeHint = string.Equals(
            _settings?.Voice.GetNormalizedSttEngine(),
            "faster-whisper",
            StringComparison.OrdinalIgnoreCase);
        var (hintTranscript, hintObservedAtUtc) = GetLiveAsrPreviewHint();
        if (allowRealtimeHint && !string.IsNullOrWhiteSpace(hintTranscript) && hintObservedAtUtc is { } observedAt)
        {
            _voiceOrchestrator?.SetRealtimeTranscriptHint(hintTranscript, observedAt);
            WriteVoiceAuditNonBlocking("VOICE_PREVIEW_HINT_SUBMITTED", "ok", new Dictionary<string, object>
            {
                ["sessionId"] = GetCurrentVoiceSessionId() ?? "",
                ["transcriptLength"] = hintTranscript.Length,
                ["hintObservedAtUtc"] = observedAt.ToString("O")
            });
        }
        else
        {
            _voiceOrchestrator?.SetRealtimeTranscriptHint("", DateTimeOffset.MinValue);
        }

        _voiceOrchestrator?.EnqueueMicUp();
        return true;
    }

    private string GetVoiceHostBaseUrlForRequests()
    {
        if (!string.IsNullOrWhiteSpace(_voiceHostProcessManager?.CurrentBaseUrl))
            return _voiceHostProcessManager.CurrentBaseUrl;

        return _settings?.Voice.GetVoiceHostBaseUrl() ?? "http://127.0.0.1:17845";
    }

    private async Task<bool> EnsureVoiceHostReadyAsync(bool showUserFacingFailure)
    {
        if (_settings?.Voice.VoiceHostEnabled != true || _voiceHostProcessManager is null)
            return true;

        var result = await _voiceHostProcessManager.EnsureRunningAsync(CancellationToken.None);
        if (result.Success)
        {
            _lastVoiceHostFailureMessage = null;
            return true;
        }

        _lastVoiceHostFailureMessage = result.UserMessage;

        _auditLogger?.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICEHOST_READY_CHECK_FAILED",
            Result = "error",
            Details = new Dictionary<string, object>
            {
                ["errorCode"] = result.ErrorCode ?? "",
                ["message"] = result.UserMessage
            }
        });

        if (showUserFacingFailure)
        {
            try
            {
                System.Windows.MessageBox.Show(
                    result.UserMessage,
                    "Voice unavailable",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            catch
            {
                // MessageBox is best-effort only; runtime should continue.
            }
        }

        return false;
    }

    private void OnAsrTranscriptReceived(object? sender, AsrTranscriptReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Transcript))
            return;

        var transcript = e.Transcript.Trim();
        if (string.IsNullOrWhiteSpace(transcript))
            return;

        if (IsPreviewSessionId(e.SessionId))
        {
            UpdateLiveAsrPreviewTranscript(transcript);
            return;
        }

        lock (_liveAsrTranscriptGate)
        {
            _latestLiveVoiceTranscript = transcript;
        }
        PublishVoiceTranscript(transcript);
    }

    private void ResetLiveAsrPreviewTranscript()
    {
        lock (_liveAsrTranscriptGate)
        {
            _latestLiveVoiceTranscript = "";
            _liveAsrAccumulatedTranscript = "";
            _liveAsrAccumulatedUpdatedAtUtc = null;
        }
    }

    private (string Transcript, DateTimeOffset? ObservedAtUtc) GetLiveAsrPreviewHint()
    {
        lock (_liveAsrTranscriptGate)
        {
            return (_liveAsrAccumulatedTranscript, _liveAsrAccumulatedUpdatedAtUtc);
        }
    }

    private void UpdateLiveAsrPreviewTranscript(string transcript)
    {
        var normalized = NormalizeTranscriptText(transcript);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var now = DateTimeOffset.UtcNow;
        string merged;
        bool changed = false;
        lock (_liveAsrTranscriptGate)
        {
            merged = MergePreviewTranscript(_liveAsrAccumulatedTranscript, normalized);
            if (!string.Equals(_liveAsrAccumulatedTranscript, merged, StringComparison.Ordinal))
            {
                _liveAsrAccumulatedTranscript = merged;
                changed = true;
            }

            _liveAsrAccumulatedUpdatedAtUtc = now;

            if (!string.Equals(_latestLiveVoiceTranscript, merged, StringComparison.Ordinal))
            {
                _latestLiveVoiceTranscript = merged;
                changed = true;
            }
        }

        if (changed)
            PublishVoiceTranscript(merged);
    }

    private static string NormalizeTranscriptText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return string.Join(
            " ",
            text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string MergePreviewTranscript(string accumulated, string incoming)
    {
        if (string.IsNullOrWhiteSpace(accumulated))
            return incoming;
        if (string.IsNullOrWhiteSpace(incoming))
            return accumulated;

        if (incoming.Length >= accumulated.Length &&
            incoming.Contains(accumulated, StringComparison.OrdinalIgnoreCase))
        {
            return incoming;
        }

        if (accumulated.Length >= incoming.Length &&
            accumulated.Contains(incoming, StringComparison.OrdinalIgnoreCase))
        {
            return accumulated;
        }

        var leftTokens = accumulated.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = incoming.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var maxOverlap = Math.Min(leftTokens.Length, rightTokens.Length);
        var overlap = 0;

        for (var candidate = maxOverlap; candidate >= 1; candidate--)
        {
            var matches = true;
            for (var index = 0; index < candidate; index++)
            {
                var leftToken = NormalizeOverlapToken(leftTokens[leftTokens.Length - candidate + index]);
                var rightToken = NormalizeOverlapToken(rightTokens[index]);
                if (!string.Equals(leftToken, rightToken, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                overlap = candidate;
                break;
            }
        }

        if (overlap == 0)
            return incoming.Length >= accumulated.Length ? incoming : accumulated;

        if (overlap >= rightTokens.Length)
            return accumulated;

        var mergedTokens = new string[leftTokens.Length + rightTokens.Length - overlap];
        Array.Copy(leftTokens, mergedTokens, leftTokens.Length);
        Array.Copy(
            rightTokens,
            overlap,
            mergedTokens,
            leftTokens.Length,
            rightTokens.Length - overlap);
        return string.Join(" ", mergedTokens);
    }

    private static string NormalizeOverlapToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "";

        return token.Trim().Trim('\"', '\'', '.', ',', '!', '?', ';', ':', '-', '_', '(', ')', '[', ']', '{', '}', '…');
    }

    private void OnCaptureFirstFrameCaptured(object? sender, AudioCaptureFirstFrameEventArgs e)
    {
        if (IsPreviewSessionId(e.SessionId))
            return;

        bool marked = false;
        string? resolvedSessionId;
        lock (_voiceTimelineGate)
        {
            _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = e.TimestampUtc };
            if (!string.IsNullOrWhiteSpace(e.SessionId))
                _voiceTimeline.SessionId = e.SessionId;
            resolvedSessionId = _voiceTimeline.SessionId;

            if (_voiceTimeline.FirstAudioFrameAtUtc is null)
            {
                _voiceTimeline.FirstAudioFrameAtUtc = e.TimestampUtc;
                marked = true;
            }
        }

        if (marked)
        {
            LogVoiceStageTimestamp("t_first_audio_frame", e.TimestampUtc, resolvedSessionId, new Dictionary<string, object>
            {
                ["bytesRecorded"] = e.BytesRecorded
            });
        }
    }

    private void OnAsrTimingUpdated(object? sender, AsrTimingEventArgs e)
    {
        if (IsPreviewSessionId(e.SessionId))
            return;

        var timestamp = e.TimestampUtc;
        var stageKey = "";
        bool marked = false;
        string? resolvedSessionId;
        var extra = new Dictionary<string, object>();

        lock (_voiceTimelineGate)
        {
            _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = timestamp };
            if (!string.IsNullOrWhiteSpace(e.SessionId))
                _voiceTimeline.SessionId = e.SessionId;
            resolvedSessionId = _voiceTimeline.SessionId;

            switch (e.Stage)
            {
                case AsrTimingStage.Start:
                    if (_voiceTimeline.AsrStartedAtUtc is null)
                    {
                        _voiceTimeline.AsrStartedAtUtc = timestamp;
                        stageKey = "t_asr_start";
                        marked = true;
                    }
                    break;
                case AsrTimingStage.FirstToken:
                    if (_voiceTimeline.AsrFirstTokenAtUtc is null)
                    {
                        _voiceTimeline.AsrFirstTokenAtUtc = timestamp;
                        stageKey = "t_asr_first_token";
                        marked = true;
                    }
                    break;
                case AsrTimingStage.Final:
                    if (_voiceTimeline.TranscriptReadyAtUtc is null)
                    {
                        _voiceTimeline.TranscriptReadyAtUtc = timestamp;
                        stageKey = "t_asr_final";
                        marked = true;
                    }
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(e.Source))
            extra["source"] = e.Source;
        extra["requestId"] = e.RequestId;

        if (marked && !string.IsNullOrWhiteSpace(stageKey))
            LogVoiceStageTimestamp(stageKey, timestamp, resolvedSessionId, extra);
    }

    private void OnTtsTimingUpdated(object? sender, TtsTimingEventArgs e)
    {
        if (IsPreviewSessionId(e.SessionId))
            return;

        if (e.Stage != TtsTimingStage.Start)
            return;

        bool marked = false;
        string? resolvedSessionId;
        lock (_voiceTimelineGate)
        {
            _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = e.TimestampUtc };
            if (!string.IsNullOrWhiteSpace(e.SessionId))
                _voiceTimeline.SessionId = e.SessionId;
            resolvedSessionId = _voiceTimeline.SessionId;

            if (_voiceTimeline.TtsStartedAtUtc is null)
            {
                _voiceTimeline.TtsStartedAtUtc = e.TimestampUtc;
                marked = true;
            }
        }

        if (marked)
        {
            LogVoiceStageTimestamp("t_tts_start", e.TimestampUtc, resolvedSessionId, new Dictionary<string, object>
            {
                ["source"] = "tts_client",
                ["requestId"] = e.RequestId
            });
        }
    }

    private void OnPlaybackStarted(object? sender, AudioPlaybackStartedEventArgs e)
    {
        if (IsPreviewSessionId(e.SessionId))
            return;

        MarkVoiceSpeakingStarted(e.TimestampUtc, e.SessionId);
    }

    private static bool IsPreviewSessionId(string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) &&
           sessionId.StartsWith("preview-", StringComparison.OrdinalIgnoreCase);

    private void StartLiveAsrPreviewLoop()
    {
        _ = StopLiveAsrPreviewLoopAsync(waitForDrain: false);

        if (_audioCaptureService is null || _asrClient is null)
            return;

        var cts = new CancellationTokenSource();
        var token = cts.Token;

        var previewTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, token);
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_audioCaptureService is null || !_audioCaptureService.IsCapturing || _asrClient is null)
                        {
                            await Task.Delay(120, token);
                            continue;
                        }

                        var clip = _audioCaptureService.CreateLiveSnapshotClip(maxDurationMs: 2_500);
                        if (clip is null || clip.AudioBytes.Length < 2_400)
                        {
                            await Task.Delay(120, token);
                            continue;
                        }

                        _ = await _asrClient.TranscribeAsync(
                            clip,
                            $"preview-{GetCurrentVoiceSessionId() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()}",
                            token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Preview is diagnostics-only and must not interrupt voice orchestration.
                    }

                    await Task.Delay(350, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation path.
            }
        }, token);

        lock (_liveAsrPreviewGate)
        {
            _liveAsrPreviewCts = cts;
            _liveAsrPreviewTask = previewTask;
        }
    }

    private async Task StopLiveAsrPreviewLoopAsync(bool waitForDrain)
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_liveAsrPreviewGate)
        {
            cts = _liveAsrPreviewCts;
            task = _liveAsrPreviewTask;
            _liveAsrPreviewCts = null;
            _liveAsrPreviewTask = null;
        }

        if (cts is null)
            return;

        try { cts.Cancel(); } catch { /* best effort */ }

        if (waitForDrain && task is not null)
        {
            try
            {
                await Task.WhenAny(task, Task.Delay(1_500));
            }
            catch
            {
                // Preview is diagnostics-only. Never fail the session for preview shutdown.
            }
        }

        cts.Dispose();
    }

    // ── Voice debug panel updates ──────────────────────────────────

    private void PublishVoiceStatus(string status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_commandPaletteViewModel is null) return;
            _commandPaletteViewModel.VoiceStatusText = status;
        });
    }

    private void PublishVoiceTranscript(string text)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_commandPaletteViewModel is null) return;
            _commandPaletteViewModel.VoiceTranscriptText = text;
        });
    }

    private void PublishVoiceActive(bool active)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_commandPaletteViewModel is null) return;
            _commandPaletteViewModel.IsVoiceActive = active;
        });
    }

    private void OnVoiceStateChanged(object? sender, VoiceStateChangedEventArgs e)
    {
        try
        {
            var mapped = e.CurrentState switch
            {
                VoiceState.Idle => AssistantState.Idle,
                VoiceState.Listening => AssistantState.Listening,
                VoiceState.Transcribing or VoiceState.Thinking or VoiceState.Speaking => AssistantState.Thinking,
                VoiceState.Faulted => AssistantState.Thinking,
                _ => AssistantState.Idle
            };

            _runtimeController?.SetState(mapped, $"Voice:{e.CurrentState}");

            // Map orchestrator state to the debug panel.
            var label = e.CurrentState switch
            {
                VoiceState.Idle         => "",
                VoiceState.Listening    => "Listening...",
                VoiceState.Transcribing => "Transcribing...",
                VoiceState.Thinking     => "Thinking...",
                VoiceState.Speaking     => "Speaking...",
                VoiceState.Faulted      => "Faulted.",
                _                       => ""
            };

            PublishVoiceStatus(label);
            PublishVoiceActive(e.CurrentState != VoiceState.Idle);

            if (e.CurrentState == VoiceState.Transcribing)
            {
                var now = DateTimeOffset.UtcNow;
                bool marked = false;
                string? sessionId;
                lock (_voiceTimelineGate)
                {
                    _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = now };
                    sessionId = _voiceTimeline.SessionId;
                    if (_voiceTimeline.AsrStartedAtUtc is null)
                    {
                        _voiceTimeline.AsrStartedAtUtc = now;
                        marked = true;
                    }
                }

                if (marked)
                    LogVoiceStageTimestamp("t_asr_start", now, sessionId, new Dictionary<string, object>
                    {
                        ["source"] = "state_transition"
                    });
            }
            else if (e.CurrentState == VoiceState.Thinking)
            {
                var now = DateTimeOffset.UtcNow;
                bool marked = false;
                string? sessionId;
                lock (_voiceTimelineGate)
                {
                    _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = now };
                    sessionId = _voiceTimeline.SessionId;
                    if (_voiceTimeline.AgentStartedAtUtc is null)
                    {
                        _voiceTimeline.AgentStartedAtUtc = now;
                        marked = true;
                    }
                }

                if (marked)
                    LogVoiceStageTimestamp("t_agent_start", now, sessionId);
            }
            else if (e.CurrentState == VoiceState.Speaking)
            {
                var useStateFallback =
                    string.Equals(
                        _settings?.Voice.GetNormalizedTtsEngine(),
                        "windows",
                        StringComparison.OrdinalIgnoreCase) ||
                    _localTtsClient is null;

                if (useStateFallback)
                {
                    var now = DateTimeOffset.UtcNow;
                    bool marked = false;
                    string? sessionId;
                    lock (_voiceTimelineGate)
                    {
                        _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = now };
                        sessionId = _voiceTimeline.SessionId;
                        if (_voiceTimeline.TtsStartedAtUtc is null)
                        {
                            _voiceTimeline.TtsStartedAtUtc = now;
                            marked = true;
                        }
                    }

                    if (marked)
                        LogVoiceStageTimestamp("t_tts_start", now, sessionId, new Dictionary<string, object>
                        {
                            ["source"] = "state_fallback"
                        });
                }
            }
            else if (e.CurrentState == VoiceState.Faulted)
            {
                var reason = string.IsNullOrWhiteSpace(e.Reason) ? "Unknown voice error." : e.Reason!;
                AppendVoiceActivity($"Voice faulted: {reason}", LogEntryKind.Error);
            }
            else if (e.CurrentState == VoiceState.Idle)
            {
                CompleteVoiceTimeline(e.PreviousState, e.Reason);
            }
        }
        catch (Exception ex)
        {
            AppendVoiceActivity($"Voice state handler error: {ex.Message}", LogEntryKind.Error);
            WriteVoiceAuditNonBlocking("VOICE_HANDLER_ERROR", "error", new Dictionary<string, object>
            {
                ["handler"] = "OnVoiceStateChanged",
                ["message"] = ex.Message
            });
        }
    }

    private void OnVoiceProgressUpdated(object? sender, VoiceProgressEventArgs e)
    {
        try
        {
            switch (e.Kind)
            {
                case VoiceProgressKind.TranscriptReady:
                    PublishVoiceTranscript(e.Text);
                    HandleTranscriptReady(e.SessionId, e.Text);
                    break;

                case VoiceProgressKind.AgentResponseReady:
                    PublishVoiceTranscript($"[Agent] {e.Text}");
                    HandleAgentResponseReady(
                        e.SessionId,
                        e.Text,
                        e.GuardrailsUsed,
                        e.GuardrailsRationale,
                        e.HasTokenUsage,
                        e.TokensIn,
                        e.TokensOut,
                        e.ContextFillPercent);
                    break;

                case VoiceProgressKind.PhaseInfo:
                    PublishVoiceStatus(e.Text);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendVoiceActivity($"Voice progress handler error: {ex.Message}", LogEntryKind.Error);
            WriteVoiceAuditNonBlocking("VOICE_HANDLER_ERROR", "error", new Dictionary<string, object>
            {
                ["handler"] = "OnVoiceProgressUpdated",
                ["message"] = ex.Message
            });
        }
    }

    private void BeginVoiceTimeline()
    {
        var startedAt = DateTimeOffset.UtcNow;
        lock (_voiceTimelineGate)
        {
            _voiceTimeline = new VoiceSessionTimeline
            {
                StartedAtUtc = startedAt
            };
        }

        AppendVoiceActivity("Voice session started.", LogEntryKind.Info);
        LogVoiceStageTimestamp("t_mic_down", startedAt, null);
    }

    private void MarkVoiceMicReleased()
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan? holdDuration = null;
        string? sessionId = null;
        var micUpMarked = false;

        lock (_voiceTimelineGate)
        {
            _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = now };
            if (_voiceTimeline.MicReleasedAtUtc is null)
            {
                _voiceTimeline.MicReleasedAtUtc = now;
                holdDuration = now - _voiceTimeline.StartedAtUtc;
                sessionId = _voiceTimeline.SessionId;
                micUpMarked = true;
            }
        }

        if (holdDuration is { } hold)
            LogVoiceTiming("Mic hold", hold, sessionId, LogEntryKind.Info);
        if (micUpMarked)
            LogVoiceStageTimestamp("t_mic_up", now, sessionId);
    }

    private void HandleTranscriptReady(string sessionId, string transcript)
    {
        var trimmed = transcript?.Trim() ?? "";
        var now = DateTimeOffset.UtcNow;
        TimeSpan? asrDuration = null;
        bool addUserChat = false;
        string? resolvedSessionId = null;
        var asrFinalMarked = false;
        var asrFirstTokenFallbackMarked = false;
        var asrStartFallbackMarked = false;

        lock (_voiceTimelineGate)
        {
            _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = now };
            if (!string.IsNullOrWhiteSpace(sessionId))
                _voiceTimeline.SessionId = sessionId;
            resolvedSessionId = _voiceTimeline.SessionId;

            if (_voiceTimeline.TranscriptReadyAtUtc is null)
            {
                _voiceTimeline.TranscriptReadyAtUtc = now;
                asrFinalMarked = true;
            }
            if (_voiceTimeline.AsrFirstTokenAtUtc is null)
            {
                _voiceTimeline.AsrFirstTokenAtUtc = now;
                asrFirstTokenFallbackMarked = true;
            }
            if (_voiceTimeline.AsrStartedAtUtc is null)
            {
                _voiceTimeline.AsrStartedAtUtc =
                    _voiceTimeline.MicReleasedAtUtc ??
                    _voiceTimeline.StartedAtUtc;
                asrStartFallbackMarked = true;
            }

            var asrAnchor =
                _voiceTimeline.AsrStartedAtUtc ??
                _voiceTimeline.MicReleasedAtUtc ??
                _voiceTimeline.StartedAtUtc;
            asrDuration = now - asrAnchor;

            if (!string.IsNullOrWhiteSpace(trimmed) && !_voiceTimeline.UserMessageAdded)
            {
                _voiceTimeline.UserMessageAdded = true;
                addUserChat = true;
            }
        }

        if (addUserChat)
        {
            AppendVoiceChatMessage(ChatMessageRole.User, trimmed);
            AppendVoiceActivity($"You said: {TruncateForLog(trimmed, 180)}", LogEntryKind.Info);
        }

        if (asrDuration is { } elapsed)
            LogVoiceTiming("ASR transcript ready", elapsed, resolvedSessionId, LogEntryKind.Info);
        if (asrStartFallbackMarked)
        {
            LogVoiceStageTimestamp(
                "t_asr_start",
                now,
                resolvedSessionId,
                new Dictionary<string, object> { ["source"] = "transcript_ready_fallback" });
        }
        if (asrFirstTokenFallbackMarked)
        {
            LogVoiceStageTimestamp("t_asr_first_token", now, resolvedSessionId, new Dictionary<string, object>
            {
                ["source"] = "final_response_fallback"
            });
        }
        if (asrFinalMarked)
        {
            LogVoiceStageTimestamp("t_asr_final", now, resolvedSessionId, new Dictionary<string, object>
            {
                ["source"] = "transcript_ready"
            });
        }
    }

    private void HandleAgentResponseReady(
        string sessionId,
        string response,
        bool guardrailsUsed,
        IReadOnlyList<string> guardrailsRationale,
        bool hasTokenUsage,
        int tokensIn,
        int tokensOut,
        int contextFillPercent)
    {
        var trimmed = response?.Trim() ?? "";
        var now = DateTimeOffset.UtcNow;
        TimeSpan? agentDuration = null;
        bool addAgentChat = false;
        string? resolvedSessionId = null;
        var agentFinalMarked = false;

        lock (_voiceTimelineGate)
        {
            _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = now };
            if (!string.IsNullOrWhiteSpace(sessionId))
                _voiceTimeline.SessionId = sessionId;
            resolvedSessionId = _voiceTimeline.SessionId;

            if (_voiceTimeline.AgentReadyAtUtc is null)
            {
                _voiceTimeline.AgentReadyAtUtc = now;
                agentFinalMarked = true;
            }

            var anchor =
                _voiceTimeline.AgentStartedAtUtc ??
                _voiceTimeline.TranscriptReadyAtUtc ??
                _voiceTimeline.MicReleasedAtUtc ??
                _voiceTimeline.StartedAtUtc;
            agentDuration = now - anchor;

            if (!string.IsNullOrWhiteSpace(trimmed) && !_voiceTimeline.AgentMessageAdded)
            {
                _voiceTimeline.AgentMessageAdded = true;
                addAgentChat = true;
            }
        }

        MirrorRecentMcpAuditToVoiceActivity();

        if (addAgentChat)
        {
            AppendVoiceChatMessage(ChatMessageRole.Assistant, trimmed);
            AppendVoiceActivity($"Agent said: {TruncateForLog(trimmed, 180)}", LogEntryKind.Info);
        }

        if (guardrailsUsed)
        {
            AppendVoiceActivity("First principles thinking used.", LogEntryKind.Info);
            foreach (var line in guardrailsRationale.Take(3))
                AppendVoiceActivity(line, LogEntryKind.Info);
        }

        if (hasTokenUsage)
            UpdateUiTokenUsageTicker(tokensIn, tokensOut, contextFillPercent);

        if (agentDuration is { } elapsed)
            LogVoiceTiming("Agent response ready", elapsed, resolvedSessionId, LogEntryKind.Info);
        if (agentFinalMarked)
            LogVoiceStageTimestamp("t_agent_final", now, resolvedSessionId);
    }

    private void MarkVoiceSpeakingStarted(DateTimeOffset timestampUtc, string? sessionIdHint = null)
    {
        string? sessionId = null;
        bool firstSpeakingTick = false;

        lock (_voiceTimelineGate)
        {
            _voiceTimeline ??= new VoiceSessionTimeline { StartedAtUtc = timestampUtc };
            if (!string.IsNullOrWhiteSpace(sessionIdHint))
                _voiceTimeline.SessionId = sessionIdHint;

            sessionId = _voiceTimeline.SessionId;
            if (_voiceTimeline.SpeakingStartedAtUtc is null)
            {
                _voiceTimeline.SpeakingStartedAtUtc = timestampUtc;
                firstSpeakingTick = true;
            }
        }

        if (firstSpeakingTick)
        {
            AppendVoiceActivity($"Playback started{FormatSessionSuffix(sessionId)}.", LogEntryKind.Info);
            LogVoiceStageTimestamp("t_playback_start", timestampUtc, sessionId);
        }
    }

    private void CompleteVoiceTimeline(VoiceState previousState, string? reason)
    {
        VoiceSessionTimeline? timeline;
        var now = DateTimeOffset.UtcNow;

        lock (_voiceTimelineGate)
        {
            timeline = _voiceTimeline;
            _voiceTimeline = null;
        }

        if (timeline is null)
            return;

        var sessionId = timeline.SessionId;
        if (timeline.SpeakingStartedAtUtc is { } speakingStarted &&
            previousState == VoiceState.Speaking)
        {
            LogVoiceTiming("Playback finished", now - speakingStarted, sessionId, LogEntryKind.Info);
        }

        if (timeline.MicReleasedAtUtc is { } micReleased)
            LogVoiceTiming("Voice roundtrip (mic up -> idle)", now - micReleased, sessionId, LogEntryKind.Info);

        LogVoiceTiming("Voice session total", now - timeline.StartedAtUtc, sessionId, LogEntryKind.Info);

        var audioCaptureDurationMs = TryElapsedMs(timeline.StartedAtUtc, timeline.MicReleasedAtUtc);
        var asrLatencyMs = TryElapsedMs(timeline.AsrStartedAtUtc, timeline.TranscriptReadyAtUtc);
        var endToEndToPlaybackStartMs = TryElapsedMs(timeline.StartedAtUtc, timeline.SpeakingStartedAtUtc);

        WriteVoiceAuditNonBlocking("VOICE_TURN_TIMING_SUMMARY", "ok", new Dictionary<string, object>
        {
            ["sessionId"] = sessionId,
            ["t_mic_down"] = timeline.StartedAtUtc.ToString("O"),
            ["t_first_audio_frame"] = timeline.FirstAudioFrameAtUtc?.ToString("O") ?? "",
            ["t_mic_up"] = timeline.MicReleasedAtUtc?.ToString("O") ?? "",
            ["t_asr_start"] = timeline.AsrStartedAtUtc?.ToString("O") ?? "",
            ["t_asr_first_token"] = timeline.AsrFirstTokenAtUtc?.ToString("O") ?? "",
            ["t_asr_final"] = timeline.TranscriptReadyAtUtc?.ToString("O") ?? "",
            ["t_agent_start"] = timeline.AgentStartedAtUtc?.ToString("O") ?? "",
            ["t_agent_final"] = timeline.AgentReadyAtUtc?.ToString("O") ?? "",
            ["t_tts_start"] = timeline.TtsStartedAtUtc?.ToString("O") ?? "",
            ["t_playback_start"] = timeline.SpeakingStartedAtUtc?.ToString("O") ?? "",
            ["audio_capture_duration_ms"] = audioCaptureDurationMs ?? -1L,
            ["asr_latency_ms"] = asrLatencyMs ?? -1L,
            ["end_to_end_to_playback_start_ms"] = endToEndToPlaybackStartMs ?? -1L
        });

        if (!string.IsNullOrWhiteSpace(reason) &&
            reason.Contains("fault", StringComparison.OrdinalIgnoreCase))
        {
            AppendVoiceActivity($"Voice ended with fault{FormatSessionSuffix(sessionId)}: {reason}", LogEntryKind.Error);
        }
    }

    private string? GetCurrentVoiceSessionId()
    {
        lock (_voiceTimelineGate)
            return _voiceTimeline?.SessionId;
    }

    private void AppendVoiceActivity(string text, LogEntryKind kind)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_commandPaletteViewModel is null)
            {
                lock (_pendingVoiceUiGate)
                    _pendingVoiceLogs.Add((kind, text));
                return;
            }

            _commandPaletteViewModel.AddVoiceLog(text, kind);
        });
    }

    private void AppendVoiceChatMessage(ChatMessageRole role, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        Dispatcher.InvokeAsync(() =>
        {
            if (_commandPaletteViewModel is null)
            {
                lock (_pendingVoiceUiGate)
                    _pendingVoiceMessages.Add((role, text));
                return;
            }

            if (role == ChatMessageRole.User)
            {
                _commandPaletteViewModel.AddVoiceUserMessage(text);
                return;
            }

            _commandPaletteViewModel.AddVoiceAssistantMessage(text);
        });
    }

    private void UpdateUiTokenUsageTicker(int tokensIn, int tokensOut, int contextFillPercent)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _commandPaletteViewModel?.UpdateTokenUsageTicker(tokensIn, tokensOut, contextFillPercent);
        });
    }

    private void FlushPendingVoiceUi()
    {
        if (_commandPaletteViewModel is null)
            return;

        List<(ChatMessageRole Role, string Content)> pendingMessages;
        List<(LogEntryKind Kind, string Text)> pendingLogs;

        lock (_pendingVoiceUiGate)
        {
            pendingMessages = [.. _pendingVoiceMessages];
            pendingLogs = [.. _pendingVoiceLogs];
            _pendingVoiceMessages.Clear();
            _pendingVoiceLogs.Clear();
        }

        foreach (var (kind, text) in pendingLogs)
            _commandPaletteViewModel.AddVoiceLog(text, kind);

        foreach (var (role, content) in pendingMessages)
        {
            if (role == ChatMessageRole.User)
                _commandPaletteViewModel.AddVoiceUserMessage(content);
            else
                _commandPaletteViewModel.AddVoiceAssistantMessage(content);
        }
    }

    private void MirrorRecentMcpAuditToVoiceActivity()
    {
        var logger = _auditLogger;
        if (logger is null)
            return;

        _ = Task.Run(() =>
        {
            IReadOnlyList<AuditEvent> tail;
            try
            {
                tail = logger.ReadTail(300);
            }
            catch
            {
                return;
            }

            var pending = new List<(LogEntryKind Kind, string Text)>();
            lock (_mcpAuditMirrorGate)
            {
                foreach (var evt in tail)
                {
                    if (!IsMcpToolAuditEvent(evt))
                        continue;
                    if (evt.Timestamp < _mcpAuditMirrorCutoffUtc)
                        continue;

                    var dedupeKey = BuildMcpAuditDedupeKey(evt);
                    if (_mirroredMcpAuditKeys.Contains(dedupeKey))
                        continue;

                    _mirroredMcpAuditKeys.Add(dedupeKey);
                    _mirroredMcpAuditKeyOrder.Enqueue(dedupeKey);

                    while (_mirroredMcpAuditKeyOrder.Count > MaxMirroredMcpAuditKeys)
                    {
                        var oldest = _mirroredMcpAuditKeyOrder.Dequeue();
                        _mirroredMcpAuditKeys.Remove(oldest);
                    }

                    if (TryFormatMcpAuditEntry(evt, out var kind, out var text))
                        pending.Add((kind, text));
                }
            }

            foreach (var (kind, text) in pending)
                AppendVoiceActivity(text, kind);
        });
    }

    private static bool IsMcpToolAuditEvent(AuditEvent evt)
    {
        if (!string.Equals(evt.Actor, "agent", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(evt.Action, "MCP_TOOL_CALL_START", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(evt.Action, "MCP_TOOL_CALL_END", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildMcpAuditDedupeKey(AuditEvent evt)
    {
        var requestId = ReadAuditDetailString(evt.Details, "request_id");
        if (!string.IsNullOrWhiteSpace(requestId))
            return $"{requestId}:{evt.Action}:{evt.Result}";

        return string.Join("|",
            evt.Timestamp.ToUnixTimeMilliseconds().ToString(),
            evt.Action ?? "",
            evt.Target ?? "",
            evt.Result ?? "",
            TruncateForLog(ReadAuditDetailString(evt.Details, "input_summary"), 60),
            TruncateForLog(ReadAuditDetailString(evt.Details, "output_summary"), 60));
    }

    private static bool TryFormatMcpAuditEntry(
        AuditEvent evt,
        out LogEntryKind kind,
        out string text)
    {
        var toolName = string.IsNullOrWhiteSpace(evt.Target)
            ? ReadAuditDetailString(evt.Details, "tool_name_canonical")
            : evt.Target!;
        if (string.IsNullOrWhiteSpace(toolName))
            toolName = "unknown_tool";

        if (string.Equals(evt.Action, "MCP_TOOL_CALL_START", StringComparison.OrdinalIgnoreCase))
        {
            var inputSummary = TruncateForLog(ReadAuditDetailString(evt.Details, "input_summary"), 140);
            text = string.IsNullOrWhiteSpace(inputSummary)
                ? $"MCP -> {toolName}"
                : $"MCP -> {toolName}({inputSummary})";
            kind = LogEntryKind.ToolInput;
            return true;
        }

        if (!string.Equals(evt.Action, "MCP_TOOL_CALL_END", StringComparison.OrdinalIgnoreCase))
        {
            kind = LogEntryKind.Info;
            text = "";
            return false;
        }

        var durationMs = ReadAuditDetailInt64(evt.Details, "duration_ms");
        var durationSuffix = durationMs is long ms
            ? $" • {FormatDuration(TimeSpan.FromMilliseconds(Math.Max(0, ms)))}"
            : "";

        var result = string.IsNullOrWhiteSpace(evt.Result) ? "ok" : evt.Result.Trim().ToLowerInvariant();
        if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            var outputSummary = TruncateForLog(ReadAuditDetailString(evt.Details, "output_summary"), 180);
            var outputSuffix = string.IsNullOrWhiteSpace(outputSummary)
                ? ""
                : $" • {outputSummary}";
            text = $"MCP <- {toolName} ok{durationSuffix}{outputSuffix}";
            kind = LogEntryKind.ToolOutput;
            return true;
        }

        var errorSummary = ReadAuditDetailString(evt.Details, "error_message");
        if (string.IsNullOrWhiteSpace(errorSummary))
            errorSummary = ReadAuditDetailString(evt.Details, "output_summary");
        errorSummary = TruncateForLog(errorSummary, 180);
        var detailSuffix = string.IsNullOrWhiteSpace(errorSummary)
            ? ""
            : $" • {errorSummary}";

        text = $"MCP <- {toolName} {result}{durationSuffix}{detailSuffix}";
        kind = LogEntryKind.Error;
        return true;
    }

    private static string ReadAuditDetailString(Dictionary<string, object>? details, string key)
    {
        if (details is null || !details.TryGetValue(key, out var value) || value is null)
            return "";

        return value switch
        {
            string s => s,
            JsonElement elem => elem.ValueKind switch
            {
                JsonValueKind.String => elem.GetString() ?? "",
                JsonValueKind.Number => elem.TryGetInt64(out var n)
                    ? n.ToString()
                    : elem.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                JsonValueKind.Undefined => "",
                _ => elem.ToString() ?? ""
            },
            _ => value.ToString() ?? ""
        };
    }

    private static long? ReadAuditDetailInt64(Dictionary<string, object>? details, string key)
    {
        if (details is null || !details.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            long l => l,
            int i => i,
            JsonElement elem when elem.ValueKind == JsonValueKind.Number && elem.TryGetInt64(out var n) => n,
            JsonElement elem when elem.ValueKind == JsonValueKind.String &&
                                  long.TryParse(elem.GetString(), out var parsed) => parsed,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    private void LogVoiceTiming(
        string step,
        TimeSpan elapsed,
        string? sessionId,
        LogEntryKind kind)
    {
        var safeElapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        var text = $"{step}{FormatSessionSuffix(sessionId)} • {FormatDuration(safeElapsed)}";
        AppendVoiceActivity(text, kind);

        WriteVoiceAuditNonBlocking("VOICE_STEP_TIMING", "ok", new Dictionary<string, object>
        {
            ["step"] = step,
            ["elapsedMs"] = (long)Math.Round(safeElapsed.TotalMilliseconds),
            ["sessionId"] = sessionId ?? ""
        });
    }

    private void LogVoiceStageTimestamp(
        string stage,
        DateTimeOffset timestampUtc,
        string? sessionId,
        Dictionary<string, object>? extraDetails = null)
    {
        var details = new Dictionary<string, object>
        {
            ["stage"] = stage,
            ["sessionId"] = sessionId ?? "",
            ["timestampUtc"] = timestampUtc.ToString("O")
        };

        if (extraDetails is not null)
        {
            foreach (var pair in extraDetails)
                details[pair.Key] = pair.Value;
        }

        WriteVoiceAuditNonBlocking("VOICE_STAGE_TIMESTAMP", "ok", details);
    }

    private static long? TryElapsedMs(DateTimeOffset start, DateTimeOffset? end)
    {
        if (end is null)
            return null;

        var elapsed = end.Value - start;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return (long)Math.Round(elapsed.TotalMilliseconds);
    }

    private static long? TryElapsedMs(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null || end is null)
            return null;

        var elapsed = end.Value - start.Value;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return (long)Math.Round(elapsed.TotalMilliseconds);
    }

    private void WriteVoiceAuditNonBlocking(
        string action,
        string result,
        Dictionary<string, object> details)
    {
        var logger = _auditLogger;
        if (logger is null)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                logger.Append(new AuditEvent
                {
                    Actor = "voice",
                    Action = action,
                    Result = result,
                    Details = details
                });
            }
            catch
            {
                // Best effort only. Voice loop must never stall on diagnostics writes.
            }
        });
    }

    private static string FormatSessionSuffix(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "";

        return $" [{sessionId}]";
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds >= 10)
            return $"{elapsed.TotalSeconds:F1}s";
        if (elapsed.TotalSeconds >= 1)
            return $"{elapsed.TotalSeconds:F2}s";
        return $"{Math.Round(elapsed.TotalMilliseconds):0}ms";
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var compact = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        if (compact.Length <= maxLength)
            return compact;
        return compact[..maxLength] + "…";
    }

    private void RebindPushToTalkInput(AudioSettings audioSettings)
    {
        if (_auditLogger is null)
            return;

        if (_pttService is not null)
        {
            _pttService.MicDown -= OnPttMicDown;
            _pttService.MicUp -= OnPttMicUp;
            _pttService.Shutup -= OnPttShutup;
            _pttService.Dispose();
            _pttService = null;
        }

        _pttService = new PushToTalkService(
            _auditLogger,
            legacyPttKey: audioSettings.PttKey,
            pttChord: audioSettings.PttChord,
            shutupChord: audioSettings.ShutupChord);
        _pttService.MicDown += OnPttMicDown;
        _pttService.MicUp += OnPttMicUp;
        _pttService.Shutup += OnPttShutup;
        _pttService.Start();
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
            RequestShutdown,
            _voiceOrchestrator);

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

        _reasoningGuardrailsHotkeyId = _hotkeyService.Register(
            GlobalHotkeyService.Modifiers.Control | GlobalHotkeyService.Modifiers.Shift,
            GlobalHotkeyService.VirtualKeys.R,
            CycleReasoningGuardrailsMode);

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

        _auditLogger?.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "HOTKEY_REGISTERED",
            Result = _reasoningGuardrailsHotkeyId > 0 ? "ok" : "failed",
            Details = new Dictionary<string, object>
            {
                ["hotkey"] = "Ctrl+Shift+R",
                ["action"] = "Cycle First Principles Thinking"
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

    // ─────────────────────────────────────────────────────────────────────
    // Command Palette
    // ─────────────────────────────────────────────────────────────────────

    private void ShowCommandPalette()
    {
        Dispatcher.Invoke(() =>
        {
            try
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
            }
            catch (Exception ex)
            {
                _auditLogger?.Append(new AuditEvent
                {
                    Actor = "runtime",
                    Action = "COMMAND_PALETTE_OPEN_FAILED",
                    Result = "error",
                    Details = new Dictionary<string, object>
                    {
                        ["message"] = ex.Message,
                        ["errorType"] = ex.GetType().FullName ?? "unknown"
                    }
                });
            }
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

        // Wire PTT delegates so the UI button triggers the same path as the hotkey.
        viewModel.VoiceMicDown = OnPttMicDown;
        viewModel.VoiceMicUp  = OnPttMicUp;
        viewModel.VoiceShutup = OnPttShutup;

        _commandPaletteViewModel = viewModel;
        _commandPaletteViewModel.ReasoningGuardrailsMode = _settings?.Ui.ReasoningGuardrails ?? "off";
        FlushPendingVoiceUi();
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
        SqliteMemoryStore? settingsStore = null;
        if (_settings!.Memory.Enabled)
        {
            try
            {
                var dbPath = ResolveMemoryDbPath(_settings);
                settingsStore = new SqliteMemoryStore(dbPath);
                var memVm  = new MemoryBrowserViewModel(settingsStore, _auditLogger!);
                window.SetMemoryBrowserViewModel(memVm);

                // Profile Browser shares the same DB connection
                var profVm = new ProfileBrowserViewModel(settingsStore, _auditLogger!);
                window.SetProfileBrowserViewModel(profVm);
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

        // Settings panel — always available, even when memory is disabled.
        // When memory is off, profile list features are no-op.
        try
        {
            var settingsVm = new SettingsViewModel(_settings, settingsStore, _auditLogger!);
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
                _settings = updated;
                _permissionGate?.UpdateSettings(updated);

                // Propagate memory master off to orchestrator
                if (_orchestrator is not null)
                    _orchestrator.MemoryEnabled = updated.Memory.Enabled;
                if (_orchestrator is not null)
                    _orchestrator.ReasoningGuardrailsMode = updated.Ui.ReasoningGuardrails;
                if (_commandPaletteViewModel is not null)
                    _commandPaletteViewModel.ReasoningGuardrailsMode = updated.Ui.ReasoningGuardrails;

                if (_ttsService is not null)
                    _ttsService.Enabled = updated.Audio.TtsEnabled;

                _voiceHostProcessManager?.UpdateSettings(updated.Voice);

                // Propagate audio device selections to capture/playback services.
                // New device numbers take effect on the next recording/playback call.
                if (_audioCaptureService is not null)
                {
                    _audioCaptureService.DeviceNumber = AudioDeviceEnumerator.ResolveInputDeviceNumber(updated.Audio.InputDeviceName);
                    _audioCaptureService.InputGain    = Math.Clamp(updated.Audio.InputGain, 0.0, 2.0);
                }
                if (_audioPlaybackService is not null)
                    _audioPlaybackService.OutputDeviceNumber = AudioDeviceEnumerator.ResolveOutputDeviceNumber(updated.Audio.OutputDeviceName);

                RebindPushToTalkInput(updated.Audio);
            };

            window.SetSettingsViewModel(settingsVm);
        }
        catch (Exception ex)
        {
            _auditLogger?.Append(new AuditEvent
            {
                Actor = "system",
                Action = "SETTINGS_VIEWMODEL_INIT_FAILED",
                Result = ex.Message
            });
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

    private void CycleReasoningGuardrailsMode()
    {
        if (_settings is null)
            return;

        var current = NormalizeReasoningGuardrailsMode(_settings.Ui.ReasoningGuardrails);
        var next = current switch
        {
            "off" => "auto",
            "auto" => "always",
            _ => "off"
        };

        var updated = _settings with
        {
            Ui = _settings.Ui with
            {
                ReasoningGuardrails = next
            }
        };

        SettingsManager.Save(updated);
        _settings = updated;

        if (_orchestrator is not null)
            _orchestrator.ReasoningGuardrailsMode = next;
        if (_commandPaletteViewModel is not null)
            _commandPaletteViewModel.ReasoningGuardrailsMode = next;

        _auditLogger?.Append(new AuditEvent
        {
            Actor = "user",
            Action = "SETTINGS_SAVED",
            Result = $"reasoning_guardrails={next} (via tray/hotkey)"
        });

        AppendVoiceActivity($"First principles thinking set to {next}.", LogEntryKind.Info);
    }

    private static string NormalizeReasoningGuardrailsMode(string? mode)
    {
        var normalized = (mode ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" => "auto",
            "always" => "always",
            _ => "off"
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // STOP ALL / Shutdown
    // ─────────────────────────────────────────────────────────────────────

    private void StopAllAndShutdown()
    {
        _pttService?.RequestShutup();
        _voiceOrchestrator?.EnqueueShutup();
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
            _voiceOrchestrator?.EnqueueShutup();
            _pttService?.Stop();
            _ = StopLiveAsrPreviewLoopAsync(waitForDrain: false);

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
                _reasoningGuardrailsHotkeyId = -1;

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

        if (_pttService is not null)
        {
            _pttService.MicDown -= OnPttMicDown;
            _pttService.MicUp -= OnPttMicUp;
            _pttService.Shutup -= OnPttShutup;
            _pttService.Dispose();
            _pttService = null;
        }

        if (_voiceOrchestrator is not null)
        {
            _voiceOrchestrator.StateChanged -= OnVoiceStateChanged;
            _voiceOrchestrator.ProgressUpdated -= OnVoiceProgressUpdated;
            await _voiceOrchestrator.DisposeAsync();
            _voiceOrchestrator = null;
        }

        await StopLiveAsrPreviewLoopAsync(waitForDrain: false);

        if (_audioPlaybackService is not null)
            _audioPlaybackService.PlaybackStarted -= OnPlaybackStarted;
        _audioPlaybackService?.Dispose();
        _audioPlaybackService = null;

        if (_audioCaptureService is not null)
            _audioCaptureService.FirstAudioFrameCaptured -= OnCaptureFirstFrameCaptured;
        _audioCaptureService?.Dispose();
        _audioCaptureService = null;

        if (_asrClient is not null)
        {
            _asrClient.TranscriptReceived -= OnAsrTranscriptReceived;
            _asrClient.TimingUpdated -= OnAsrTimingUpdated;
        }
        _asrClient?.Dispose();
        _asrClient = null;

        if (_localTtsClient is not null)
            _localTtsClient.TimingUpdated -= OnTtsTimingUpdated;
        _localTtsClient?.Dispose();
        _localTtsClient = null;

        if (_voiceHostProcessManager is not null)
        {
            await _voiceHostProcessManager.DisposeAsync();
            _voiceHostProcessManager = null;
        }

        _voiceAgentService = null;

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
        _commandPaletteViewModel = null;

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
