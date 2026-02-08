using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using SirThaddeus.AuditLog;
using SirThaddeus.Memory;

using Application = System.Windows.Application;

namespace SirThaddeus.DesktopRuntime.ViewModels;

// ─────────────────────────────────────────────────────────────────────────
// Profile & Nuggets Browser ViewModel
//
// Drives the "Profile" tab inside the Command Palette window.
// Two panes:
//   1. Profile Cards — user's own card + cards for other people
//   2. Memory Nuggets — short, atomic personal facts
//
// All mutations are audit-logged (PROFILE_*, NUGGET_*).
// User-initiated — does not route through MCP (invariant I5).
// ─────────────────────────────────────────────────────────────────────────

public sealed class ProfileBrowserViewModel : ViewModelBase
{
    private readonly IMemoryStore _store;
    private readonly IAuditLogger _audit;
    private readonly Dispatcher   _dispatcher;

    private const int NuggetPageSize = 50;

    // ── State ────────────────────────────────────────────────────────
    private string  _activePane      = "Profiles";   // Profiles | Nuggets
    private bool    _isLoading;
    private string  _statusText      = "Ready";

    // Profile
    private ProfileCardRow? _selectedProfile;

    // Nuggets
    private string  _nuggetSearchText = "";
    private int     _nuggetPage       = 1;
    private int     _nuggetTotalPages = 1;
    private int     _nuggetTotalItems;
    private NuggetRow? _selectedNugget;

    private DispatcherTimer? _searchDebounce;

    // ── Constructor ──────────────────────────────────────────────────

    public ProfileBrowserViewModel(IMemoryStore store, IAuditLogger audit)
    {
        _store      = store ?? throw new ArgumentNullException(nameof(store));
        _audit      = audit ?? throw new ArgumentNullException(nameof(audit));
        _dispatcher = Application.Current.Dispatcher;

        ShowProfilesCommand    = new RelayCommand(() => SwitchPane("Profiles"));
        ShowNuggetsCommand     = new RelayCommand(() => SwitchPane("Nuggets"));
        RefreshCommand         = new AsyncRelayCommand(RefreshAsync);

        // Profile commands
        AddProfileCommand      = new AsyncRelayCommand(AddProfileAsync);
        DeleteProfileCommand   = new AsyncRelayCommand(DeleteProfileAsync, () => _selectedProfile is not null);
        SaveProfileCommand     = new AsyncRelayCommand(SaveProfileAsync, () => _selectedProfile is not null);

        // Nugget commands
        AddNuggetCommand       = new AsyncRelayCommand(AddNuggetAsync);
        DeleteNuggetCommand    = new AsyncRelayCommand(DeleteNuggetAsync, () => _selectedNugget is not null);
        SaveNuggetCommand      = new AsyncRelayCommand(SaveNuggetAsync, () => _selectedNugget is not null);
        NuggetNextPageCommand  = new RelayCommand(NuggetNextPage, () => _nuggetPage < _nuggetTotalPages);
        NuggetPrevPageCommand  = new RelayCommand(NuggetPrevPage, () => _nuggetPage > 1);
    }

    // ── Bindable Properties ─────────────────────────────────────────

    public ObservableCollection<ProfileCardRow> Profiles { get; } = [];
    public ObservableCollection<NuggetRow>      Nuggets  { get; } = [];

    public string ActivePane
    {
        get => _activePane;
        set { if (SetProperty(ref _activePane, value))
              { OnPropertyChanged(nameof(IsProfilesPane));
                OnPropertyChanged(nameof(IsNuggetsPane)); } }
    }

    public bool IsProfilesPane => _activePane == "Profiles";
    public bool IsNuggetsPane  => _activePane == "Nuggets";

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // Profile selection
    public ProfileCardRow? SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    // Nugget selection + search + pagination
    public NuggetRow? SelectedNugget
    {
        get => _selectedNugget;
        set => SetProperty(ref _selectedNugget, value);
    }

    public string NuggetSearchText
    {
        get => _nuggetSearchText;
        set
        {
            if (!SetProperty(ref _nuggetSearchText, value)) return;
            _searchDebounce?.Stop();
            _searchDebounce = new DispatcherTimer(
                TimeSpan.FromMilliseconds(300),
                DispatcherPriority.Background,
                async (_, _) =>
                {
                    _searchDebounce?.Stop();
                    _nuggetPage = 1;
                    OnPropertyChanged(nameof(NuggetPage));
                    await RefreshNuggetsAsync();
                },
                _dispatcher);
            _searchDebounce.Start();
        }
    }

    public int NuggetPage
    {
        get => _nuggetPage;
        private set => SetProperty(ref _nuggetPage, value);
    }

