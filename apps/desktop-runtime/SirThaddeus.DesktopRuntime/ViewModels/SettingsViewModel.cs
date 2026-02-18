using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using NAudio.Wave;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.DesktopRuntime.Services;
using SirThaddeus.Memory;
using SirThaddeus.Memory.Sqlite;
using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.DesktopRuntime.ViewModels;

/// <summary>
/// ViewModel for the Settings tab. Surfaces commonly adjusted
/// configuration values and the active profile dropdown.
///
/// Changes are written to settings.json on every save. The active
/// profile selection is persisted so the assistant knows who it's
/// talking to across sessions — no "who is this?" required.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IAuditLogger    _audit;
    private readonly SqliteMemoryStore? _store;
    private readonly YouTubeJobsHttpClient _youtubeJobsClient;
    private readonly VoiceHostProcessManager? _voiceHostProcessManager;

    private AppSettings _settings;

    // ─── Backing fields ──────────────────────────────────────────────

    // LLM
    private string _llmBaseUrl     = "";
    private string _llmModel       = "";
    private int    _llmMaxTokens   = 2048;
    private double _llmTemperature = 0.7;

    // Audio
    private bool   _ttsEnabled   = true;
    private string _pttKey       = "F13";
    private string _pttChord     = "Ctrl+Shift+Space";
    private string _shutupChord  = "Ctrl+Shift+Escape";
    private bool   _voiceHostEnabled = true;
    private string _voiceHostBaseUrl = "http://127.0.0.1:17845";
    private int    _voiceHostStartupTimeoutMs = 20000;
    private string _voiceHostHealthPath = "/health";
    private string _voiceTtsEngine = "windows";
    private string _voiceTtsModelId = "";
    private string _voiceTtsVoiceId = "";
    private string _voiceSttEngine = "faster-whisper";
    private string _voiceSttModelId = "base";
    private bool   _voicePreferLocalTts = true;
    private int    _voiceAsrTimeoutMs = 45000;
    private int    _voiceAgentTimeoutMs = 90000;
    private int    _voiceSpeakingTimeoutMs = 90000;
    private string _youtubeAsrProvider = "qwen3asr";
    private string _youtubeAsrModelId = "qwen-asr-1.6b";
    private string _youtubeLanguageHint = "en-us";
    private string _youtubeDraftTone = "professional";
    private bool _youtubeKeepAudio;
    private string _youtubeUrl = "";
    private string _youtubeJobId = "";
    private string _youtubeJobStatus = "Idle";
    private string _youtubeJobStage = "";
    private int _youtubeJobProgressPercent;
    private bool _youtubeJobIsRunning;
    private string _youtubeSummary = "";
    private string _youtubeTranscriptPath = "";
    private string _youtubeOutputDir = "";
    private string _youtubeErrorMessage = "";
    private CancellationTokenSource? _youtubeJobPollCts;

    // VoiceHost health panel
    private string _voiceHostStatusText   = "Unknown";
    private bool   _voiceHostIsReachable;
    private bool   _voiceHostIsReady;
    private bool   _voiceHostAsrReady;
    private bool   _voiceHostTtsReady;
    private string _voiceHostVersion      = "";
    private string _voiceHostMessage      = "";
    private bool   _voiceHostIsBusy;
    private CancellationTokenSource? _voiceHostHealthPollCts;

    // Memory
    private bool   _memoryEnabled     = true;
    private bool   _embeddingsEnabled = true;

    // MCP Permissions
    private string _mcpPermDeveloperOverride = "none";
    private string _mcpPermScreen            = "ask";
    private string _mcpPermFiles             = "ask";
    private string _mcpPermSystem            = "ask";
    private string _mcpPermWeb               = "ask";
    private string _mcpPermMemoryRead        = "always";
    private string _mcpPermMemoryWrite       = "ask";

    // Weather
    private string _weatherUserAgent =
        "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)";
    private string _weatherPreferredUnits = "imperial";
    private string _reasoningGuardrails = "off";

    // Location (manual city/ZIP)
    private string _locationLabel = "";

    // Audio Devices
    private AudioDeviceInfo? _selectedInputDevice;
    private AudioDeviceInfo? _selectedOutputDevice;
    private double _inputGain = 1.0;

    // Mic Test
    private readonly object _testGate = new();
    private WaveInEvent?    _testWaveIn;
    private MemoryStream?   _testPcmBuffer;
    private byte[]?         _testRecordingBytes;
    private double          _micTestLevel;
    private bool            _isTestingMic;
    private string          _micTestStatus = "";
    private CancellationTokenSource? _testTimerCts;

    // Profile
    private ProfileOption? _selectedProfile;
    private PersonalityOption? _selectedPersonality;
    private string _personalityProfilesDirectory = "";
    private bool _suppressPersonalitySelectionSave;
    private string         _statusText = "";
    private readonly PersonalityProfileStore _personalityStore = new();

    // ─── Public Properties ───────────────────────────────────────────

    // LLM
    public string LlmBaseUrl     { get => _llmBaseUrl;     set { if (SetProperty(ref _llmBaseUrl, value))     MarkDirty(); } }
    public string LlmModel       { get => _llmModel;       set { if (SetProperty(ref _llmModel, value))       MarkDirty(); } }
    public int    LlmMaxTokens   { get => _llmMaxTokens;   set { if (SetProperty(ref _llmMaxTokens, value))   MarkDirty(); } }
    public double LlmTemperature { get => _llmTemperature; set { if (SetProperty(ref _llmTemperature, value)) MarkDirty(); } }

    // Audio
    public bool   TtsEnabled     { get => _ttsEnabled;     set { if (SetProperty(ref _ttsEnabled, value))     MarkDirty(); } }
    public string PttKey         { get => _pttKey;          set { if (SetProperty(ref _pttKey, value))         MarkDirty(); } }
    public string PttChord       { get => _pttChord;        set { if (SetProperty(ref _pttChord, value))       MarkDirty(); } }
    public string ShutupChord    { get => _shutupChord;     set { if (SetProperty(ref _shutupChord, value))    MarkDirty(); } }
    public bool VoiceHostEnabled { get => _voiceHostEnabled; set { if (SetProperty(ref _voiceHostEnabled, value)) MarkDirty(); } }
    public string VoiceHostBaseUrl { get => _voiceHostBaseUrl; set { if (SetProperty(ref _voiceHostBaseUrl, value)) MarkDirty(); } }
    public int VoiceHostStartupTimeoutMs { get => _voiceHostStartupTimeoutMs; set { if (SetProperty(ref _voiceHostStartupTimeoutMs, value)) MarkDirty(); } }
    public string VoiceHostHealthPath { get => _voiceHostHealthPath; set { if (SetProperty(ref _voiceHostHealthPath, value)) MarkDirty(); } }
    public string VoiceTtsEngine { get => _voiceTtsEngine; set { if (SetProperty(ref _voiceTtsEngine, value)) MarkDirty(); } }
    public string VoiceTtsModelId { get => _voiceTtsModelId; set { if (SetProperty(ref _voiceTtsModelId, value)) MarkDirty(); } }
    public string VoiceTtsVoiceId { get => _voiceTtsVoiceId; set { if (SetProperty(ref _voiceTtsVoiceId, value)) { MarkDirty(); OnPropertyChanged(nameof(SelectedKokoroVoice)); } } }
    public string VoiceSttEngine { get => _voiceSttEngine; set { if (SetProperty(ref _voiceSttEngine, value)) MarkDirty(); } }
    public string VoiceSttModelId { get => _voiceSttModelId; set { if (SetProperty(ref _voiceSttModelId, value)) MarkDirty(); } }
    public bool VoicePreferLocalTts { get => _voicePreferLocalTts; set { if (SetProperty(ref _voicePreferLocalTts, value)) MarkDirty(); } }
    public int VoiceAsrTimeoutMs { get => _voiceAsrTimeoutMs; set { if (SetProperty(ref _voiceAsrTimeoutMs, value)) MarkDirty(); } }
    public int VoiceAgentTimeoutMs { get => _voiceAgentTimeoutMs; set { if (SetProperty(ref _voiceAgentTimeoutMs, value)) MarkDirty(); } }
    public int VoiceSpeakingTimeoutMs { get => _voiceSpeakingTimeoutMs; set { if (SetProperty(ref _voiceSpeakingTimeoutMs, value)) MarkDirty(); } }
    public string YouTubeAsrProvider { get => _youtubeAsrProvider; set { if (SetProperty(ref _youtubeAsrProvider, value)) MarkDirty(); } }
    public string YouTubeAsrModelId { get => _youtubeAsrModelId; set { if (SetProperty(ref _youtubeAsrModelId, value)) MarkDirty(); } }
    public string YouTubeLanguageHint { get => _youtubeLanguageHint; set { if (SetProperty(ref _youtubeLanguageHint, value)) MarkDirty(); } }
    public string YouTubeDraftTone { get => _youtubeDraftTone; set { if (SetProperty(ref _youtubeDraftTone, value)) MarkDirty(); } }
    public bool YouTubeKeepAudio { get => _youtubeKeepAudio; set { if (SetProperty(ref _youtubeKeepAudio, value)) MarkDirty(); } }
    public string YouTubeUrl { get => _youtubeUrl; set => SetProperty(ref _youtubeUrl, value); }
    public string YouTubeJobId { get => _youtubeJobId; private set => SetProperty(ref _youtubeJobId, value); }
    public string YouTubeJobStatus { get => _youtubeJobStatus; private set => SetProperty(ref _youtubeJobStatus, value); }
    public string YouTubeJobStage { get => _youtubeJobStage; private set => SetProperty(ref _youtubeJobStage, value); }
    public int YouTubeJobProgressPercent { get => _youtubeJobProgressPercent; private set => SetProperty(ref _youtubeJobProgressPercent, value); }
    public bool YouTubeJobIsRunning
    {
        get => _youtubeJobIsRunning;
        private set
        {
            if (SetProperty(ref _youtubeJobIsRunning, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }
    public string YouTubeSummary { get => _youtubeSummary; private set => SetProperty(ref _youtubeSummary, value); }
    public string YouTubeTranscriptPath { get => _youtubeTranscriptPath; private set => SetProperty(ref _youtubeTranscriptPath, value); }
    public string YouTubeOutputDir { get => _youtubeOutputDir; private set => SetProperty(ref _youtubeOutputDir, value); }
    public string YouTubeErrorMessage { get => _youtubeErrorMessage; private set => SetProperty(ref _youtubeErrorMessage, value); }

    // ─── VoiceHost Health ───────────────────────────────────────────
    public string VoiceHostStatusText  { get => _voiceHostStatusText;  private set => SetProperty(ref _voiceHostStatusText, value); }
    public bool   VoiceHostIsReachable { get => _voiceHostIsReachable; private set => SetProperty(ref _voiceHostIsReachable, value); }
    public bool   VoiceHostIsReady     { get => _voiceHostIsReady;     private set => SetProperty(ref _voiceHostIsReady, value); }
    public bool   VoiceHostAsrReady    { get => _voiceHostAsrReady;    private set => SetProperty(ref _voiceHostAsrReady, value); }
    public bool   VoiceHostTtsReady    { get => _voiceHostTtsReady;    private set => SetProperty(ref _voiceHostTtsReady, value); }
    public string VoiceHostVersion     { get => _voiceHostVersion;     private set => SetProperty(ref _voiceHostVersion, value); }
    public string VoiceHostMessage     { get => _voiceHostMessage;     private set => SetProperty(ref _voiceHostMessage, value); }
    public bool   VoiceHostIsBusy      { get => _voiceHostIsBusy;      private set { if (SetProperty(ref _voiceHostIsBusy, value)) CommandManager.InvalidateRequerySuggested(); } }

    // Memory
    public bool MemoryEnabled
    {
        get => _memoryEnabled;
        set
        {
            if (SetProperty(ref _memoryEnabled, value))
            {
                MarkDirty();
                // Memory master off disables memory permission dropdowns
                OnPropertyChanged(nameof(McpPermMemoryRead));
                OnPropertyChanged(nameof(McpPermMemoryWrite));
            }
        }
    }
    public bool   EmbeddingsEnabled { get => _embeddingsEnabled; set { if (SetProperty(ref _embeddingsEnabled, value)) MarkDirty(); } }

    // MCP Permissions
    public string McpPermDeveloperOverride { get => _mcpPermDeveloperOverride; set { if (SetProperty(ref _mcpPermDeveloperOverride, value)) MarkDirty(); } }
    public string McpPermScreen            { get => _mcpPermScreen;            set { if (SetProperty(ref _mcpPermScreen, value))            MarkDirty(); } }
    public string McpPermFiles             { get => _mcpPermFiles;             set { if (SetProperty(ref _mcpPermFiles, value))             MarkDirty(); } }
    public string McpPermSystem            { get => _mcpPermSystem;            set { if (SetProperty(ref _mcpPermSystem, value))            MarkDirty(); } }
    public string McpPermWeb               { get => _mcpPermWeb;               set { if (SetProperty(ref _mcpPermWeb, value))               MarkDirty(); } }
    public string McpPermMemoryRead        { get => _mcpPermMemoryRead;        set { if (SetProperty(ref _mcpPermMemoryRead, value))        MarkDirty(); } }
    public string McpPermMemoryWrite       { get => _mcpPermMemoryWrite;       set { if (SetProperty(ref _mcpPermMemoryWrite, value))       MarkDirty(); } }

    // Weather
    public string WeatherUserAgent         { get => _weatherUserAgent;         set { if (SetProperty(ref _weatherUserAgent, value))         MarkDirty(); } }
    public string WeatherPreferredUnits    { get => _weatherPreferredUnits;    set { if (SetProperty(ref _weatherPreferredUnits, value))    MarkDirty(); } }

    // Location (manual city/ZIP)
    public string LocationLabel { get => _locationLabel; set { if (SetProperty(ref _locationLabel, value)) MarkDirty(); } }

    public string ReasoningGuardrails
    {
        get => _reasoningGuardrails;
        set
        {
            var normalized = NormalizeReasoningGuardrailsMode(value);
            if (SetProperty(ref _reasoningGuardrails, normalized))
                MarkDirty();
        }
    }

    // ─── Audio Devices ──────────────────────────────────────────────

    public ObservableCollection<AudioDeviceInfo> AvailableInputDevices  { get; } = new();
    public ObservableCollection<AudioDeviceInfo> AvailableOutputDevices { get; } = new();
    public IReadOnlyList<string> AvailableDraftTones { get; } = new[] { "professional", "playful", "direct" };

    // ─── Kokoro Voice Catalog ────────────────────────────────────────

    /// <summary>
    /// Installed Kokoro voice IDs discovered from the local filesystem.
    /// Bound to the TTS Voice dropdown in the settings panel.
    /// </summary>
    public ObservableCollection<string> AvailableKokoroVoices { get; } = new();

    /// <summary>
    /// Currently selected Kokoro voice. Two-way synced with
    /// <see cref="VoiceTtsVoiceId"/> so persistence is unchanged.
    /// </summary>
    public string? SelectedKokoroVoice
    {
        get => string.IsNullOrWhiteSpace(_voiceTtsVoiceId) ? null : _voiceTtsVoiceId;
        set
        {
            var normalized = (value ?? "").Trim();
            VoiceTtsVoiceId = normalized;
            OnPropertyChanged();
        }
    }

    public AudioDeviceInfo? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set { if (SetProperty(ref _selectedInputDevice, value)) MarkDirty(); }
    }

    public AudioDeviceInfo? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set { if (SetProperty(ref _selectedOutputDevice, value)) MarkDirty(); }
    }

    public double InputGain
    {
        get => _inputGain;
        set { if (SetProperty(ref _inputGain, Math.Clamp(value, 0.0, 2.0))) MarkDirty(); }
    }

    // ─── Mic Test ───────────────────────────────────────────────────

    public double MicTestLevel
    {
        get => _micTestLevel;
        private set => SetProperty(ref _micTestLevel, value);
    }

    public bool IsTestingMic
    {
        get => _isTestingMic;
        private set
        {
            if (SetProperty(ref _isTestingMic, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string MicTestStatus
    {
        get => _micTestStatus;
        set => SetProperty(ref _micTestStatus, value);
    }

    public bool HasTestRecording => _testRecordingBytes is { Length: > 100 };

    public ICommand TestMicCommand          { get; }
    public ICommand StopTestMicCommand      { get; }
    public ICommand PlayTestRecordingCommand { get; }
    public ICommand RefreshDevicesCommand    { get; }
    public ICommand StartYouTubeJobCommand { get; }
    public ICommand CancelYouTubeJobCommand { get; }
    public ICommand StartVoiceHostCommand    { get; }
    public ICommand StopVoiceHostCommand     { get; }
    public ICommand RefreshVoiceHostCommand  { get; }
    public ICommand AddPersonalityCommand    { get; }
    public ICommand EditPersonalityCommand   { get; }
    public ICommand DuplicatePersonalityCommand { get; }
    public ICommand ResetPersonalityCommand  { get; }

    // Profile dropdown
    public ObservableCollection<ProfileOption> AvailableProfiles { get; } = new();
    public ObservableCollection<PersonalityOption> AvailablePersonalities { get; } = new();

    public ProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                LoadLocationForProfile(value?.ProfileId);
                SaveProfileSelectionOnly();
            }
        }
    }

    public PersonalityOption? SelectedPersonality
    {
        get => _selectedPersonality;
        set
        {
            if (!SetProperty(ref _selectedPersonality, value))
                return;

            if (_suppressPersonalitySelectionSave)
                return;

            SavePersonalitySelectionOnly();
        }
    }

    public string PersonalityProfilesDirectory
    {
        get => _personalityProfilesDirectory;
        private set => SetProperty(ref _personalityProfilesDirectory, value);
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    // ─── Commands ────────────────────────────────────────────────────

    public ICommand SaveCommand    { get; }
    public ICommand RefreshCommand { get; }

    // ─── Raised when settings change ──────────────────────────────────

    /// <summary>
    /// Raised when the active profile selection changes so the host
    /// can propagate the choice to the agent and MCP layers.
    /// </summary>
    public event Action<string?>? ActiveProfileChanged;

    /// <summary>
    /// Raised when the active personality changes.
    /// </summary>
    public event Action<string>? ActivePersonalityChanged;

    /// <summary>
    /// Raised after settings are saved so the runtime can swap the
    /// immutable permission snapshot in <c>WpfPermissionGate</c>.
    /// </summary>
    public event Action<AppSettings>? SettingsChanged;

    // ─── Constructor ─────────────────────────────────────────────────

    public SettingsViewModel(
        AppSettings settings,
        SqliteMemoryStore? store,
        IAuditLogger audit,
        YouTubeJobsHttpClient? youtubeJobsClient = null,
        VoiceHostProcessManager? voiceHostProcessManager = null)
    {
        _settings = settings;
        _store    = store;
        _audit    = audit;
        _voiceHostProcessManager = voiceHostProcessManager;
        _youtubeJobsClient = youtubeJobsClient ?? new YouTubeJobsHttpClient(
            () => _settings.Voice.GetVoiceHostBaseUrl(),
            _audit);

        SaveCommand    = new RelayCommand(_ => SaveSettings());
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync());

        TestMicCommand           = new RelayCommand(_ => StartMicTest(),       _ => !IsTestingMic);
        StopTestMicCommand       = new RelayCommand(_ => StopMicTest(),        _ => IsTestingMic);
        PlayTestRecordingCommand = new RelayCommand(_ => PlayTestRecording(),  _ => HasTestRecording && !IsTestingMic);
        RefreshDevicesCommand    = new RelayCommand(_ => RefreshAudioDevices());
        StartYouTubeJobCommand   = new AsyncRelayCommand(StartYouTubeJobAsync, () => !YouTubeJobIsRunning);
        CancelYouTubeJobCommand  = new AsyncRelayCommand(CancelYouTubeJobAsync, () => YouTubeJobIsRunning);
        StartVoiceHostCommand    = new AsyncRelayCommand(StartVoiceHostAsync,  () => !VoiceHostIsBusy);
        StopVoiceHostCommand     = new AsyncRelayCommand(StopVoiceHostAsync,   () => !VoiceHostIsBusy);
        RefreshVoiceHostCommand  = new AsyncRelayCommand(RefreshVoiceHostHealthAsync, () => !VoiceHostIsBusy);
        AddPersonalityCommand = new RelayCommand(_ => AddNewPersonality());
        EditPersonalityCommand = new RelayCommand(_ => EditSelectedPersonality());
        DuplicatePersonalityCommand = new RelayCommand(_ => DuplicateSelectedPersonality());
        ResetPersonalityCommand = new RelayCommand(_ => ResetPersonalityToDefault());

        LoadFromSettings(settings);
        LoadAudioDevices();
        LoadPersonalityProfiles();
    }

    // ─── Load / Save ─────────────────────────────────────────────────

    /// <summary>
    /// Populates the form fields from the current settings and reloads
    /// the profile dropdown from the memory database.
    /// </summary>
    public async Task LoadAsync()
    {
        _settings = SettingsManager.Load();
        LoadFromSettings(_settings);
        LoadPersonalityProfiles();
        LoadAudioDevices();
        await LoadProfilesAsync();
        ClearDirty();
        StatusText = "Settings loaded.";
    }

    private void LoadFromSettings(AppSettings s)
    {
        _llmBaseUrl     = s.Llm.BaseUrl;
        _llmModel       = s.Llm.Model;
        _llmMaxTokens   = s.Llm.MaxTokens;
        _llmTemperature = s.Llm.Temperature;

        _ttsEnabled     = s.Audio.TtsEnabled;
        _pttKey         = s.Audio.PttKey;
        _pttChord       = s.Audio.PttChord;
        _shutupChord    = s.Audio.ShutupChord;
        _voiceHostEnabled = s.Voice.VoiceHostEnabled;
        _voiceHostBaseUrl = s.Voice.GetVoiceHostBaseUrl();
        _voiceHostStartupTimeoutMs = s.Voice.VoiceHostStartupTimeoutMs;
        _voiceHostHealthPath = string.IsNullOrWhiteSpace(s.Voice.VoiceHostHealthPath)
            ? "/health"
            : s.Voice.VoiceHostHealthPath.Trim();
        _voiceTtsEngine = s.Voice.GetNormalizedTtsEngine();
        _voiceTtsModelId = s.Voice.GetResolvedTtsModelId();
        _voiceTtsVoiceId = s.Voice.GetResolvedTtsVoiceId();
        // Front-end ASR stays pinned to faster-whisper.
        _voiceSttEngine = "faster-whisper";
        _voiceSttModelId = ResolveFrontendSttModelIdForUi(s.Voice);
        _voicePreferLocalTts = s.Voice.PreferLocalTts;
        _voiceAsrTimeoutMs = s.Voice.AsrTimeoutMs;
        _voiceAgentTimeoutMs = s.Voice.AgentTimeoutMs;
        _voiceSpeakingTimeoutMs = s.Voice.SpeakingTimeoutMs;
        _youtubeAsrProvider = s.Voice.GetResolvedYouTubeAsrProvider();
        _youtubeAsrModelId = s.Voice.GetResolvedYouTubeAsrModelId();
        _youtubeLanguageHint = s.Voice.GetResolvedYouTubeLanguageHint();
        _youtubeDraftTone = s.Voice.GetResolvedYouTubeDraftTone();
        _youtubeKeepAudio = s.Voice.YouTubeKeepAudio;

        _memoryEnabled     = s.Memory.Enabled;
        _embeddingsEnabled = s.Memory.UseEmbeddings;

        // MCP Permissions
        _mcpPermDeveloperOverride = s.Mcp.Permissions.DeveloperOverride;
        _mcpPermScreen            = s.Mcp.Permissions.Screen;
        _mcpPermFiles             = s.Mcp.Permissions.Files;
        _mcpPermSystem            = s.Mcp.Permissions.System;
        _mcpPermWeb               = s.Mcp.Permissions.Web;
        _mcpPermMemoryRead        = s.Mcp.Permissions.MemoryRead;
        _mcpPermMemoryWrite       = s.Mcp.Permissions.MemoryWrite;
        _weatherUserAgent         = s.Weather.UserAgent;
        _weatherPreferredUnits    = s.Weather.PreferredUnits;
        _reasoningGuardrails      = NormalizeReasoningGuardrailsMode(s.Ui.ReasoningGuardrails);
        _inputGain                = s.Audio.InputGain;
        _personalityProfilesDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(s);
        LoadLocationForProfile(s.ActiveProfileId);

        // Notify all bindings
        OnPropertyChanged(nameof(LlmBaseUrl));
        OnPropertyChanged(nameof(LlmModel));
        OnPropertyChanged(nameof(LlmMaxTokens));
        OnPropertyChanged(nameof(LlmTemperature));
        OnPropertyChanged(nameof(TtsEnabled));
        OnPropertyChanged(nameof(PttKey));
        OnPropertyChanged(nameof(PttChord));
        OnPropertyChanged(nameof(ShutupChord));
        OnPropertyChanged(nameof(VoiceHostEnabled));
        OnPropertyChanged(nameof(VoiceHostBaseUrl));
        OnPropertyChanged(nameof(VoiceHostStartupTimeoutMs));
        OnPropertyChanged(nameof(VoiceHostHealthPath));
        OnPropertyChanged(nameof(VoiceTtsEngine));
        OnPropertyChanged(nameof(VoiceTtsModelId));
        OnPropertyChanged(nameof(VoiceTtsVoiceId));
        OnPropertyChanged(nameof(VoiceSttEngine));
        OnPropertyChanged(nameof(VoiceSttModelId));
        OnPropertyChanged(nameof(VoicePreferLocalTts));
        OnPropertyChanged(nameof(VoiceAsrTimeoutMs));
        OnPropertyChanged(nameof(VoiceAgentTimeoutMs));
        OnPropertyChanged(nameof(VoiceSpeakingTimeoutMs));
        OnPropertyChanged(nameof(YouTubeAsrProvider));
        OnPropertyChanged(nameof(YouTubeAsrModelId));
        OnPropertyChanged(nameof(YouTubeLanguageHint));
        OnPropertyChanged(nameof(YouTubeDraftTone));
        OnPropertyChanged(nameof(YouTubeKeepAudio));
        OnPropertyChanged(nameof(MemoryEnabled));
        OnPropertyChanged(nameof(EmbeddingsEnabled));
        OnPropertyChanged(nameof(McpPermDeveloperOverride));
        OnPropertyChanged(nameof(McpPermScreen));
        OnPropertyChanged(nameof(McpPermFiles));
        OnPropertyChanged(nameof(McpPermSystem));
        OnPropertyChanged(nameof(McpPermWeb));
        OnPropertyChanged(nameof(McpPermMemoryRead));
        OnPropertyChanged(nameof(McpPermMemoryWrite));
        OnPropertyChanged(nameof(WeatherUserAgent));
        OnPropertyChanged(nameof(WeatherPreferredUnits));
        OnPropertyChanged(nameof(ReasoningGuardrails));
        OnPropertyChanged(nameof(InputGain));
        OnPropertyChanged(nameof(LocationLabel));
        OnPropertyChanged(nameof(PersonalityProfilesDirectory));

        RefreshKokoroVoiceCatalog();
    }

    private async Task LoadProfilesAsync()
    {
        if (_store is null) return;

        try
        {
            var profiles = await _store.ListProfilesAsync();

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                AvailableProfiles.Clear();

                // "None" option — no profile selected
                AvailableProfiles.Add(new ProfileOption(null, "(No profile selected)"));

                foreach (var p in profiles)
                {
                    var label = p.Kind == "user"
                        ? $"{p.DisplayName} (me)"
                        : !string.IsNullOrWhiteSpace(p.Relationship)
                            ? $"{p.DisplayName} ({p.Relationship})"
                            : p.DisplayName;

                    AvailableProfiles.Add(new ProfileOption(p.ProfileId, label));
                }

                // Restore saved selection
                _selectedProfile = AvailableProfiles
                    .FirstOrDefault(p => p.ProfileId == _settings.ActiveProfileId)
                    ?? AvailableProfiles[0];

                OnPropertyChanged(nameof(SelectedProfile));
                LoadLocationForProfile(_selectedProfile?.ProfileId);
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load profiles: {ex.Message}";
        }
    }

    private void LoadPersonalityProfiles()
    {
        try
        {
            var profilesDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(_settings);
            PersonalityProfilesDirectory = profilesDirectory;
            _personalityStore.EnsureBuiltInsInstalled(profilesDirectory);

            var profiles = _personalityStore.ListProfiles(profilesDirectory)
                .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _suppressPersonalitySelectionSave = true;
            AvailablePersonalities.Clear();
            foreach (var profile in profiles)
            {
                AvailablePersonalities.Add(new PersonalityOption(
                    profile.Id,
                    profile.DisplayName,
                    profile.SourcePath,
                    profile.Hash));
            }

            if (AvailablePersonalities.Count == 0)
            {
                StatusText = "No valid personality profiles were found.";
                return;
            }

            _selectedPersonality = AvailablePersonalities
                .FirstOrDefault(p =>
                    string.Equals(
                        p.ProfileId,
                        _settings.ActivePersonalityId,
                        StringComparison.OrdinalIgnoreCase))
                ?? AvailablePersonalities.FirstOrDefault(p =>
                    string.Equals(p.ProfileId, BuiltInProfileCatalog.HelpfulDefaultId, StringComparison.OrdinalIgnoreCase))
                ?? AvailablePersonalities[0];

            OnPropertyChanged(nameof(SelectedPersonality));
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load personalities: {ex.Message}";
        }
        finally
        {
            _suppressPersonalitySelectionSave = false;
        }
    }

    private void AddNewPersonality()
    {
        try
        {
            var directory = PersonalityProfilesDirectory;
            var baseId = $"custom_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
            var candidateId = baseId;
            var suffix = 2;

            while (File.Exists(_personalityStore.ResolveProfilePath(directory, candidateId)))
            {
                candidateId = $"{baseId}_{suffix}";
                suffix++;
            }

            var profile = new PersonalityProfile
            {
                Id = candidateId,
                DisplayName = "New Personality",
                Description = "Template profile. Edit description, identity, and tone values for your new style.",
                Identity = new PersonalityIdentity
                {
                    SelfName = "New Personality",
                    SelfDescription = "I am a new custom personality template. Edit identity.self_name and identity.self_description to define your assistant's voice, mission, and style."
                },
                Tone = new PersonalityTone
                {
                    Formality = 0.60,
                    Warmth = 0.60,
                    Humor = 0.20,
                    Verbosity = 0.55,
                    Directness = 0.82
                },
                BehaviorRules = new PersonalityBehaviorRules
                {
                    PushbackOnIllogic = true,
                    AvoidFlattery = true,
                    NeverOverridePermissions = true
                },
                SpeechPatterns = new PersonalitySpeechPatterns
                {
                    IncludeSignatureNote = false,
                    AvoidModernSlang = true
                },
                CapabilityConstraints = new PersonalityCapabilityConstraints
                {
                    MaxMetaphorDensity = 0.22
                },
                ReductionRules = new PersonalityReductionRules
                {
                    Enabled = false,
                    CollapseExactDuplicates = true,
                    TrimTrailingFluff = true
                }
            };

            var descriptor = _personalityStore.SaveProfile(directory, profile);
            _settings = _settings with
            {
                ActivePersonalityId = descriptor.Id,
                PersonalityProfilesDir = directory
            };
            SettingsManager.Save(_settings);

            LoadPersonalityProfiles();
            _selectedPersonality = AvailablePersonalities
                .FirstOrDefault(p => string.Equals(p.ProfileId, descriptor.Id, StringComparison.OrdinalIgnoreCase))
                ?? _selectedPersonality;
            OnPropertyChanged(nameof(SelectedPersonality));

            StatusText = $"Created personality '{descriptor.Id}'. Edit it and save.";
            ActivePersonalityChanged?.Invoke(_settings.ActivePersonalityId);
            SettingsChanged?.Invoke(_settings);

            _audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "PERSONALITY_PROFILE_CREATED",
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["profileId"] = descriptor.Id,
                    ["profileHash"] = descriptor.Hash
                }
            });

            OpenPersonalityFile(descriptor.SourcePath);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not create personality: {ex.Message}";
        }
    }

    private void EditSelectedPersonality()
    {
        if (SelectedPersonality is null)
            return;

        try
        {
            OpenPersonalityFile(SelectedPersonality.SourcePath);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open personality profile: {ex.Message}";
        }
    }

    private void DuplicateSelectedPersonality()
    {
        if (SelectedPersonality is null)
            return;

        try
        {
            var sourceId = SelectedPersonality.ProfileId;
            var directory = PersonalityProfilesDirectory;
            var baseId = $"{sourceId}_copy";
            var candidateId = baseId;
            var suffix = 2;

            while (File.Exists(_personalityStore.ResolveProfilePath(directory, candidateId)))
            {
                candidateId = $"{baseId}{suffix}";
                suffix++;
            }

            var descriptor = _personalityStore.DuplicateProfile(
                directory,
                sourceId,
                candidateId);

            _settings = _settings with
            {
                ActivePersonalityId = descriptor.Id,
                PersonalityProfilesDir = directory
            };
            SettingsManager.Save(_settings);

            LoadPersonalityProfiles();
            _selectedPersonality = AvailablePersonalities
                .FirstOrDefault(p => string.Equals(p.ProfileId, descriptor.Id, StringComparison.OrdinalIgnoreCase))
                ?? _selectedPersonality;
            OnPropertyChanged(nameof(SelectedPersonality));

            StatusText = $"Personality duplicated as '{descriptor.Id}'.";
            ActivePersonalityChanged?.Invoke(_settings.ActivePersonalityId);
            SettingsChanged?.Invoke(_settings);

            _audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "PERSONALITY_PROFILE_DUPLICATED",
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["sourceProfileId"] = sourceId,
                    ["newProfileId"] = descriptor.Id,
                    ["profileHash"] = descriptor.Hash
                }
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not duplicate personality: {ex.Message}";
        }
    }

    private void ResetPersonalityToDefault()
    {
        var defaultOption = AvailablePersonalities
            .FirstOrDefault(p => string.Equals(p.ProfileId, BuiltInProfileCatalog.HelpfulDefaultId, StringComparison.OrdinalIgnoreCase));

        if (defaultOption is null)
        {
            StatusText = "Default personality profile is not available.";
            return;
        }

        _selectedPersonality = defaultOption;
        OnPropertyChanged(nameof(SelectedPersonality));
        SavePersonalitySelectionOnly();
    }

    private void OpenPersonalityFile(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            StatusText = "Selected personality file was not found.";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = sourcePath,
            UseShellExecute = true
        });
    }

    // ─── YouTube Job Flow ────────────────────────────────────────────

    private async Task StartYouTubeJobAsync()
    {
        var videoUrl = (YouTubeUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            YouTubeErrorMessage = "Enter a YouTube URL first.";
            return;
        }

        _youtubeJobPollCts?.Cancel();
        _youtubeJobPollCts?.Dispose();
        _youtubeJobPollCts = new CancellationTokenSource();

        YouTubeJobIsRunning = true;
        YouTubeJobStatus = "Running";
        YouTubeJobStage = "Resolving";
        YouTubeJobProgressPercent = 0;
        YouTubeSummary = "";
        YouTubeTranscriptPath = "";
        YouTubeOutputDir = "";
        YouTubeErrorMessage = "";

        try
        {
            var start = await _youtubeJobsClient.StartJobAsync(
                videoUrl: videoUrl,
                languageHint: string.IsNullOrWhiteSpace(YouTubeLanguageHint) ? null : YouTubeLanguageHint.Trim(),
                keepAudio: YouTubeKeepAudio,
                asrProvider: string.IsNullOrWhiteSpace(YouTubeAsrProvider) ? null : YouTubeAsrProvider.Trim(),
                asrModel: string.IsNullOrWhiteSpace(YouTubeAsrModelId) ? null : YouTubeAsrModelId.Trim(),
                draftTone: string.IsNullOrWhiteSpace(YouTubeDraftTone) ? "professional" : YouTubeDraftTone.Trim().ToLowerInvariant(),
                cancellationToken: _youtubeJobPollCts.Token);

            YouTubeJobId = start.JobId;
            if (!string.IsNullOrWhiteSpace(start.OutputDir))
                YouTubeOutputDir = start.OutputDir;

            await PollYouTubeJobAsync(start.JobId, _youtubeJobPollCts.Token);
        }
        catch (OperationCanceledException)
        {
            YouTubeJobStatus = "Cancelled";
            YouTubeJobStage = "Cancelled";
        }
        catch (Exception ex)
        {
            YouTubeJobStatus = "Failed";
            YouTubeJobStage = "Failed";
            YouTubeErrorMessage = ex.Message;
        }
        finally
        {
            if (!string.Equals(YouTubeJobStatus, "Running", StringComparison.OrdinalIgnoreCase))
                YouTubeJobIsRunning = false;
        }
    }

    private async Task CancelYouTubeJobAsync()
    {
        if (string.IsNullOrWhiteSpace(YouTubeJobId))
            return;

        try
        {
            _youtubeJobPollCts?.Cancel();
            using var cancelCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var snapshot = await _youtubeJobsClient.CancelJobAsync(YouTubeJobId, cancelCts.Token);
            ApplyYouTubeJobSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            YouTubeErrorMessage = $"Cancel failed: {ex.Message}";
            YouTubeJobIsRunning = false;
        }
    }

    private async Task PollYouTubeJobAsync(string jobId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = await _youtubeJobsClient.GetJobAsync(jobId, cancellationToken);
            ApplyYouTubeJobSnapshot(snapshot);
            if (snapshot.IsTerminal)
                return;
            await Task.Delay(1000, cancellationToken);
        }
    }

    private void ApplyYouTubeJobSnapshot(YouTubeJobSnapshot snapshot)
    {
        YouTubeJobId = snapshot.JobId;
        YouTubeJobStatus = string.IsNullOrWhiteSpace(snapshot.Status) ? "Unknown" : snapshot.Status;
        YouTubeJobStage = snapshot.Stage ?? "";
        YouTubeJobProgressPercent = Math.Clamp((int)Math.Round(snapshot.Progress * 100.0), 0, 100);
        if (!string.IsNullOrWhiteSpace(snapshot.TranscriptPath))
            YouTubeTranscriptPath = snapshot.TranscriptPath;
        if (!string.IsNullOrWhiteSpace(snapshot.OutputDir))
            YouTubeOutputDir = snapshot.OutputDir;
        if (!string.IsNullOrWhiteSpace(snapshot.Summary))
            YouTubeSummary = snapshot.Summary;

        if (snapshot.Error is not null)
            YouTubeErrorMessage = $"{snapshot.Error.Code}: {snapshot.Error.Message}";

        YouTubeJobIsRunning = !snapshot.IsTerminal;
    }

    // ─── VoiceHost Lifecycle ─────────────────────────────────────────

    private async Task StartVoiceHostAsync()
    {
        if (_voiceHostProcessManager is null)
        {
            VoiceHostMessage = "Process manager unavailable.";
            return;
        }

        VoiceHostIsBusy = true;
        VoiceHostStatusText = "Starting...";
        VoiceHostMessage = "";
        try
        {
            var result = await _voiceHostProcessManager.EnsureRunningAsync(CancellationToken.None);
            if (result.Success)
            {
                VoiceHostStatusText = "Running";
                VoiceHostMessage = $"Listening at {result.BaseUrl}";
            }
            else
            {
                VoiceHostStatusText = "Failed";
                VoiceHostMessage = result.UserMessage;
            }

            await RefreshVoiceHostHealthAsync();
        }
        catch (Exception ex)
        {
            VoiceHostStatusText = "Error";
            VoiceHostMessage = ex.Message;
        }
        finally
        {
            VoiceHostIsBusy = false;
        }
    }

    private async Task StopVoiceHostAsync()
    {
        if (_voiceHostProcessManager is null)
        {
            VoiceHostMessage = "Process manager unavailable.";
            return;
        }

        VoiceHostIsBusy = true;
        VoiceHostStatusText = "Stopping...";
        try
        {
            _voiceHostProcessManager.Stop();
            VoiceHostStatusText = "Stopped";
            VoiceHostIsReachable = false;
            VoiceHostIsReady = false;
            VoiceHostAsrReady = false;
            VoiceHostTtsReady = false;
            VoiceHostVersion = "";
            VoiceHostMessage = "VoiceHost stopped by user.";

            // Small delay then verify it's actually gone
            await Task.Delay(500);
            await RefreshVoiceHostHealthAsync();
        }
        catch (Exception ex)
        {
            VoiceHostStatusText = "Error";
            VoiceHostMessage = ex.Message;
        }
        finally
        {
            VoiceHostIsBusy = false;
        }
    }

    /// <summary>
    /// Probes the VoiceHost /health endpoint and updates all status properties.
    /// Called on-demand by the Refresh button or after Start/Stop.
    /// </summary>
    public async Task RefreshVoiceHostHealthAsync()
    {
        if (_voiceHostProcessManager is null)
        {
            VoiceHostStatusText = "Unavailable";
            VoiceHostMessage = "VoiceHost process manager not initialized.";
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var health = await _voiceHostProcessManager.CheckHealthAsync(cts.Token);

            VoiceHostIsReachable = health.Reachable;
            VoiceHostIsReady     = health.Ready;
            VoiceHostAsrReady    = health.AsrReady;
            VoiceHostTtsReady    = health.TtsReady;
            VoiceHostVersion     = health.Version;

            if (health.Ready)
            {
                VoiceHostStatusText = "Ready";
                VoiceHostMessage = string.IsNullOrWhiteSpace(health.Version)
                    ? "All systems operational."
                    : $"Version: {health.Version}";
            }
            else if (health.Reachable)
            {
                VoiceHostStatusText = "Warming Up";
                VoiceHostMessage = string.IsNullOrWhiteSpace(health.Message)
                    ? "Reachable but not fully ready."
                    : health.Message;
            }
            else
            {
                VoiceHostStatusText = "Offline";
                VoiceHostMessage = "VoiceHost is not running.";
            }
        }
        catch (OperationCanceledException)
        {
            VoiceHostStatusText = "Timeout";
            VoiceHostMessage = "Health check timed out.";
        }
        catch (Exception ex)
        {
            VoiceHostStatusText = "Error";
            VoiceHostMessage = ex.Message;
        }
    }

    /// <summary>
    /// Starts a background health poll loop at 5-second intervals.
    /// Called when the settings panel becomes visible.
    /// </summary>
    public void StartVoiceHostHealthPolling()
    {
        StopVoiceHostHealthPolling();
        _voiceHostHealthPollCts = new CancellationTokenSource();
        var token = _voiceHostHealthPollCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => _ = RefreshVoiceHostHealthAsync());
                    await Task.Delay(5_000, token);
                }
                catch (OperationCanceledException) { break; }
                catch { /* swallow — health polling is best-effort */ }
            }
        }, token);
    }

    /// <summary>
    /// Stops the background health poll loop.
    /// Called when the settings panel is hidden or the command palette closes.
    /// </summary>
    public void StopVoiceHostHealthPolling()
    {
        try { _voiceHostHealthPollCts?.Cancel(); }
        catch { /* best effort */ }
        _voiceHostHealthPollCts?.Dispose();
        _voiceHostHealthPollCts = null;
    }

    // ─── Dirty State ────────────────────────────────────────────────

    private bool _hasUnsavedChanges;

    /// <summary>
    /// True when the user has modified any setting since the last save/load.
    /// Bound to the sticky bar to signal pending changes.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    private void MarkDirty() => HasUnsavedChanges = true;

    private void ClearDirty() => HasUnsavedChanges = false;

    // ─── Audio Device Management ────────────────────────────────────

    private void LoadAudioDevices()
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            AvailableInputDevices.Clear();
            AvailableOutputDevices.Clear();

            foreach (var device in AudioDeviceEnumerator.GetInputDevices())
                AvailableInputDevices.Add(device);

            foreach (var device in AudioDeviceEnumerator.GetOutputDevices())
                AvailableOutputDevices.Add(device);

            // Restore persisted selection by product name
            _selectedInputDevice = AvailableInputDevices.FirstOrDefault(d =>
                    !string.IsNullOrEmpty(_settings.Audio.InputDeviceName) &&
                    d.ProductName.Equals(_settings.Audio.InputDeviceName, StringComparison.OrdinalIgnoreCase))
                ?? AvailableInputDevices.FirstOrDefault();

            _selectedOutputDevice = AvailableOutputDevices.FirstOrDefault(d =>
                    !string.IsNullOrEmpty(_settings.Audio.OutputDeviceName) &&
                    d.ProductName.Equals(_settings.Audio.OutputDeviceName, StringComparison.OrdinalIgnoreCase))
                ?? AvailableOutputDevices.FirstOrDefault();

            OnPropertyChanged(nameof(SelectedInputDevice));
            OnPropertyChanged(nameof(SelectedOutputDevice));
        });
    }

    private void RefreshAudioDevices()
    {
        LoadAudioDevices();
        MicTestStatus = "Devices refreshed.";
    }

    // ─── Mic Test ────────────────────────────────────────────────────

    private void StartMicTest()
    {
        if (_isTestingMic)
            return;

        try
        {
            var deviceNumber = _selectedInputDevice?.DeviceNumber ?? 0;

            _testRecordingBytes = null;
            OnPropertyChanged(nameof(HasTestRecording));

            _testPcmBuffer = new MemoryStream();

            _testWaveIn = new WaveInEvent
            {
                DeviceNumber       = deviceNumber,
                WaveFormat         = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 50,
                NumberOfBuffers    = 3
            };
            _testWaveIn.DataAvailable    += OnTestDataAvailable;
            _testWaveIn.RecordingStopped += OnTestRecordingStopped;
            _testWaveIn.StartRecording();

            IsTestingMic  = true;
            MicTestStatus = "Recording — speak now...";
            MicTestLevel  = 0;

            // Auto-stop after 30 seconds to prevent orphaned recordings
            _testTimerCts = new CancellationTokenSource();
            var token = _testTimerCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(30_000, token);
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(StopMicTest);
                }
                catch (OperationCanceledException) { /* expected on manual stop */ }
            }, token);
        }
        catch (Exception ex)
        {
            MicTestStatus = $"Failed to open device: {ex.Message}";
            CleanupMicTest();
        }
    }

    private void StopMicTest()
    {
        if (!_isTestingMic)
            return;

        try { _testTimerCts?.Cancel(); }
        catch { /* best effort */ }
        _testTimerCts?.Dispose();
        _testTimerCts = null;

        try { _testWaveIn?.StopRecording(); }
        catch { /* triggers OnTestRecordingStopped */ }
    }

    private void OnTestDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Compute RMS for the level meter
        double level = ComputeRmsLevel(e.Buffer, e.BytesRecorded);

        // Scale by gain for visual feedback
        double scaled = Math.Min(1.0, level * Math.Max(0.1, _inputGain));

        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            MicTestLevel = scaled;
        });

        // Buffer raw PCM for playback
        lock (_testGate)
        {
            _testPcmBuffer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnTestRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Finalize the recording — wrap raw PCM into a WAV container
        byte[] pcm;
        lock (_testGate)
        {
            pcm = _testPcmBuffer?.ToArray() ?? Array.Empty<byte>();
            _testPcmBuffer?.Dispose();
            _testPcmBuffer = null;
        }

        if (pcm.Length > 0)
        {
            var wavStream = new MemoryStream();
            using (var writer = new WaveFileWriter(wavStream, new WaveFormat(16000, 16, 1)))
            {
                writer.Write(pcm, 0, pcm.Length);
            }
            _testRecordingBytes = wavStream.ToArray();
        }

        CleanupMicTest();

        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            IsTestingMic = false;
            MicTestLevel = 0;
            MicTestStatus = _testRecordingBytes is { Length: > 100 }
                ? $"Recorded {_testRecordingBytes.Length / 1024}KB — click Play to review."
                : "No audio captured.";
            OnPropertyChanged(nameof(HasTestRecording));
        });
    }

    private void CleanupMicTest()
    {
        if (_testWaveIn is not null)
        {
            _testWaveIn.DataAvailable    -= OnTestDataAvailable;
            _testWaveIn.RecordingStopped -= OnTestRecordingStopped;
            _testWaveIn.Dispose();
            _testWaveIn = null;
        }
    }

    private void PlayTestRecording()
    {
        if (_testRecordingBytes is not { Length: > 100 })
            return;

        var deviceNumber = _selectedOutputDevice?.DeviceNumber ?? -1;
        var audioBytes   = _testRecordingBytes;

        _ = Task.Run(async () =>
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    MicTestStatus = "Playing...");

                using var stream = new MemoryStream(audioBytes, writable: false);
                using var reader = new WaveFileReader(stream);
                using var output = new WaveOutEvent { DeviceNumber = deviceNumber };

                var tcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                output.PlaybackStopped += (_, args) =>
                {
                    if (args.Exception is not null)
                        tcs.TrySetException(args.Exception);
                    else
                        tcs.TrySetResult(true);
                };

                output.Init(reader);
                output.Play();
                await tcs.Task;

                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    MicTestStatus = "Playback complete.");
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    MicTestStatus = $"Playback failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Computes RMS energy of 16-bit PCM samples and maps to a 0.0–1.0 range
    /// using a dB scale with a -60 dB floor for natural-feeling meter response.
    /// </summary>
    private static double ComputeRmsLevel(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2) return 0;

        double sum = 0;
        int sampleCount = bytesRecorded / 2;

        for (int i = 0; i + 1 < bytesRecorded; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sum += (double)sample * sample;
        }

        double rms        = Math.Sqrt(sum / sampleCount);
        double normalized  = rms / 32768.0;
        if (normalized < 1e-10) return 0;

        double db = 20.0 * Math.Log10(normalized);
        return Math.Clamp((db + 60.0) / 60.0, 0, 1);
    }

    // ─── Persistence ─────────────────────────────────────────────────

    private void SaveSettings()
    {
        var locationProfileId = _selectedProfile?.ProfileId ?? _settings.ActiveProfileId;
        var locationProfileKey = AppSettings.NormalizeLocationProfileKey(locationProfileId);
        var previousLocation = _settings.GetEffectiveUserLocation(locationProfileId);
        var previousLocationMode = previousLocation.GetNormalizedMode();
        var previousLocationValue = previousLocation.GetResolvedLabel() ?? "";

        var manualLocationValue = (_locationLabel ?? "").Trim();
        var nextLocationMode = string.IsNullOrWhiteSpace(manualLocationValue)
            ? "unset"
            : "manual";
        var nextLocationValue = string.Equals(nextLocationMode, "manual", StringComparison.OrdinalIgnoreCase)
            ? manualLocationValue
            : "";

        var locationChanged =
            !string.Equals(previousLocationMode, nextLocationMode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousLocationValue, nextLocationValue, StringComparison.Ordinal);

        var nextLocationUpdatedAt = locationChanged
            ? DateTimeOffset.UtcNow.ToString("O")
            : previousLocation.GetResolvedUpdatedAt() ?? "";

        var nextLocation = previousLocation with
        {
            Mode = nextLocationMode,
            Value = nextLocationValue,
            UpdatedAt = nextLocationUpdatedAt,

            // Mirror legacy fields for backwards-safe settings migration.
            Enabled = string.Equals(nextLocationMode, "manual", StringComparison.OrdinalIgnoreCase),
            Label = nextLocationValue,
            Timezone = "",
            Latitude = null,
            Longitude = null
        };
        var scopedLocations = new Dictionary<string, LocationSettings>(
            _settings.UserProfile.LocationsByProfile,
            StringComparer.OrdinalIgnoreCase)
        {
            [locationProfileKey] = nextLocation
        };

        var updated = _settings with
        {
            Llm = _settings.Llm with
            {
                BaseUrl     = _llmBaseUrl,
                Model       = _llmModel,
                MaxTokens   = _llmMaxTokens,
                Temperature = _llmTemperature
            },
            Audio = _settings.Audio with
            {
                TtsEnabled       = _ttsEnabled,
                PttKey           = _pttKey,
                PttChord         = _pttChord,
                ShutupChord      = _shutupChord,
                InputDeviceName  = _selectedInputDevice?.ProductName ?? "",
                OutputDeviceName = _selectedOutputDevice?.ProductName ?? "",
                InputGain        = Math.Clamp(_inputGain, 0.0, 2.0)
            },
            Voice = _settings.Voice with
            {
                VoiceHostEnabled = _voiceHostEnabled,
                VoiceHostBaseUrl = string.IsNullOrWhiteSpace(_voiceHostBaseUrl)
                    ? "http://127.0.0.1:17845"
                    : _voiceHostBaseUrl.Trim(),
                VoiceHostStartupTimeoutMs = Math.Max(5_000, _voiceHostStartupTimeoutMs),
                VoiceHostHealthPath = string.IsNullOrWhiteSpace(_voiceHostHealthPath)
                    ? "/health"
                    : _voiceHostHealthPath.Trim(),
                TtsEngine = string.IsNullOrWhiteSpace(_voiceTtsEngine)
                    ? "windows"
                    : _voiceTtsEngine.Trim(),
                TtsModelId = _voiceTtsModelId.Trim(),
                TtsVoiceId = _voiceTtsVoiceId.Trim(),
                SttEngine = "faster-whisper",
                SttModelId = ResolveFrontendSttModelIdForSave(_voiceSttEngine, _voiceSttModelId),
                PreferLocalTts = _voicePreferLocalTts,
                AsrTimeoutMs = Math.Max(5_000, _voiceAsrTimeoutMs),
                AgentTimeoutMs = Math.Max(10_000, _voiceAgentTimeoutMs),
                SpeakingTimeoutMs = Math.Max(10_000, _voiceSpeakingTimeoutMs),
                YouTubeAsrProvider = string.IsNullOrWhiteSpace(_youtubeAsrProvider)
                    ? "qwen3asr"
                    : _youtubeAsrProvider.Trim(),
                YouTubeAsrModelId = string.IsNullOrWhiteSpace(_youtubeAsrModelId)
                    ? "qwen-asr-1.6b"
                    : _youtubeAsrModelId.Trim(),
                YouTubeLanguageHint = string.IsNullOrWhiteSpace(_youtubeLanguageHint)
                    ? ""
                    : _youtubeLanguageHint.Trim(),
                YouTubeDraftTone = string.IsNullOrWhiteSpace(_youtubeDraftTone)
                    ? "professional"
                    : _youtubeDraftTone.Trim().ToLowerInvariant(),
                YouTubeKeepAudio = _youtubeKeepAudio
            },
            Memory = _settings.Memory with
            {
                Enabled       = _memoryEnabled,
                UseEmbeddings = _embeddingsEnabled
            },
            Mcp = _settings.Mcp with
            {
                Permissions = new Config.McpPermissionsSettings
                {
                    DeveloperOverride = _mcpPermDeveloperOverride,
                    Screen            = _mcpPermScreen,
                    Files             = _mcpPermFiles,
                    System            = _mcpPermSystem,
                    Web               = _mcpPermWeb,
                    MemoryRead        = _mcpPermMemoryRead,
                    MemoryWrite       = _mcpPermMemoryWrite
                }
            },
            Weather = _settings.Weather with
            {
                UserAgent = string.IsNullOrWhiteSpace(_weatherUserAgent)
                    ? "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)"
                    : _weatherUserAgent.Trim(),
                PreferredUnits = string.IsNullOrWhiteSpace(_weatherPreferredUnits)
                    ? "imperial"
                    : _weatherPreferredUnits.Trim()
            },
            Ui = _settings.Ui with
            {
                ReasoningGuardrails = NormalizeReasoningGuardrailsMode(_reasoningGuardrails)
            },
            UserProfile = _settings.UserProfile with
            {
                Location = nextLocation,
                LocationsByProfile = scopedLocations
            },
            Location = nextLocation,
            ActiveProfileId = _selectedProfile?.ProfileId,
            ActivePersonalityId = _selectedPersonality?.ProfileId ?? BuiltInProfileCatalog.HelpfulDefaultId,
            PersonalityProfilesDir = PersonalityProfilesDirectory
        };

        SettingsManager.Save(updated);
        _settings = updated;
        ClearDirty();

        StatusText = "Settings saved.";

        ActiveProfileChanged?.Invoke(_selectedProfile?.ProfileId);
        ActivePersonalityChanged?.Invoke(updated.ActivePersonalityId);
        SettingsChanged?.Invoke(updated);

        if (string.Equals(nextLocationMode, "manual", StringComparison.OrdinalIgnoreCase) &&
            locationChanged)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "LOCATION_MANUAL_SAVED",
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["mode"] = "manual",
                    ["value"] = nextLocationValue,
                    ["profileId"] = locationProfileId ?? ""
                }
            });
        }
        else if (string.Equals(previousLocationMode, "manual", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(nextLocationMode, "unset", StringComparison.OrdinalIgnoreCase))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "LOCATION_CLEARED",
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["profileId"] = locationProfileId ?? ""
                }
            });
        }

        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "SETTINGS_SAVED",
            Result = $"{(_selectedProfile?.ProfileId is not null ? $"activeProfile={_selectedProfile.ProfileId}" : "activeProfile=none")};activePersonality={updated.ActivePersonalityId}"
        });
    }

    private void SaveProfileSelectionOnly()
    {
        var updated = _settings with
        {
            ActiveProfileId = _selectedProfile?.ProfileId
        };

        SettingsManager.Save(updated);
        _settings = updated;
        StatusText = "Profile selected.";

        ActiveProfileChanged?.Invoke(_selectedProfile?.ProfileId);
        SettingsChanged?.Invoke(updated);

        _audit.Append(new AuditEvent
        {
            Actor = "user",
            Action = "SETTINGS_SAVED",
            Result = $"{(_selectedProfile?.ProfileId is not null ? $"activeProfile={_selectedProfile.ProfileId}" : "activeProfile=none")};activePersonality={updated.ActivePersonalityId}"
        });
    }

    private void SavePersonalitySelectionOnly()
    {
        var selectedId = _selectedPersonality?.ProfileId ?? BuiltInProfileCatalog.HelpfulDefaultId;
        var updated = _settings with
        {
            ActivePersonalityId = selectedId,
            PersonalityProfilesDir = PersonalityProfilesDirectory
        };

        SettingsManager.Save(updated);
        _settings = updated;
        StatusText = "Personality selected.";

        ActivePersonalityChanged?.Invoke(updated.ActivePersonalityId);
        SettingsChanged?.Invoke(updated);

        _audit.Append(new AuditEvent
        {
            Actor = "user",
            Action = "PERSONALITY_SELECTED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["profileId"] = selectedId
            }
        });
    }

    private void LoadLocationForProfile(string? profileId)
    {
        var effectiveLocation = _settings.GetEffectiveUserLocation(profileId);
        _locationLabel = effectiveLocation.GetResolvedLabel() ?? "";

        OnPropertyChanged(nameof(LocationLabel));
    }

    // ─── Kokoro Voice Catalog ────────────────────────────────────────

    private void RefreshKokoroVoiceCatalog()
    {
        var discovered = KokoroVoiceCatalog.Discover();

        AvailableKokoroVoices.Clear();
        foreach (var voiceId in discovered)
            AvailableKokoroVoices.Add(voiceId);

        // Ensure the current setting is always present in the list
        // even if the pack folder was removed since the last save.
        var current = (_voiceTtsVoiceId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(current) &&
            !AvailableKokoroVoices.Contains(current))
        {
            AvailableKokoroVoices.Add(current);
        }

        OnPropertyChanged(nameof(SelectedKokoroVoice));
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

    private static string ResolveFrontendSttModelIdForUi(VoiceSettings settings)
    {
        var configuredEngine = settings.GetNormalizedSttEngine();
        if (string.Equals(configuredEngine, "faster-whisper", StringComparison.OrdinalIgnoreCase))
        {
            var model = settings.GetResolvedSttModelId();
            return string.IsNullOrWhiteSpace(model) ? "base" : model;
        }

        return "base";
    }

    private static string ResolveFrontendSttModelIdForSave(string uiEngine, string uiModelId)
    {
        // Persist faster-whisper model defaults even when legacy configs still
        // mention qwen3asr in the UI.
        if (string.Equals((uiEngine ?? "").Trim(), "faster-whisper", StringComparison.OrdinalIgnoreCase))
        {
            var model = (uiModelId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(model))
                return "base";
            if (model.Contains("qwen", StringComparison.OrdinalIgnoreCase))
                return "base";
            return model;
        }

        return "base";
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Profile Dropdown Item
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Represents one item in the active profile dropdown.
/// <c>ProfileId == null</c> means "no profile selected."
/// </summary>
public sealed record ProfileOption(string? ProfileId, string DisplayLabel)
{
    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Represents one item in the active personality dropdown.
/// </summary>
public sealed record PersonalityOption(
    string ProfileId,
    string DisplayLabel,
    string SourcePath,
    string ProfileHash)
{
    public override string ToString() => DisplayLabel;
}
