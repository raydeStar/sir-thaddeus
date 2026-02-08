using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Memory;
using SirThaddeus.Memory.Sqlite;

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

    // Memory
    private bool   _memoryEnabled     = true;
    private bool   _embeddingsEnabled = true;

    // Profile
    private ProfileOption? _selectedProfile;
    private string         _statusText = "";

    // ─── Public Properties ───────────────────────────────────────────

    // LLM
    public string LlmBaseUrl     { get => _llmBaseUrl;     set { if (SetProperty(ref _llmBaseUrl, value))     MarkDirty(); } }
    public string LlmModel       { get => _llmModel;       set { if (SetProperty(ref _llmModel, value))       MarkDirty(); } }
    public int    LlmMaxTokens   { get => _llmMaxTokens;   set { if (SetProperty(ref _llmMaxTokens, value))   MarkDirty(); } }
    public double LlmTemperature { get => _llmTemperature; set { if (SetProperty(ref _llmTemperature, value)) MarkDirty(); } }

    // Audio
    public bool   TtsEnabled     { get => _ttsEnabled;     set { if (SetProperty(ref _ttsEnabled, value))     MarkDirty(); } }
    public string PttKey         { get => _pttKey;          set { if (SetProperty(ref _pttKey, value))         MarkDirty(); } }

    // Memory
    public bool   MemoryEnabled     { get => _memoryEnabled;     set { if (SetProperty(ref _memoryEnabled, value))     MarkDirty(); } }
    public bool   EmbeddingsEnabled { get => _embeddingsEnabled; set { if (SetProperty(ref _embeddingsEnabled, value)) MarkDirty(); } }

    // Profile dropdown
    public ObservableCollection<ProfileOption> AvailableProfiles { get; } = new();

    public ProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                MarkDirty();
                SaveSettings();
            }
        }
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    // ─── Commands ────────────────────────────────────────────────────

    public ICommand SaveCommand    { get; }
    public ICommand RefreshCommand { get; }

    // ─── Raised when the active profile changes ──────────────────────

    /// <summary>
    /// Raised when the active profile selection changes so the host
    /// can propagate the choice to the agent and MCP layers.
    /// </summary>
    public event Action<string?>? ActiveProfileChanged;

    // ─── Constructor ─────────────────────────────────────────────────

    public SettingsViewModel(AppSettings settings, SqliteMemoryStore? store, IAuditLogger audit)
    {
        _settings = settings;
        _store    = store;
        _audit    = audit;

        SaveCommand    = new RelayCommand(_ => SaveSettings());
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync());

        LoadFromSettings(settings);
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
        await LoadProfilesAsync();
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

        _memoryEnabled     = s.Memory.Enabled;
        _embeddingsEnabled = s.Memory.UseEmbeddings;

        // Notify all bindings
        OnPropertyChanged(nameof(LlmBaseUrl));
        OnPropertyChanged(nameof(LlmModel));
        OnPropertyChanged(nameof(LlmMaxTokens));
        OnPropertyChanged(nameof(LlmTemperature));
        OnPropertyChanged(nameof(TtsEnabled));
        OnPropertyChanged(nameof(PttKey));
        OnPropertyChanged(nameof(MemoryEnabled));
        OnPropertyChanged(nameof(EmbeddingsEnabled));
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
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load profiles: {ex.Message}";
        }
    }

    // ─── Persistence ─────────────────────────────────────────────────

    private void MarkDirty()
    {
        // Future: could debounce auto-save here
    }

    private void SaveSettings()
    {
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
                TtsEnabled = _ttsEnabled,
                PttKey     = _pttKey
            },
            Memory = _settings.Memory with
            {
                Enabled       = _memoryEnabled,
                UseEmbeddings = _embeddingsEnabled
            },
            ActiveProfileId = _selectedProfile?.ProfileId
        };

        SettingsManager.Save(updated);
        _settings = updated;

        StatusText = "Settings saved.";

        ActiveProfileChanged?.Invoke(_selectedProfile?.ProfileId);

        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "SETTINGS_SAVED",
            Result = _selectedProfile?.ProfileId is not null
                ? $"activeProfile={_selectedProfile.ProfileId}"
                : "activeProfile=none"
        });
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