    public string NuggetPageDisplay => $"Page {_nuggetPage} of {_nuggetTotalPages}";

    // ── Commands ─────────────────────────────────────────────────────

    public ICommand ShowProfilesCommand   { get; }
    public ICommand ShowNuggetsCommand    { get; }
    public ICommand RefreshCommand        { get; }

    public ICommand AddProfileCommand     { get; }
    public ICommand DeleteProfileCommand  { get; }
    public ICommand SaveProfileCommand    { get; }

    public ICommand AddNuggetCommand      { get; }
    public ICommand DeleteNuggetCommand   { get; }
    public ICommand SaveNuggetCommand     { get; }
    public ICommand NuggetNextPageCommand { get; }
    public ICommand NuggetPrevPageCommand { get; }

    // ── Pane Switching ──────────────────────────────────────────────

    private void SwitchPane(string pane)
    {
        ActivePane = pane;
        _ = RefreshAsync();
    }

    // ── Load / Refresh ──────────────────────────────────────────────

    public async Task LoadAsync()
    {
        await _store.EnsureSchemaAsync();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_activePane == "Profiles")
            await RefreshProfilesAsync();
        else
            await RefreshNuggetsAsync();
    }

    private async Task RefreshProfilesAsync()
    {
        IsLoading  = true;
        StatusText = "Loading profiles...";

        try
        {
            var profiles = await _store.ListProfilesAsync();
            _dispatcher.Invoke(() =>
            {
                Profiles.Clear();
                foreach (var p in profiles)
                    Profiles.Add(new ProfileCardRow(p));
            });

            StatusText = $"{profiles.Count} profile(s)";
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private async Task RefreshNuggetsAsync()
    {
        IsLoading  = true;
        StatusText = "Loading nuggets...";

        try
        {
            var filter = string.IsNullOrWhiteSpace(_nuggetSearchText) ? null : _nuggetSearchText.Trim();
            var skip   = (_nuggetPage - 1) * NuggetPageSize;

            var (items, total) = await _store.ListNuggetsAsync(filter, skip, NuggetPageSize);
            _dispatcher.Invoke(() =>
            {
                Nuggets.Clear();
                foreach (var n in items)
                    Nuggets.Add(new NuggetRow(n));
            });

            _nuggetTotalItems = total;
            _nuggetTotalPages = Math.Max(1, (int)Math.Ceiling((double)total / NuggetPageSize));
            OnPropertyChanged(nameof(NuggetPageDisplay));
            StatusText = $"{total} nugget(s) total";
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    // ── Profile CRUD ────────────────────────────────────────────────

    private async Task AddProfileAsync()
    {
        var id  = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;

        // If no user profile exists, the first one is "user"; otherwise "person"
        var kind = Profiles.All(p => p.Kind != "user") ? "user" : "person";

        var card = new ProfileCard
        {
            ProfileId   = $"prof-{id}",
            Kind        = kind,
            DisplayName = kind == "user" ? "(Your Name)" : "(Person Name)",
            Relationship = kind == "person" ? "(relationship)" : null,
            ProfileJson = kind == "user"
                ? JsonSerializer.Serialize(new
                {
                    preferred_name = "",
                    pronouns       = "",
                    timezone       = "",
                    style          = ""
                })
                : JsonSerializer.Serialize(new
                {
                    highlight = "",
                    notes     = ""
                }),
            UpdatedAt   = now
        };

        await _store.StoreProfileAsync(card);
        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "PROFILE_CREATED",
            Target = "profile_browser",
            Details = new() { ["id"] = card.ProfileId, ["kind"] = kind }
        });

        await RefreshProfilesAsync();
    }

    private async Task DeleteProfileAsync()
    {
        if (_selectedProfile is null) return;
        await _store.DeleteProfileAsync(_selectedProfile.ProfileId);
        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "PROFILE_DELETED",
            Target = "profile_browser",
            Details = new() { ["id"] = _selectedProfile.ProfileId }
        });
        SelectedProfile = null;
        await RefreshProfilesAsync();
    }

    private async Task SaveProfileAsync()
    {
        if (_selectedProfile is null) return;

        var updated = new ProfileCard
        {
            ProfileId    = _selectedProfile.ProfileId,
            Kind         = _selectedProfile.Kind,
            DisplayName  = _selectedProfile.DisplayName,
            Relationship = _selectedProfile.Relationship,
            Aliases      = _selectedProfile.Aliases,
            ProfileJson  = _selectedProfile.ProfileJson,
            UpdatedAt    = DateTimeOffset.UtcNow
        };

        await _store.StoreProfileAsync(updated);
        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "PROFILE_EDITED",
            Target = "profile_browser",
            Details = new()
            {
                ["id"]   = updated.ProfileId,
                ["name"] = updated.DisplayName
            }
        });

        await RefreshProfilesAsync();
    }

    // ── Nugget CRUD ─────────────────────────────────────────────────

    private async Task AddNuggetAsync()
    {
        var id  = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;

        var nugget = new MemoryNugget
        {
            NuggetId    = $"nug-{id}",
            Text        = "(edit me)",
            Tags        = ";preference;",
            Weight      = 0.65,
            PinLevel    = 0,
            Sensitivity = NuggetSensitivity.Low,
            CreatedAt   = now
        };

        await _store.StoreNuggetAsync(nugget);
        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "NUGGET_CREATED",
            Target = "profile_browser",
            Details = new() { ["id"] = nugget.NuggetId }
        });

        await RefreshNuggetsAsync();
    }

    private async Task DeleteNuggetAsync()
    {
        if (_selectedNugget is null) return;
        await _store.DeleteNuggetAsync(_selectedNugget.NuggetId);
        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "NUGGET_DELETED",
            Target = "profile_browser",
            Details = new() { ["id"] = _selectedNugget.NuggetId }
        });
        SelectedNugget = null;
        await RefreshNuggetsAsync();
    }

    private async Task SaveNuggetAsync()
    {
        if (_selectedNugget is null) return;

        var updated = new MemoryNugget
        {
            NuggetId    = _selectedNugget.NuggetId,
            Text        = _selectedNugget.Text,
            Tags        = _selectedNugget.Tags,
            Weight      = _selectedNugget.Weight,
            PinLevel    = _selectedNugget.PinLevel,
            Sensitivity = _selectedNugget.Sensitivity,
            UseCount    = _selectedNugget.UseCount,
            LastUsedAt  = _selectedNugget.LastUsedAt,
            CreatedAt   = _selectedNugget.CreatedAt
        };

        await _store.StoreNuggetAsync(updated);
        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "NUGGET_EDITED",
            Target = "profile_browser",
            Details = new()
            {
                ["id"]   = updated.NuggetId,
                ["text"] = updated.Text.Length > 60 ? updated.Text[..60] + "…" : updated.Text
            }
        });

        await RefreshNuggetsAsync();
    }

    // ── Nugget Pagination ───────────────────────────────────────────

    private void NuggetNextPage()
    {
        if (_nuggetPage >= _nuggetTotalPages) return;
        NuggetPage++;
        OnPropertyChanged(nameof(NuggetPageDisplay));
        _ = RefreshNuggetsAsync();
    }

    private void NuggetPrevPage()
    {
        if (_nuggetPage <= 1) return;
        NuggetPage--;
        OnPropertyChanged(nameof(NuggetPageDisplay));
        _ = RefreshNuggetsAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Row Models (mutable wrappers for DataGrid / form binding)
// ─────────────────────────────────────────────────────────────────────────

public sealed class ProfileCardRow : ViewModelBase
{
    public ProfileCardRow(ProfileCard card)
    {
        ProfileId    = card.ProfileId;
        Kind         = card.Kind;
        DisplayName  = card.DisplayName;
        Relationship = card.Relationship;
        Aliases      = card.Aliases;
        ProfileJson  = card.ProfileJson;
        UpdatedAt    = card.UpdatedAt;
    }

    public string         ProfileId    { get; }
    public string         Kind         { get; set; }
    public string         DisplayName  { get; set; }
    public string?        Relationship { get; set; }
    public string?        Aliases      { get; set; }
    public string         ProfileJson  { get; set; }
    public DateTimeOffset UpdatedAt    { get; }
}

public sealed class NuggetRow : ViewModelBase
{
    public NuggetRow(MemoryNugget nugget)
    {
        NuggetId    = nugget.NuggetId;
        Text        = nugget.Text;
        Tags        = nugget.Tags;
        Weight      = nugget.Weight;
        PinLevel    = nugget.PinLevel;
        Sensitivity = nugget.Sensitivity;
        UseCount    = nugget.UseCount;
        LastUsedAt  = nugget.LastUsedAt;
        CreatedAt   = nugget.CreatedAt;
    }

    public string          NuggetId    { get; }
    public string          Text        { get; set; }
    public string?         Tags        { get; set; }
    public double          Weight      { get; set; }
    public int             PinLevel    { get; set; }
    public string          Sensitivity { get; set; }
    public int             UseCount    { get; }
    public DateTimeOffset? LastUsedAt  { get; }
    public DateTimeOffset  CreatedAt   { get; }
}
