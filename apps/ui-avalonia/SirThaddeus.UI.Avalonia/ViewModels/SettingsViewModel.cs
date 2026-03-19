using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.UI.Avalonia;

namespace SirThaddeus.UI.Avalonia.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string DefaultPiperVoiceId = "en_US-john-medium";
    private const string DefaultKokoroVoiceId = "bm_lewis";

    private AppSettings _appSettings;
    private bool _isDirty;
    private bool _isRefreshingModels;
    private bool _llmsTabActivated;
    private string _statusText = "";
    private CancellationTokenSource? _voiceHostHealthPollCts;
    private string _voiceHostStatusText = "Unknown";
    private bool _voiceHostIsReachable;
    private bool _voiceHostIsReady;
    private bool _voiceHostAsrReady;
    private bool _voiceHostTtsReady;
    private string _voiceHostVersion = "";
    private string _voiceHostMessage = "";
    private string _searchStatusText = "Unknown";
    private string _searchStatusMessage = "Connect the runtime to inspect web search and MCP health.";
    private string _searchLastCheckedText = "";
    private string _searchSearxngStatusText = "Unknown";
    private string _searchSearxngMessage = "";
    private bool _searchSearxngReachable;
    private bool _searchSearxngManaged;
    private string _searchSearxngLastLaunchStatus = "";
    private string _searchSearchApiStatusText = "Unknown";
    private string _searchSearchApiMessage = "";
    private string _searchMcpStatusText = "Unknown";
    private string _searchMcpMessage = "";
    private string _searchMcpServerPath = "";
    private string _searchWebPermissionEffective = "";
    private bool _searchLiveSearchAvailable;
    private bool _searchMcpToolsAvailable;
    private string _searchLastProviderStatusText = "None";
    private string _searchLastProviderText = "";
    private string _searchLastQueryText = "";
    private string _searchLastPathText = "";
    private string _searchLastFailureText = "";
    private string _searchLastProviderCheckedText = "";
    private string _piperVoiceDownloadStatus = "";
    private bool _isPiperVoiceDownloading;
    private AudioDeviceOption? _selectedInputDevice;
    private AudioDeviceOption? _selectedOutputDevice;
    private double _inputGain = 1.0;

    public SettingsViewModel()
    {
        _appSettings = SettingsManager.Load();
        ChatMessageItem.Use24HourTime = _appSettings.Ui.Use24HourTime;
        StatusText = "Settings loaded.";
        LoadAudioDevices();
        RefreshVoiceCatalogs();
        InitializeVoiceHostHealthState();
        ResetSearchHealthState("Unknown", "Connect the runtime to inspect web search and MCP health.");
    }

    public string LlmBaseUrl
    {
        get => _appSettings.Llm.BaseUrl;
        set
        {
            if (_appSettings.Llm.BaseUrl != value)
            {
                _appSettings = _appSettings with { Llm = _appSettings.Llm with { BaseUrl = value } };
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectiveGatekeeperBaseUrl));
                MarkDirty();
            }
        }
    }

    public string LlmModel
    {
        get => _appSettings.Llm.Model;
        set
        {
            if (_appSettings.Llm.Model != value)
            {
                _appSettings = _appSettings with { Llm = _appSettings.Llm with { Model = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string GatekeeperBaseUrl
    {
        get => _appSettings.Llm.GatekeeperBaseUrl;
        set
        {
            if (_appSettings.Llm.GatekeeperBaseUrl != value)
            {
                _appSettings = _appSettings with { Llm = _appSettings.Llm with { GatekeeperBaseUrl = value } };
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectiveGatekeeperBaseUrl));
                MarkDirty();
            }
        }
    }

    public string EffectiveGatekeeperBaseUrl =>
        string.IsNullOrWhiteSpace(_appSettings.Llm.GatekeeperBaseUrl)
            ? NormalizeBaseUrl(_appSettings.Llm.BaseUrl, "http://localhost:1234")
            : NormalizeBaseUrl(
                _appSettings.Llm.GatekeeperBaseUrl,
                NormalizeBaseUrl(_appSettings.Llm.BaseUrl, "http://localhost:1234"));

    public string GatekeeperModelId
    {
        get => _appSettings.Llm.GatekeeperModelId;
        set
        {
            if (_appSettings.Llm.GatekeeperModelId != value)
            {
                _appSettings = _appSettings with { Llm = _appSettings.Llm with { GatekeeperModelId = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public int LlmMaxTokens
    {
        get => _appSettings.Llm.MaxTokens;
        set
        {
            if (_appSettings.Llm.MaxTokens != value)
            {
                _appSettings = _appSettings with { Llm = _appSettings.Llm with { MaxTokens = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public double LlmTemperature
    {
        get => _appSettings.Llm.Temperature;
        set
        {
            if (Math.Abs(_appSettings.Llm.Temperature - value) > 0.001)
            {
                _appSettings = _appSettings with { Llm = _appSettings.Llm with { Temperature = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public ObservableCollection<string> AvailablePrimaryModels { get; } = [];
    public ObservableCollection<string> AvailableGatekeeperModels { get; } = [];
    public ObservableCollection<string> AllowedFileRoots { get; } = [];

    public bool IsRefreshingModels
    {
        get => _isRefreshingModels;
        private set
        {
            if (_isRefreshingModels != value)
            {
                _isRefreshingModels = value;
                OnPropertyChanged();
            }
        }
    }

    public string[] TtsEngineOptions => ["windows", "piper", "kokoro"];
    public string[] YouTubeAsrProviderOptions => ["faster-whisper", "qwen3asr"];
    public string[] YouTubeDraftToneOptions => ["professional", "direct", "playful"];

    public bool VoiceHostEnabled
    {
        get => _appSettings.Voice.VoiceHostEnabled;
        set
        {
            if (_appSettings.Voice.VoiceHostEnabled != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { VoiceHostEnabled = value } };
                OnPropertyChanged();
                MarkDirty();

                if (!value)
                {
                    StopVoiceHostHealthPolling();
                    ResetVoiceHostHealthState("Disabled", "Enable Local VoiceHost to probe local voice readiness.");
                }
                else
                {
                    ResetVoiceHostHealthState("Checking...", "VoiceHost health will be probed when this tab is active.");
                }
            }
        }
    }

    public string VoiceHostBaseUrl
    {
        get => _appSettings.Voice.VoiceHostBaseUrl;
        set
        {
            if (_appSettings.Voice.VoiceHostBaseUrl != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { VoiceHostBaseUrl = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string VoiceTtsEngine
    {
        get => _appSettings.Voice.TtsEngine;
        set
        {
            if (_appSettings.Voice.TtsEngine != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { TtsEngine = value } };
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPiperEngine));
                OnPropertyChanged(nameof(IsKokoroEngine));
                OnPropertyChanged(nameof(IsWindowsEngine));
                OnPropertyChanged(nameof(VoiceSectionLabel));
                OnPropertyChanged(nameof(VoiceSectionDescription));
                OnPropertyChanged(nameof(SelectedPiperVoice));
                OnPropertyChanged(nameof(SelectedKokoroVoice));
                RefreshVoiceCatalogs();
                MarkDirty();
            }
        }
    }

    public string VoiceTtsModelId
    {
        get => _appSettings.Voice.TtsModelId;
        set
        {
            if (_appSettings.Voice.TtsModelId != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { TtsModelId = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string VoiceTtsVoiceId
    {
        get => _appSettings.Voice.TtsVoiceId;
        set
        {
            if (_appSettings.Voice.TtsVoiceId != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { TtsVoiceId = value } };
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedPiperVoice));
                OnPropertyChanged(nameof(SelectedKokoroVoice));
                MarkDirty();
            }
        }
    }

    /// <summary>Snapshot of the current voice settings for TTS callers.</summary>
    public VoiceSettings GetVoiceSettingsSnapshot() => _appSettings.Voice;

    public bool IsPiperEngine => VoiceTtsEngine.Equals("piper", StringComparison.OrdinalIgnoreCase);
    public bool IsKokoroEngine => VoiceTtsEngine.Equals("kokoro", StringComparison.OrdinalIgnoreCase);
    public bool IsWindowsEngine => VoiceTtsEngine.Equals("windows", StringComparison.OrdinalIgnoreCase);
    public string VoiceSectionLabel => IsKokoroEngine ? "Kokoro Voice" : IsPiperEngine ? "Piper Voice" : "Windows Voice";
    public string VoiceSectionDescription => IsKokoroEngine
        ? "Choose an installed Kokoro voice pack, or leave the voice blank to follow the active personality preference."
        : IsPiperEngine
            ? "Choose a Piper voice for the local backend. Entries marked '(download)' will be fetched through VoiceHost when selected."
            : "Windows speech uses the installed system voice, so extra model setup is optional.";

    public ObservableCollection<string> AvailablePiperVoices { get; } = [];
    public ObservableCollection<string> AvailableKokoroVoices { get; } = [];

    public string PiperVoiceDownloadStatus
    {
        get => _piperVoiceDownloadStatus;
        private set
        {
            if (!string.Equals(_piperVoiceDownloadStatus, value, StringComparison.Ordinal))
            {
                _piperVoiceDownloadStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPiperStatusVisible));
            }
        }
    }

    public bool IsPiperStatusVisible => !string.IsNullOrWhiteSpace(PiperVoiceDownloadStatus);

    public bool IsPiperVoiceDownloading
    {
        get => _isPiperVoiceDownloading;
        private set
        {
            if (_isPiperVoiceDownloading != value)
            {
                _isPiperVoiceDownloading = value;
                OnPropertyChanged();
            }
        }
    }

    public string? SelectedPiperVoice
    {
        get
        {
            var configured = NormalizeString(VoiceTtsVoiceId);
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = DefaultPiperVoiceId;
            }

            if (AvailablePiperVoices.Contains(configured))
            {
                return configured;
            }

            var downloadLabel = ToPiperDownloadLabel(configured);
            if (AvailablePiperVoices.Contains(downloadLabel))
            {
                return downloadLabel;
            }

            if (AvailablePiperVoices.Contains(DefaultPiperVoiceId))
            {
                return DefaultPiperVoiceId;
            }

            var defaultDownloadLabel = ToPiperDownloadLabel(DefaultPiperVoiceId);
            if (AvailablePiperVoices.Contains(defaultDownloadLabel))
            {
                return defaultDownloadLabel;
            }

            return AvailablePiperVoices.FirstOrDefault();
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = NormalizeString(value);
            var needsDownload = normalized.EndsWith(" (download)", StringComparison.Ordinal);
            var voiceId = StripPiperDownloadSuffix(normalized);
            VoiceTtsVoiceId = voiceId;

            if (needsDownload && !IsPiperVoiceDownloading)
            {
                _ = DownloadPiperVoiceAsync(voiceId);
            }
        }
    }

    public string? SelectedKokoroVoice
    {
        get
        {
            var configured = NormalizeString(VoiceTtsVoiceId);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            if (!IsKokoroEngine)
            {
                return string.Empty;
            }

            var preferredVoiceId = PersonalityVoicePreferenceResolver.ResolvePreferredTtsVoiceId(
                _appSettings,
                _appSettings.ActivePersonalityId,
                VoiceTtsEngine);
            return string.IsNullOrWhiteSpace(preferredVoiceId)
                ? DefaultKokoroVoiceId
                : preferredVoiceId;
        }
        set
        {
            var normalized = NormalizeString(value);
            if (string.IsNullOrWhiteSpace(normalized) && !IsKokoroEngine)
            {
                return;
            }

            VoiceTtsVoiceId = normalized;
        }
    }

    public string VoiceHostStatusText
    {
        get => _voiceHostStatusText;
        private set
        {
            if (!string.Equals(_voiceHostStatusText, value, StringComparison.Ordinal))
            {
                _voiceHostStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    public bool VoiceHostIsReachable
    {
        get => _voiceHostIsReachable;
        private set
        {
            if (_voiceHostIsReachable != value)
            {
                _voiceHostIsReachable = value;
                OnPropertyChanged();
            }
        }
    }

    public bool VoiceHostIsReady
    {
        get => _voiceHostIsReady;
        private set
        {
            if (_voiceHostIsReady != value)
            {
                _voiceHostIsReady = value;
                OnPropertyChanged();
            }
        }
    }

    public bool VoiceHostAsrReady
    {
        get => _voiceHostAsrReady;
        private set
        {
            if (_voiceHostAsrReady != value)
            {
                _voiceHostAsrReady = value;
                OnPropertyChanged();
            }
        }
    }

    public bool VoiceHostTtsReady
    {
        get => _voiceHostTtsReady;
        private set
        {
            if (_voiceHostTtsReady != value)
            {
                _voiceHostTtsReady = value;
                OnPropertyChanged();
            }
        }
    }

    public string VoiceHostVersion
    {
        get => _voiceHostVersion;
        private set
        {
            if (!string.Equals(_voiceHostVersion, value, StringComparison.Ordinal))
            {
                _voiceHostVersion = value;
                OnPropertyChanged();
            }
        }
    }

    public string VoiceHostMessage
    {
        get => _voiceHostMessage;
        private set
        {
            if (!string.Equals(_voiceHostMessage, value, StringComparison.Ordinal))
            {
                _voiceHostMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public string[] WebSearchModeOptions => ["auto", "searxng", "search_api", "google_news", "ddg_html", "manual"];

    public bool WebSearchEnabled
    {
        get => !string.Equals(WebSearchMode, "manual", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value == WebSearchEnabled)
            {
                return;
            }

            WebSearchMode = value ? "auto" : "manual";
        }
    }

    public string WebSearchMode
    {
        get => NormalizeWebSearchMode(_appSettings.WebSearch.Mode);
        set
        {
            var normalized = NormalizeWebSearchMode(value);
            if (!string.Equals(_appSettings.WebSearch.Mode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _appSettings = _appSettings with
                {
                    WebSearch = _appSettings.WebSearch with { Mode = normalized }
                };
                OnPropertyChanged();
                OnPropertyChanged(nameof(WebSearchEnabled));
                MarkDirty();
            }
        }
    }

    public string SearxngBaseUrl
    {
        get => _appSettings.WebSearch.SearxngBaseUrl;
        set
        {
            if (_appSettings.WebSearch.SearxngBaseUrl != value)
            {
                _appSettings = _appSettings with
                {
                    WebSearch = _appSettings.WebSearch with { SearxngBaseUrl = value }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public bool SearxngAutoStart
    {
        get => _appSettings.WebSearch.SearxngAutoStart;
        set
        {
            if (_appSettings.WebSearch.SearxngAutoStart != value)
            {
                _appSettings = _appSettings with
                {
                    WebSearch = _appSettings.WebSearch with { SearxngAutoStart = value }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string SearchApiKey
    {
        get => _appSettings.WebSearch.SearchApiKey;
        set
        {
            if (_appSettings.WebSearch.SearchApiKey != value)
            {
                _appSettings = _appSettings with
                {
                    WebSearch = _appSettings.WebSearch with { SearchApiKey = value }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string SearchApiBaseUrl
    {
        get => _appSettings.WebSearch.SearchApiBaseUrl;
        set
        {
            if (_appSettings.WebSearch.SearchApiBaseUrl != value)
            {
                _appSettings = _appSettings with
                {
                    WebSearch = _appSettings.WebSearch with { SearchApiBaseUrl = value }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string SearchApiEngine
    {
        get => _appSettings.WebSearch.SearchApiEngine;
        set
        {
            if (_appSettings.WebSearch.SearchApiEngine != value)
            {
                _appSettings = _appSettings with
                {
                    WebSearch = _appSettings.WebSearch with { SearchApiEngine = value }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public bool DisableAllFileAccess
    {
        get => _appSettings.DocumentReader.DisableAllFileAccess;
        set
        {
            if (_appSettings.DocumentReader.DisableAllFileAccess == value)
                return;

            _appSettings = _appSettings with
            {
                DocumentReader = _appSettings.DocumentReader with { DisableAllFileAccess = value }
            };
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public void AddAllowedFileRoot(string path)
    {
        var normalized = NormalizePathRoot(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (AllowedFileRoots.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("That folder is already allowed.");
            return;
        }

        AllowedFileRoots.Add(normalized);
        UpdateAllowedFileRootsInSettings();
        MarkDirty();
    }

    public void RemoveAllowedFileRoot(string? path)
    {
        var normalized = NormalizePathRoot(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var existing = AllowedFileRoots.FirstOrDefault(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return;

        AllowedFileRoots.Remove(existing);
        UpdateAllowedFileRootsInSettings();
        MarkDirty();
    }

    public int WebSearchTimeoutMs
    {
        get => _appSettings.WebSearch.TimeoutMs;
        set
        {
            if (_appSettings.WebSearch.TimeoutMs != value)
            {
                _appSettings = _appSettings with
                {
                    WebSearch = _appSettings.WebSearch with { TimeoutMs = value }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public int WebSearchMaxResults
    {
        get => _appSettings.WebSearch.MaxResults;
        set
        {
            if (_appSettings.WebSearch.MaxResults != value)
            {
                _appSettings = _appSettings with
                {
                    WebSearch = _appSettings.WebSearch with { MaxResults = value }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string SearchStatusText
    {
        get => _searchStatusText;
        private set
        {
            if (!string.Equals(_searchStatusText, value, StringComparison.Ordinal))
            {
                _searchStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchStatusMessage
    {
        get => _searchStatusMessage;
        private set
        {
            if (!string.Equals(_searchStatusMessage, value, StringComparison.Ordinal))
            {
                _searchStatusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchLastCheckedText
    {
        get => _searchLastCheckedText;
        private set
        {
            if (!string.Equals(_searchLastCheckedText, value, StringComparison.Ordinal))
            {
                _searchLastCheckedText = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SearchLiveSearchAvailable
    {
        get => _searchLiveSearchAvailable;
        private set
        {
            if (_searchLiveSearchAvailable != value)
            {
                _searchLiveSearchAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SearchLiveSearchAvailableText));
            }
        }
    }

    public string SearchLiveSearchAvailableText => SearchLiveSearchAvailable ? "Yes" : "No";

    public string SearchSearxngStatusText
    {
        get => _searchSearxngStatusText;
        private set
        {
            if (!string.Equals(_searchSearxngStatusText, value, StringComparison.Ordinal))
            {
                _searchSearxngStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchSearxngMessage
    {
        get => _searchSearxngMessage;
        private set
        {
            if (!string.Equals(_searchSearxngMessage, value, StringComparison.Ordinal))
            {
                _searchSearxngMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SearchSearxngReachable
    {
        get => _searchSearxngReachable;
        private set
        {
            if (_searchSearxngReachable != value)
            {
                _searchSearxngReachable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SearchSearxngReachableText));
            }
        }
    }

    public string SearchSearxngReachableText => SearchSearxngReachable ? "Yes" : "No";

    public bool SearchSearxngManaged
    {
        get => _searchSearxngManaged;
        private set
        {
            if (_searchSearxngManaged != value)
            {
                _searchSearxngManaged = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SearchSearxngManagedText));
            }
        }
    }

    public string SearchSearxngManagedText => SearchSearxngManaged ? "Yes" : "No";

    public string SearchSearxngLastLaunchStatus
    {
        get => _searchSearxngLastLaunchStatus;
        private set
        {
            if (!string.Equals(_searchSearxngLastLaunchStatus, value, StringComparison.Ordinal))
            {
                _searchSearxngLastLaunchStatus = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchSearchApiStatusText
    {
        get => _searchSearchApiStatusText;
        private set
        {
            if (!string.Equals(_searchSearchApiStatusText, value, StringComparison.Ordinal))
            {
                _searchSearchApiStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchSearchApiMessage
    {
        get => _searchSearchApiMessage;
        private set
        {
            if (!string.Equals(_searchSearchApiMessage, value, StringComparison.Ordinal))
            {
                _searchSearchApiMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchMcpStatusText
    {
        get => _searchMcpStatusText;
        private set
        {
            if (!string.Equals(_searchMcpStatusText, value, StringComparison.Ordinal))
            {
                _searchMcpStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchMcpMessage
    {
        get => _searchMcpMessage;
        private set
        {
            if (!string.Equals(_searchMcpMessage, value, StringComparison.Ordinal))
            {
                _searchMcpMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchMcpServerPath
    {
        get => _searchMcpServerPath;
        private set
        {
            if (!string.Equals(_searchMcpServerPath, value, StringComparison.Ordinal))
            {
                _searchMcpServerPath = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SearchMcpToolsAvailable
    {
        get => _searchMcpToolsAvailable;
        private set
        {
            if (_searchMcpToolsAvailable != value)
            {
                _searchMcpToolsAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SearchMcpToolsAvailableText));
            }
        }
    }

    public string SearchMcpToolsAvailableText => SearchMcpToolsAvailable ? "Yes" : "No";

    public string SearchWebPermissionEffective
    {
        get => _searchWebPermissionEffective;
        private set
        {
            if (!string.Equals(_searchWebPermissionEffective, value, StringComparison.Ordinal))
            {
                _searchWebPermissionEffective = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchLastProviderStatusText
    {
        get => _searchLastProviderStatusText;
        private set
        {
            if (!string.Equals(_searchLastProviderStatusText, value, StringComparison.Ordinal))
            {
                _searchLastProviderStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchLastProviderText
    {
        get => _searchLastProviderText;
        private set
        {
            if (!string.Equals(_searchLastProviderText, value, StringComparison.Ordinal))
            {
                _searchLastProviderText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchLastQueryText
    {
        get => _searchLastQueryText;
        private set
        {
            if (!string.Equals(_searchLastQueryText, value, StringComparison.Ordinal))
            {
                _searchLastQueryText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchLastPathText
    {
        get => _searchLastPathText;
        private set
        {
            if (!string.Equals(_searchLastPathText, value, StringComparison.Ordinal))
            {
                _searchLastPathText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchLastFailureText
    {
        get => _searchLastFailureText;
        private set
        {
            if (!string.Equals(_searchLastFailureText, value, StringComparison.Ordinal))
            {
                _searchLastFailureText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SearchLastProviderCheckedText
    {
        get => _searchLastProviderCheckedText;
        private set
        {
            if (!string.Equals(_searchLastProviderCheckedText, value, StringComparison.Ordinal))
            {
                _searchLastProviderCheckedText = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<AudioDeviceOption> AvailableInputDevices { get; } = [];
    public ObservableCollection<AudioDeviceOption> AvailableOutputDevices { get; } = [];

    public AudioDeviceOption? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            if (!Equals(_selectedInputDevice, value))
            {
                _selectedInputDevice = value;
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public AudioDeviceOption? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!Equals(_selectedOutputDevice, value))
            {
                _selectedOutputDevice = value;
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public double InputGain
    {
        get => _inputGain;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 2.0);
            if (Math.Abs(_inputGain - clamped) > 0.001)
            {
                _inputGain = clamped;
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string PttChord
    {
        get => _appSettings.Audio.PttChord;
        set
        {
            var normalized = NormalizeHotkeyChord(value, "Ctrl+Alt+M");
            if (!string.Equals(_appSettings.Audio.PttChord, normalized, StringComparison.Ordinal))
            {
                _appSettings = _appSettings with { Audio = _appSettings.Audio with { PttChord = normalized } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string ShutupChord
    {
        get => _appSettings.Audio.ShutupChord;
        set
        {
            var normalized = NormalizeHotkeyChord(value, "Ctrl+Alt+Escape");
            if (!string.Equals(_appSettings.Audio.ShutupChord, normalized, StringComparison.Ordinal))
            {
                _appSettings = _appSettings with { Audio = _appSettings.Audio with { ShutupChord = normalized } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    // -- Display settings --

    public string Location
    {
        get => _appSettings.GetEffectiveUserLocation(_appSettings.ActiveProfileId).GetResolvedLabel() ?? "";
        set
        {
            var normalized = NormalizeString(value);
            var current = _appSettings.GetEffectiveUserLocation(_appSettings.ActiveProfileId).GetResolvedLabel() ?? "";
            if (string.Equals(current, normalized, StringComparison.Ordinal))
            {
                return;
            }

            var mode = string.IsNullOrWhiteSpace(normalized) ? "unset" : "manual";
            var updatedAt = mode == "manual" ? DateTimeOffset.UtcNow.ToString("O") : "";

            var normalizedLocation = new LocationSettings
            {
                Mode = mode,
                Value = normalized,
                UpdatedAt = updatedAt,
                Enabled = mode == "manual",
                Label = normalized,
                Timezone = "",
                Latitude = null,
                Longitude = null
            };

            var activeProfileKey = AppSettings.NormalizeLocationProfileKey(_appSettings.ActiveProfileId);
            var locationsByProfile = new Dictionary<string, LocationSettings>(
                _appSettings.UserProfile.LocationsByProfile,
                StringComparer.OrdinalIgnoreCase)
            {
                [activeProfileKey] = normalizedLocation
            };

            _appSettings = _appSettings with
            {
                Location = normalizedLocation,
                UserProfile = _appSettings.UserProfile with
                {
                    Location = normalizedLocation,
                    LocationsByProfile = locationsByProfile
                }
            };

            OnPropertyChanged();
            MarkDirty();
        }
    }

    public bool Use24HourTime
    {
        get => _appSettings.Ui.Use24HourTime;
        set
        {
            if (_appSettings.Ui.Use24HourTime != value)
            {
                _appSettings = _appSettings with { Ui = _appSettings.Ui with { Use24HourTime = value } };
                ChatMessageItem.Use24HourTime = value;
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public static string[] PreferredUnitsOptions { get; } = ["imperial", "metric"];

    public string PreferredUnits
    {
        get => _appSettings.Weather.PreferredUnits;
        set
        {
            if (!string.Equals(_appSettings.Weather.PreferredUnits, value, StringComparison.Ordinal))
            {
                _appSettings = _appSettings with { Weather = _appSettings.Weather with { PreferredUnits = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    // -- MCP Permission ComboBox option sources --
    public static string[] PermissionOptions { get; } = ["ask", "always", "off"];
    public static string[] DeveloperOverrideOptions { get; } = ["none", "ask", "always", "off"];

    public string McpDeveloperOverride
    {
        get => _appSettings.Mcp.Permissions.DeveloperOverride;
        set
        {
            if (_appSettings.Mcp.Permissions.DeveloperOverride != value)
            {
                _appSettings = _appSettings with
                {
                    Mcp = _appSettings.Mcp with
                    {
                        Permissions = _appSettings.Mcp.Permissions with { DeveloperOverride = value }
                    }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string McpScreen
    {
        get => _appSettings.Mcp.Permissions.Screen;
        set
        {
            if (_appSettings.Mcp.Permissions.Screen != value)
            {
                _appSettings = _appSettings with
                {
                    Mcp = _appSettings.Mcp with
                    {
                        Permissions = _appSettings.Mcp.Permissions with { Screen = value }
                    }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string McpFiles
    {
        get => _appSettings.Mcp.Permissions.Files;
        set
        {
            if (_appSettings.Mcp.Permissions.Files != value)
            {
                _appSettings = _appSettings with
                {
                    Mcp = _appSettings.Mcp with
                    {
                        Permissions = _appSettings.Mcp.Permissions with { Files = value }
                    }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string McpSystem
    {
        get => _appSettings.Mcp.Permissions.System;
        set
        {
            if (_appSettings.Mcp.Permissions.System != value)
            {
                _appSettings = _appSettings with
                {
                    Mcp = _appSettings.Mcp with
                    {
                        Permissions = _appSettings.Mcp.Permissions with { System = value }
                    }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string McpWeb
    {
        get => _appSettings.Mcp.Permissions.Web;
        set
        {
            if (_appSettings.Mcp.Permissions.Web != value)
            {
                _appSettings = _appSettings with
                {
                    Mcp = _appSettings.Mcp with
                    {
                        Permissions = _appSettings.Mcp.Permissions with { Web = value }
                    }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string McpMemoryRead
    {
        get => _appSettings.Mcp.Permissions.MemoryRead;
        set
        {
            if (_appSettings.Mcp.Permissions.MemoryRead != value)
            {
                _appSettings = _appSettings with
                {
                    Mcp = _appSettings.Mcp with
                    {
                        Permissions = _appSettings.Mcp.Permissions with { MemoryRead = value }
                    }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string McpMemoryWrite
    {
        get => _appSettings.Mcp.Permissions.MemoryWrite;
        set
        {
            if (_appSettings.Mcp.Permissions.MemoryWrite != value)
            {
                _appSettings = _appSettings with
                {
                    Mcp = _appSettings.Mcp with
                    {
                        Permissions = _appSettings.Mcp.Permissions with { MemoryWrite = value }
                    }
                };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string YouTubeAsrProvider
    {
        get => _appSettings.Voice.YouTubeAsrProvider;
        set
        {
            if (_appSettings.Voice.YouTubeAsrProvider != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { YouTubeAsrProvider = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string YouTubeAsrModelId
    {
        get => _appSettings.Voice.YouTubeAsrModelId;
        set
        {
            if (_appSettings.Voice.YouTubeAsrModelId != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { YouTubeAsrModelId = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string YouTubeLanguageHint
    {
        get => _appSettings.Voice.YouTubeLanguageHint;
        set
        {
            if (_appSettings.Voice.YouTubeLanguageHint != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { YouTubeLanguageHint = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public string YouTubeDraftTone
    {
        get => _appSettings.Voice.YouTubeDraftTone;
        set
        {
            if (_appSettings.Voice.YouTubeDraftTone != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { YouTubeDraftTone = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public bool YouTubeKeepAudio
    {
        get => _appSettings.Voice.YouTubeKeepAudio;
        set
        {
            if (_appSettings.Voice.YouTubeKeepAudio != value)
            {
                _appSettings = _appSettings with { Voice = _appSettings.Voice with { YouTubeKeepAudio = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public int MaxToolCallsPerTurn
    {
        get => _appSettings.ToolBudgets.MaxToolCallsPerTurn;
        set
        {
            if (_appSettings.ToolBudgets.MaxToolCallsPerTurn != value)
            {
                _appSettings = _appSettings with { ToolBudgets = _appSettings.ToolBudgets with { MaxToolCallsPerTurn = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public int MaxToolCallsPerSession
    {
        get => _appSettings.ToolBudgets.MaxToolCallsPerSession;
        set
        {
            if (_appSettings.ToolBudgets.MaxToolCallsPerSession != value)
            {
                _appSettings = _appSettings with { ToolBudgets = _appSettings.ToolBudgets with { MaxToolCallsPerSession = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public int MaxWebPullsPerTurn
    {
        get => _appSettings.ToolBudgets.MaxWebPullsPerTurn;
        set
        {
            if (_appSettings.ToolBudgets.MaxWebPullsPerTurn != value)
            {
                _appSettings = _appSettings with { ToolBudgets = _appSettings.ToolBudgets with { MaxWebPullsPerTurn = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public int MaxFileOpsPerMinute
    {
        get => _appSettings.ToolBudgets.MaxFileOpsPerMinute;
        set
        {
            if (_appSettings.ToolBudgets.MaxFileOpsPerMinute != value)
            {
                _appSettings = _appSettings with { ToolBudgets = _appSettings.ToolBudgets with { MaxFileOpsPerMinute = value } };
                OnPropertyChanged();
                MarkDirty();
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty != value)
            {
                _isDirty = value;
                OnPropertyChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (!string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }
    }
    public async Task OnLlmsTabActivatedAsync()
    {
        if (!_llmsTabActivated || AvailablePrimaryModels.Count == 0 || AvailableGatekeeperModels.Count == 0)
        {
            _llmsTabActivated = true;
            await RefreshPrimaryModelsAsync();
            await RefreshGatekeeperModelsAsync();
        }
    }

    public Task RefreshPrimaryModelsAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(LlmBaseUrl, "http://localhost:1234");
        return RefreshModelsAsync(
            baseUrl,
            AvailablePrimaryModels,
            successPrefix: "Primary",
            errorPrefix: "Primary endpoint",
            cancellationToken);
    }

    public Task RefreshGatekeeperModelsAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(EffectiveGatekeeperBaseUrl, "http://localhost:1234");
        return RefreshModelsAsync(
            baseUrl,
            AvailableGatekeeperModels,
            successPrefix: "Gatekeeper",
            errorPrefix: "Gatekeeper endpoint",
            cancellationToken);
    }

    public void RefreshVoiceCatalogs(string? statusText = null)
    {
        RefreshPiperVoiceCatalog();
        RefreshKokoroVoiceCatalog();

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            StatusText = statusText;
        }
    }

    public void StartVoiceHostHealthPolling()
    {
        if (!VoiceHostEnabled || _voiceHostHealthPollCts is not null)
        {
            return;
        }

        _voiceHostHealthPollCts = new CancellationTokenSource();
        _ = PollVoiceHostHealthAsync(_voiceHostHealthPollCts.Token);
    }

    public void StopVoiceHostHealthPolling()
    {
        try
        {
            _voiceHostHealthPollCts?.Cancel();
        }
        catch
        {
            // Best effort shutdown only.
        }

        _voiceHostHealthPollCts?.Dispose();
        _voiceHostHealthPollCts = null;
    }

    public async Task RefreshVoiceHostHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!VoiceHostEnabled)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ResetVoiceHostHealthState("Disabled", "Enable Local VoiceHost to probe local voice readiness."));
            return;
        }

        var baseUrl = NormalizeBaseUrl(VoiceHostBaseUrl, "http://127.0.0.1:17845");
        var healthUri = $"{baseUrl}{NormalizeHealthPath(_appSettings.Voice.VoiceHostHealthPath)}";

        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };

            using var response = await http.GetAsync(healthUri, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ApplyVoiceHostState(
                        reachable: true,
                        ready: false,
                        asrReady: false,
                        ttsReady: false,
                        status: "Error",
                        version: "",
                        message: $"VoiceHost health returned {(int)response.StatusCode}."));
                return;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ApplyVoiceHostState(
                        reachable: true,
                        ready: false,
                        asrReady: false,
                        ttsReady: false,
                        status: "Error",
                        version: "",
                        message: "VoiceHost health endpoint returned an empty body."));
                return;
            }

            var snapshot = JsonSerializer.Deserialize<VoiceHostHealthResponse>(body, JsonOptions);
            if (snapshot is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ApplyVoiceHostState(
                        reachable: true,
                        ready: false,
                        asrReady: false,
                        ttsReady: false,
                        status: "Error",
                        version: "",
                        message: "VoiceHost health payload could not be parsed."));
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                ApplyVoiceHostState(
                    reachable: true,
                    ready: snapshot.Ready,
                    asrReady: snapshot.AsrReady,
                    ttsReady: snapshot.TtsReady,
                    status: ResolveVoiceHostStatusLabel(snapshot),
                    version: snapshot.Version ?? "",
                    message: NormalizeVoiceHostMessage(snapshot, baseUrl)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore caller-driven cancellations.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ApplyVoiceHostState(
                    reachable: false,
                    ready: false,
                    asrReady: false,
                    ttsReady: false,
                    status: "Unreachable",
                    version: "",
                message: ex.Message));
        }
    }

    public void ResetSearchHealthState(string status, string message)
    {
        ApplySearchHealthState(
            status: status,
            message: message,
            liveSearchAvailable: false,
            checkedAtText: "",
            searxngStatus: "Unknown",
            searxngMessage: "",
            searxngReachable: false,
            searxngManaged: false,
            searxngLastLaunchStatus: "",
            searchApiStatus: "Unknown",
            searchApiMessage: "",
            mcpStatus: "Unknown",
            mcpMessage: "",
            mcpServerPath: "",
            mcpToolsAvailable: false,
            webPermission: "",
            lastProviderStatus: "None",
            lastProvider: "",
            lastQuery: "",
            lastPath: "",
            lastFailure: "",
            lastProviderCheckedText: "");
    }

    public void ApplySearchStatus(SearchStatusResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var checkedAt = snapshot.CheckedAtUtc == default
            ? ""
            : snapshot.CheckedAtUtc.ToLocalTime().ToString("g");
        var requestedQuery = snapshot.LastProviderTrace.RequestedQuery;
        var effectiveQuery = snapshot.LastProviderTrace.EffectiveQuery;
        var lastQuery = !string.IsNullOrWhiteSpace(requestedQuery) &&
                        !string.IsNullOrWhiteSpace(effectiveQuery) &&
                        !string.Equals(requestedQuery, effectiveQuery, StringComparison.Ordinal)
            ? requestedQuery + " -> " + effectiveQuery
            : !string.IsNullOrWhiteSpace(requestedQuery)
                ? requestedQuery
                : effectiveQuery;
        var lastProviderCheckedAt = snapshot.LastProviderTrace.RecordedAtUtc == default
            ? ""
            : snapshot.LastProviderTrace.RecordedAtUtc.ToLocalTime().ToString("g");

        ApplySearchHealthState(
            status: snapshot.EffectiveStatus,
            message: snapshot.EffectiveMessage,
            liveSearchAvailable: snapshot.LiveSearchAvailable,
            checkedAtText: checkedAt,
            searxngStatus: snapshot.Searxng.Status,
            searxngMessage: snapshot.Searxng.Message,
            searxngReachable: snapshot.Searxng.Reachable,
            searxngManaged: snapshot.Searxng.ManagedByRuntime,
            searxngLastLaunchStatus: snapshot.Searxng.LastLaunchStatus,
            searchApiStatus: snapshot.SearchApi.Status,
            searchApiMessage: snapshot.SearchApi.Message,
            mcpStatus: snapshot.Mcp.Status,
            mcpMessage: snapshot.Mcp.Message,
            mcpServerPath: snapshot.Mcp.ServerPath,
            mcpToolsAvailable: snapshot.Mcp.ToolsAvailable,
            webPermission: snapshot.WebPermission,
            lastProviderStatus: snapshot.LastProviderTrace.Status,
            lastProvider: snapshot.LastProviderTrace.Provider,
            lastQuery: lastQuery,
            lastPath: snapshot.LastProviderTrace.PathSummary,
            lastFailure: snapshot.LastProviderTrace.Failure,
            lastProviderCheckedText: lastProviderCheckedAt);
    }

    public AppSettings BuildPersistableSnapshot()
    {
        var latest = SettingsManager.Load();

        return latest with
        {
            Llm = latest.Llm with
            {
                BaseUrl = NormalizeBaseUrl(LlmBaseUrl, latest.Llm.BaseUrl),
                Model = NormalizeString(LlmModel),
                GatekeeperBaseUrl = NormalizeOptionalBaseUrl(GatekeeperBaseUrl),
                GatekeeperModelId = NormalizeStringOrFallback(GatekeeperModelId, latest.Llm.GatekeeperModelId),
                MaxTokens = Math.Max(1, LlmMaxTokens),
                Temperature = Math.Clamp(LlmTemperature, 0.0, 2.0)
            },
            Audio = latest.Audio with
            {
                PttChord = NormalizeHotkeyChord(PttChord, latest.Audio.PttChord),
                ShutupChord = NormalizeHotkeyChord(ShutupChord, latest.Audio.ShutupChord),
                InputDeviceName = NormalizeString(SelectedInputDevice?.ProductName),
                OutputDeviceName = NormalizeString(SelectedOutputDevice?.ProductName),
                InputGain = Math.Clamp(InputGain, 0.0, 2.0)
            },
            Voice = latest.Voice with
            {
                VoiceHostEnabled = VoiceHostEnabled,
                VoiceHostBaseUrl = NormalizeBaseUrl(VoiceHostBaseUrl, latest.Voice.VoiceHostBaseUrl),
                TtsEngine = NormalizeToken(VoiceTtsEngine, latest.Voice.TtsEngine),
                TtsModelId = NormalizeString(VoiceTtsModelId),
                TtsVoiceId = NormalizeString(VoiceTtsVoiceId),
                YouTubeAsrProvider = NormalizeToken(YouTubeAsrProvider, latest.Voice.YouTubeAsrProvider),
                YouTubeAsrModelId = NormalizeStringOrFallback(YouTubeAsrModelId, latest.Voice.YouTubeAsrModelId),
                YouTubeLanguageHint = NormalizeString(YouTubeLanguageHint),
                YouTubeDraftTone = NormalizeDraftTone(YouTubeDraftTone, latest.Voice.YouTubeDraftTone),
                YouTubeKeepAudio = YouTubeKeepAudio
            },
            Mcp = latest.Mcp with
            {
                Permissions = latest.Mcp.Permissions with
                {
                    DeveloperOverride = NormalizePermission(McpDeveloperOverride, latest.Mcp.Permissions.DeveloperOverride),
                    Screen = NormalizePermission(McpScreen, latest.Mcp.Permissions.Screen),
                    Files = NormalizePermission(McpFiles, latest.Mcp.Permissions.Files),
                    System = NormalizePermission(McpSystem, latest.Mcp.Permissions.System),
                    Web = NormalizePermission(McpWeb, latest.Mcp.Permissions.Web),
                    MemoryRead = NormalizePermission(McpMemoryRead, latest.Mcp.Permissions.MemoryRead),
                    MemoryWrite = NormalizePermission(McpMemoryWrite, latest.Mcp.Permissions.MemoryWrite)
                }
            },
            WebSearch = latest.WebSearch with
            {
                Mode = NormalizeWebSearchMode(WebSearchMode),
                SearxngBaseUrl = NormalizeBaseUrl(SearxngBaseUrl, latest.WebSearch.SearxngBaseUrl),
                SearxngAutoStart = SearxngAutoStart,
                SearchApiKey = NormalizeString(SearchApiKey),
                SearchApiBaseUrl = NormalizeBaseUrl(SearchApiBaseUrl, latest.WebSearch.SearchApiBaseUrl),
                SearchApiEngine = NormalizeStringOrFallback(SearchApiEngine, latest.WebSearch.SearchApiEngine),
                TimeoutMs = Math.Clamp(WebSearchTimeoutMs, 1_000, 120_000),
                MaxResults = Math.Clamp(WebSearchMaxResults, 1, 20)
            },
            DocumentReader = latest.DocumentReader with
            {
                DisableAllFileAccess = DisableAllFileAccess,
                AllowedRoots = AllowedFileRoots
                    .Select(NormalizePathRoot)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            },
            ToolBudgets = new ToolBudgetSettings
            {
                Enabled = latest.ToolBudgets.Enabled,
                MaxToolCallsPerTurn = Math.Max(1, MaxToolCallsPerTurn),
                MaxToolCallsPerSession = Math.Max(1, MaxToolCallsPerSession),
                MaxWebPullsPerTurn = Math.Max(0, MaxWebPullsPerTurn),
                MaxFileOpsPerMinute = Math.Max(0, MaxFileOpsPerMinute)
            }.Normalize(),
            Ui = latest.Ui with
            {
                Use24HourTime = Use24HourTime
            },
            Weather = latest.Weather with
            {
                PreferredUnits = PreferredUnits
            }
        };
    }

    public AppSettings SaveLocally(string? statusText = null)
    {
        var snapshot = BuildPersistableSnapshot();
        SettingsManager.Save(snapshot);
        var persisted = SettingsManager.Load();
        ApplySnapshot(persisted, statusText ?? "Settings saved locally.");
        return persisted;
    }

    public void ApplySavedSnapshot(AppSettings savedSettings, string? statusText = null)
    {
        ApplySnapshot(savedSettings, statusText ?? "Settings saved.");
    }

    public void Reload()
    {
        ApplySnapshot(SettingsManager.Load(), "Settings reloaded.");
    }

    public void ApplyActiveIdentity(string? activeProfileId, string? activePersonalityId)
    {
        var changed = false;
        var profileId = NormalizeString(activeProfileId);
        var personalityId = NormalizeString(activePersonalityId);

        if (!string.IsNullOrWhiteSpace(profileId) &&
            !string.Equals(_appSettings.ActiveProfileId, profileId, StringComparison.OrdinalIgnoreCase))
        {
            _appSettings = _appSettings with { ActiveProfileId = profileId };
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(personalityId) &&
            !string.Equals(_appSettings.ActivePersonalityId, personalityId, StringComparison.OrdinalIgnoreCase))
        {
            _appSettings = _appSettings with { ActivePersonalityId = personalityId };
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        RefreshVoiceCatalogs();
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(SelectedKokoroVoice));
    }

    public void SetStatus(string statusText)
    {
        StatusText = statusText;
    }

    public void SetVoiceHostStatus(string status, string message)
    {
        ApplyVoiceHostState(
            reachable: false,
            ready: false,
            asrReady: false,
            ttsReady: false,
            status: status,
            version: string.Empty,
            message: message);
    }

    private async Task<bool> DownloadPiperVoiceAsync(string voiceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(voiceId) || IsPiperVoiceDownloading)
        {
            return false;
        }

        IsPiperVoiceDownloading = true;
        PiperVoiceDownloadStatus = $"Downloading '{voiceId}' (~60 MB)...";

        try
        {
            var baseUrl = NormalizeBaseUrl(VoiceHostBaseUrl, "http://127.0.0.1:17845");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var content = new StringContent(
                JsonSerializer.Serialize(new { voiceId }),
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await client.PostAsync($"{baseUrl}/api/piper/download", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RefreshPiperVoiceCatalog();
                    OnPropertyChanged(nameof(SelectedPiperVoice));
                });

                PiperVoiceDownloadStatus = $"Installed '{voiceId}'.";
                return true;
            }

            var detail = string.IsNullOrWhiteSpace(body)
                ? $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})"
                : body[..Math.Min(body.Length, 200)];
            PiperVoiceDownloadStatus = $"Download failed ({(int)response.StatusCode}): {detail}";
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PiperVoiceDownloadStatus = "Download canceled.";
            return false;
        }
        catch (HttpRequestException ex)
        {
            PiperVoiceDownloadStatus = $"Download failed: VoiceHost unreachable ({ex.Message})";
            return false;
        }
        catch (TaskCanceledException)
        {
            PiperVoiceDownloadStatus = "Download failed: request timed out (10 min limit).";
            return false;
        }
        catch (Exception ex)
        {
            PiperVoiceDownloadStatus = $"Download error: {ex.Message}";
            return false;
        }
        finally
        {
            IsPiperVoiceDownloading = false;
        }
    }

    private async Task RefreshModelsAsync(
        string baseUrl,
        ObservableCollection<string> targetCollection,
        string successPrefix,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        if (IsRefreshingModels)
        {
            return;
        }

        IsRefreshingModels = true;

        try
        {
            var modelIds = await FetchModelIdsAsync(baseUrl, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ReplaceCollection(targetCollection, modelIds);
                StatusText = $"{successPrefix}: found {modelIds.Count} model(s) at {baseUrl}.";

                if (targetCollection == AvailablePrimaryModels && string.IsNullOrWhiteSpace(LlmModel) && modelIds.Count > 0)
                {
                    LlmModel = modelIds[0];
                }
                else if (targetCollection == AvailableGatekeeperModels && string.IsNullOrWhiteSpace(GatekeeperModelId) && modelIds.Count > 0)
                {
                    GatekeeperModelId = modelIds[0];
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore caller-driven cancellations.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                targetCollection.Clear();
                StatusText = $"{errorPrefix} unreachable: {ex.Message}";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsRefreshingModels = false);
        }
    }

    private static async Task<IReadOnlyList<string>> FetchModelIdsAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        using var response = await http.GetAsync($"{baseUrl}/v1/models", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);

        var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    modelIds.Add(id.GetString()!);
                }
            }
        }

        return modelIds
            .OrderBy(modelId => modelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task PollVoiceHostHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshVoiceHostHealthAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    public void RefreshAudioDevices()
    {
        LoadAudioDevices();
        StatusText = "Audio devices refreshed.";
    }


    private void RefreshPiperVoiceCatalog()
    {
        var discovered = PiperVoiceCatalog.Discover();
        var items = new List<string>(discovered.Count + 2);

        foreach (var entry in discovered)
        {
            items.Add(entry.IsInstalled ? entry.VoiceId : ToPiperDownloadLabel(entry.VoiceId));
        }

        if (!items.Contains(DefaultPiperVoiceId, StringComparer.OrdinalIgnoreCase) &&
            !items.Contains(ToPiperDownloadLabel(DefaultPiperVoiceId), StringComparer.OrdinalIgnoreCase))
        {
            items.Add(DefaultPiperVoiceId);
        }

        var configured = NormalizeString(VoiceTtsVoiceId);
        if (IsPiperEngine &&
            !string.IsNullOrWhiteSpace(configured) &&
            !items.Contains(configured, StringComparer.OrdinalIgnoreCase) &&
            !items.Contains(ToPiperDownloadLabel(configured), StringComparer.OrdinalIgnoreCase))
        {
            items.Add(configured);
        }

        ReplaceCollection(AvailablePiperVoices, items);
        OnPropertyChanged(nameof(SelectedPiperVoice));
    }

    private void RefreshKokoroVoiceCatalog()
    {
        var discovered = KokoroVoiceCatalog.Discover();
        var items = new List<string>(discovered);

        var configured = NormalizeString(VoiceTtsVoiceId);
        if (IsKokoroEngine &&
            !string.IsNullOrWhiteSpace(configured) &&
            !items.Contains(configured, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(configured);
        }

        var effective = NormalizeString(SelectedKokoroVoice);
        if (!string.IsNullOrWhiteSpace(effective) &&
            !items.Contains(effective, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(effective);
        }

        ReplaceCollection(AvailableKokoroVoices, items);
        OnPropertyChanged(nameof(SelectedKokoroVoice));
    }

    private void LoadAudioDevices()
    {
        ReplaceAudioDeviceCollection(AvailableInputDevices, AudioDeviceCatalog.GetInputDevices());
        ReplaceAudioDeviceCollection(AvailableOutputDevices, AudioDeviceCatalog.GetOutputDevices());

        _selectedInputDevice = ResolveSelectedDevice(AvailableInputDevices, _appSettings.Audio.InputDeviceName);
        _selectedOutputDevice = ResolveSelectedDevice(AvailableOutputDevices, _appSettings.Audio.OutputDeviceName);
        _inputGain = Math.Clamp(_appSettings.Audio.InputGain, 0.0, 2.0);

        OnPropertyChanged(nameof(SelectedInputDevice));
        OnPropertyChanged(nameof(SelectedOutputDevice));
        OnPropertyChanged(nameof(InputGain));
    }

    private static void ReplaceAudioDeviceCollection(ObservableCollection<AudioDeviceOption> target, IReadOnlyList<AudioDeviceOption> devices)
    {
        target.Clear();
        foreach (var device in devices)
        {
            target.Add(device);
        }
    }

    private static AudioDeviceOption? ResolveSelectedDevice(IEnumerable<AudioDeviceOption> devices, string? productName)
    {
        var normalized = NormalizeString(productName);
        if (string.IsNullOrEmpty(normalized))
        {
            return devices.FirstOrDefault();
        }

        return devices.FirstOrDefault(device => string.Equals(device.ProductName, normalized, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault();
    }

    private void InitializeVoiceHostHealthState()
    {
        if (VoiceHostEnabled)
        {
            ResetVoiceHostHealthState("Checking...", "VoiceHost health will be probed when this tab is active.");
        }
        else
        {
            ResetVoiceHostHealthState("Disabled", "Enable Local VoiceHost to probe local voice readiness.");
        }
    }

    private void ResetVoiceHostHealthState(string status, string message)
    {
        ApplyVoiceHostState(
            reachable: false,
            ready: false,
            asrReady: false,
            ttsReady: false,
            status: status,
            version: "",
            message: message);
    }

    private void ApplyVoiceHostState(
        bool reachable,
        bool ready,
        bool asrReady,
        bool ttsReady,
        string status,
        string version,
        string message)
    {
        VoiceHostIsReachable = reachable;
        VoiceHostIsReady = ready;
        VoiceHostAsrReady = asrReady;
        VoiceHostTtsReady = ttsReady;
        VoiceHostStatusText = status;
        VoiceHostVersion = version;
        VoiceHostMessage = message;
    }

    private void ApplySearchHealthState(
        string status,
        string message,
        bool liveSearchAvailable,
        string checkedAtText,
        string searxngStatus,
        string searxngMessage,
        bool searxngReachable,
        bool searxngManaged,
        string searxngLastLaunchStatus,
        string searchApiStatus,
        string searchApiMessage,
        string mcpStatus,
        string mcpMessage,
        string mcpServerPath,
        bool mcpToolsAvailable,
        string webPermission,
        string lastProviderStatus,
        string lastProvider,
        string lastQuery,
        string lastPath,
        string lastFailure,
        string lastProviderCheckedText)
    {
        SearchStatusText = status;
        SearchStatusMessage = message;
        SearchLiveSearchAvailable = liveSearchAvailable;
        SearchLastCheckedText = checkedAtText;
        SearchSearxngStatusText = searxngStatus;
        SearchSearxngMessage = searxngMessage;
        SearchSearxngReachable = searxngReachable;
        SearchSearxngManaged = searxngManaged;
        SearchSearxngLastLaunchStatus = searxngLastLaunchStatus;
        SearchSearchApiStatusText = searchApiStatus;
        SearchSearchApiMessage = searchApiMessage;
        SearchMcpStatusText = mcpStatus;
        SearchMcpMessage = mcpMessage;
        SearchMcpServerPath = mcpServerPath;
        SearchMcpToolsAvailable = mcpToolsAvailable;
        SearchWebPermissionEffective = webPermission;
        SearchLastProviderStatusText = lastProviderStatus;
        SearchLastProviderText = lastProvider;
        SearchLastQueryText = lastQuery;
        SearchLastPathText = lastPath;
        SearchLastFailureText = lastFailure;
        SearchLastProviderCheckedText = lastProviderCheckedText;
    }

    private void ApplySnapshot(AppSettings settings, string statusText)
    {
        var priorSettings = _appSettings;
        _appSettings = settings;
        ReplaceCollection(AllowedFileRoots, settings.DocumentReader.AllowedRoots);
        ChatMessageItem.Use24HourTime = settings.Ui.Use24HourTime;
        IsDirty = false;
        StatusText = statusText;
        LoadAudioDevices();
        RefreshVoiceCatalogs();
        InitializeVoiceHostHealthState();
        OnPropertyChanged(string.Empty);
        if (priorSettings.Voice.VoiceHostEnabled != settings.Voice.VoiceHostEnabled)
        {
            OnPropertyChanged(nameof(VoiceHostEnabled));
        }

        if (!string.Equals(priorSettings.Voice.VoiceHostBaseUrl, settings.Voice.VoiceHostBaseUrl, StringComparison.Ordinal) &&
            priorSettings.Voice.VoiceHostEnabled == settings.Voice.VoiceHostEnabled)
        {
            OnPropertyChanged(nameof(VoiceHostBaseUrl));
        }

        OnPropertyChanged(nameof(EffectiveGatekeeperBaseUrl));
        OnPropertyChanged(nameof(IsPiperEngine));
        OnPropertyChanged(nameof(IsKokoroEngine));
        OnPropertyChanged(nameof(IsWindowsEngine));
        OnPropertyChanged(nameof(VoiceSectionLabel));
        OnPropertyChanged(nameof(VoiceSectionDescription));
        OnPropertyChanged(nameof(SelectedPiperVoice));
        OnPropertyChanged(nameof(SelectedKokoroVoice));
    }

    private void MarkDirty()
    {
        IsDirty = true;
        StatusText = "Unsaved changes.";
    }

    private static string ResolveVoiceHostStatusLabel(VoiceHostHealthResponse snapshot)
    {
        if (snapshot.Ready)
        {
            return "Ready";
        }

        return string.Equals(snapshot.Status, "loading", StringComparison.OrdinalIgnoreCase)
            ? "Warming up"
            : string.IsNullOrWhiteSpace(snapshot.Status)
                ? "Unavailable"
                : snapshot.Status.Trim();
    }

    private static string NormalizeVoiceHostMessage(VoiceHostHealthResponse snapshot, string baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Message))
        {
            return snapshot.Message!.Trim();
        }

        return snapshot.Ready
            ? $"VoiceHost reachable at {baseUrl}."
            : $"VoiceHost reachable at {baseUrl}, but dependencies are still loading.";
    }
    private static void ReplaceCollection(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static string StripPiperDownloadSuffix(string value)
    {
        const string suffix = " (download)";
        return value.EndsWith(suffix, StringComparison.Ordinal)
            ? value[..^suffix.Length]
            : value;
    }

    private static string ToPiperDownloadLabel(string voiceId) => $"{voiceId} (download)";

    private static string NormalizeBaseUrl(string? value, string fallback)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return trimmed.TrimEnd('/');
    }

    private static string NormalizeOptionalBaseUrl(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().TrimEnd('/');
    }

    private static string NormalizeHealthPath(string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "/health" : path.Trim();
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private static string NormalizeString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static string NormalizeStringOrFallback(string? value, string fallback)
    {
        var normalized = NormalizeString(value);
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private void UpdateAllowedFileRootsInSettings()
    {
        _appSettings = _appSettings with
        {
            DocumentReader = _appSettings.DocumentReader with
            {
                AllowedRoots = AllowedFileRoots.ToArray()
            }
        };
    }

    private static string NormalizePathRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                return fullPath;

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeHotkeyChord(string? value, string fallback)
    {
        var normalized = NormalizeString(value);
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeToken(string? value, string fallback)
    {
        var normalized = NormalizeStringOrFallback(value, fallback);
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeDraftTone(string? value, string fallback)
    {
        var normalized = NormalizeToken(value, fallback);
        return normalized switch
        {
            "professional" => "professional",
            "direct" => "direct",
            "playful" => "playful",
            _ => NormalizeToken(fallback, "professional")
        };
    }

    private static string NormalizePermission(string? value, string fallback)
    {
        var normalized = NormalizeToken(value, fallback);
        return normalized switch
        {
            "none" => "none",
            "ask" => "ask",
            "always" => "always",
            "off" => "off",
            _ => NormalizeToken(fallback, "ask")
        };
    }

    private static string NormalizeWebSearchMode(string? value)
    {
        var normalized = NormalizeToken(value, "auto");
        return normalized switch
        {
            "auto" => "auto",
            "searxng" => "searxng",
            "search_api" => "search_api",
            "api" => "search_api",
            "google_news" => "google_news",
            "ddg_html" => "ddg_html",
            "manual" => "manual",
            _ => "auto"
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record VoiceHostHealthResponse(
        string? Status,
        bool Ready,
        bool AsrReady,
        bool TtsReady,
        string? Version,
        string? ErrorCode,
        string? Message);
}
