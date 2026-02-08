using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SirThaddeus.AuditLog;
using SirThaddeus.Memory;

using Application = System.Windows.Application;

namespace SirThaddeus.DesktopRuntime.ViewModels;

// ─────────────────────────────────────────────────────────────────────────
// Memory Browser ViewModel
//
// Drives the CRUD panel inside the Command Palette window. Talks directly
// to the SqliteMemoryStore for reads/writes — this is user-initiated data
// management, not agent-driven, so it doesn't route through MCP.
//
// All mutations are audit-logged (MEMORY_EDITED / MEMORY_DELETED).
// ─────────────────────────────────────────────────────────────────────────

public sealed class MemoryBrowserViewModel : ViewModelBase
{
    // ─────────────────────────────────────────────────────────────────
    // Dependencies
    // ─────────────────────────────────────────────────────────────────

    private readonly IMemoryStore _store;
    private readonly IAuditLogger _audit;
    private readonly Dispatcher   _dispatcher;

    // ─────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────

    private const int PageSize = 50;

    // ─────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────

    private string _activeTab       = "Facts";
    private string _searchText      = "";
    private int    _currentPage     = 1;
    private int    _totalItems;
    private int    _totalPages      = 1;
    private bool   _isLoading;
    private string _statusText      = "Ready";
    private object? _selectedItem;

    // Debounce timer for search input
    private DispatcherTimer? _searchDebounce;

    // ─────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────

    public MemoryBrowserViewModel(IMemoryStore store, IAuditLogger audit)
    {
        _store      = store ?? throw new ArgumentNullException(nameof(store));
        _audit      = audit ?? throw new ArgumentNullException(nameof(audit));
        _dispatcher = Application.Current.Dispatcher;

        // ── Commands ─────────────────────────────────────────────────
        RefreshCommand    = new AsyncRelayCommand(RefreshAsync);
        NextPageCommand   = new RelayCommand(NextPage,   () => _currentPage < _totalPages);
        PrevPageCommand   = new RelayCommand(PrevPage,   () => _currentPage > 1);
        DeleteCommand     = new AsyncRelayCommand(DeleteSelectedAsync, () => _selectedItem is not null);
        SaveFactCommand   = new AsyncRelayCommand(SaveEditedFactAsync);
        SaveEventCommand  = new AsyncRelayCommand(SaveEditedEventAsync);
        SaveChunkCommand  = new AsyncRelayCommand(SaveEditedChunkAsync);

        AddNewCommand     = new AsyncRelayCommand(AddNewItemAsync);

        ShowFactsCommand  = new RelayCommand(() => SwitchTab("Facts"));
        ShowEventsCommand = new RelayCommand(() => SwitchTab("Events"));
        ShowChunksCommand = new RelayCommand(() => SwitchTab("Chunks"));
    }

    // ─────────────────────────────────────────────────────────────────
    // Bindable Properties
    // ─────────────────────────────────────────────────────────────────

    public ObservableCollection<MemoryFactRow>  Facts  { get; } = [];
    public ObservableCollection<MemoryEventRow> Events { get; } = [];
    public ObservableCollection<MemoryChunkRow> Chunks { get; } = [];

    public string ActiveTab
    {
        get => _activeTab;
        set { if (SetProperty(ref _activeTab, value)) OnPropertyChanged(nameof(IsFactsTab));
              OnPropertyChanged(nameof(IsEventsTab));
              OnPropertyChanged(nameof(IsChunksTab)); }
    }

    public bool IsFactsTab  => _activeTab == "Facts";
    public bool IsEventsTab => _activeTab == "Events";
    public bool IsChunksTab => _activeTab == "Chunks";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            // Debounce: wait 300ms after the user stops typing
            _searchDebounce?.Stop();
            _searchDebounce = new DispatcherTimer(
                TimeSpan.FromMilliseconds(300),
                DispatcherPriority.Background,
                async (_, _) =>
                {
                    _searchDebounce?.Stop();
                    _currentPage = 1;
                    OnPropertyChanged(nameof(CurrentPage));
                    await RefreshAsync();
                },
                _dispatcher);
            _searchDebounce.Start();
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public int TotalPages
    {
        get => _totalPages;
        private set => SetProperty(ref _totalPages, value);
    }

    public int TotalItems
    {
        get => _totalItems;
        private set => SetProperty(ref _totalItems, value);
    }

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

    public object? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public string PageDisplay => $"Page {_currentPage} of {_totalPages}";

    // ─────────────────────────────────────────────────────────────────
    // Commands
    // ─────────────────────────────────────────────────────────────────

    public ICommand RefreshCommand    { get; }
    public ICommand NextPageCommand   { get; }
    public ICommand PrevPageCommand   { get; }
    public ICommand DeleteCommand     { get; }
    public ICommand AddNewCommand     { get; }
    public ICommand SaveFactCommand   { get; }
    public ICommand SaveEventCommand  { get; }
    public ICommand SaveChunkCommand  { get; }
    public ICommand ShowFactsCommand  { get; }
    public ICommand ShowEventsCommand { get; }
    public ICommand ShowChunksCommand { get; }

    // ─────────────────────────────────────────────────────────────────
    // Tab Switching
    // ─────────────────────────────────────────────────────────────────

    private void SwitchTab(string tab)
    {
        ActiveTab    = tab;
        _currentPage = 1;
        OnPropertyChanged(nameof(CurrentPage));
        _ = RefreshAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Load / Refresh
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Initial load — called once when the browser becomes visible.
    /// </summary>
    public async Task LoadAsync()
    {
        await _store.EnsureSchemaAsync();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading  = true;
        StatusText = "Loading...";

        try
        {
            var filter = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText.Trim();
            var skip   = (_currentPage - 1) * PageSize;

            switch (_activeTab)
            {
                case "Facts":
                {
                    var (items, total) = await _store.ListFactsAsync(filter, skip, PageSize);
                    _dispatcher.Invoke(() =>
                    {
                        Facts.Clear();
                        foreach (var f in items)
                            Facts.Add(new MemoryFactRow(f));
                    });
                    TotalItems = total;
                    break;
                }
                case "Events":
                {
                    var (items, total) = await _store.ListEventsAsync(filter, skip, PageSize);
                    _dispatcher.Invoke(() =>
                    {
                        Events.Clear();
                        foreach (var e in items)
                            Events.Add(new MemoryEventRow(e));
                    });
                    TotalItems = total;
                    break;
                }
                case "Chunks":
                {
                    var (items, total) = await _store.ListChunksAsync(filter, skip, PageSize);
                    _dispatcher.Invoke(() =>
                    {
                        Chunks.Clear();
                        foreach (var c in items)
                            Chunks.Add(new MemoryChunkRow(c));
                    });
                    TotalItems = total;
                    break;
                }
            }

            TotalPages = Math.Max(1, (int)Math.Ceiling((double)_totalItems / PageSize));
            OnPropertyChanged(nameof(PageDisplay));
            StatusText = $"{_totalItems} {_activeTab.ToLowerInvariant()} total";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Pagination
    // ─────────────────────────────────────────────────────────────────

    private void NextPage()
    {
        if (_currentPage >= _totalPages) return;
        CurrentPage++;
        OnPropertyChanged(nameof(PageDisplay));
        _ = RefreshAsync();
    }

    private void PrevPage()
    {
        if (_currentPage <= 1) return;
        CurrentPage--;
        OnPropertyChanged(nameof(PageDisplay));
        _ = RefreshAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Delete
    // ─────────────────────────────────────────────────────────────────

    private async Task DeleteSelectedAsync()
    {
        switch (_selectedItem)
        {
            case MemoryFactRow fact:
                await _store.DeleteFactAsync(fact.MemoryId);
                _audit.Append(new AuditEvent
                {
                    Actor  = "user",
                    Action = "MEMORY_DELETED",
                    Target = "memory_browser",
                    Details = new() { ["item"] = $"fact:{fact.MemoryId}" }
                });
                break;

            case MemoryEventRow evt:
                await _store.DeleteEventAsync(evt.EventId);
                _audit.Append(new AuditEvent
                {
                    Actor  = "user",
                    Action = "MEMORY_DELETED",
                    Target = "memory_browser",
                    Details = new() { ["item"] = $"event:{evt.EventId}" }
                });
                break;

            case MemoryChunkRow chunk:
                await _store.DeleteChunkAsync(chunk.ChunkId);
                _audit.Append(new AuditEvent
                {
                    Actor  = "user",
                    Action = "MEMORY_DELETED",
                    Target = "memory_browser",
                    Details = new() { ["item"] = $"chunk:{chunk.ChunkId}" }
                });
                break;

            default:
                return;
        }

        SelectedItem = null;
        await RefreshAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Add New
    // ─────────────────────────────────────────────────────────────────

    private async Task AddNewItemAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var id  = Guid.NewGuid().ToString("N")[..12];

        switch (_activeTab)
        {
            case "Facts":
            {
                var fact = new MemoryFact
                {
                    MemoryId   = $"fact-{id}",
                    Subject    = "(edit me)",
                    Predicate  = "(edit me)",
                    Object     = "(edit me)",
                    Confidence = 1.0,
                    CreatedAt  = now,
                    UpdatedAt  = now
                };
                await _store.StoreFactAsync(fact);
                _audit.Append(new AuditEvent
                {
                    Actor   = "user",
                    Action  = "MEMORY_CREATED",
                    Target  = "memory_browser",
                    Details = new() { ["item"] = $"fact:{fact.MemoryId}" }
                });
                break;
            }
            case "Events":
            {
                var evt = new MemoryEvent
                {
                    EventId    = $"evt-{id}",
                    Type       = "(edit me)",
                    Title      = "(edit me)",
                    WhenIso    = now,
                    Confidence = 1.0
                };
                await _store.StoreEventAsync(evt);
                _audit.Append(new AuditEvent
                {
                    Actor   = "user",
                    Action  = "MEMORY_CREATED",
                    Target  = "memory_browser",
                    Details = new() { ["item"] = $"event:{evt.EventId}" }
                });
                break;
            }
            case "Chunks":
            {
                var chunk = new MemoryChunk
                {
                    ChunkId    = $"chunk-{id}",
                    SourceType = "manual",
                    Text       = "(edit me)",
                    WhenIso    = now
                };
                await _store.StoreChunkAsync(chunk);
                _audit.Append(new AuditEvent
                {
                    Actor   = "user",
                    Action  = "MEMORY_CREATED",
                    Target  = "memory_browser",
                    Details = new() { ["item"] = $"chunk:{chunk.ChunkId}" }
                });
                break;
            }
        }

        await RefreshAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Save (edit commit)
    // ─────────────────────────────────────────────────────────────────

    private async Task SaveEditedFactAsync()
    {
        if (_selectedItem is not MemoryFactRow row) return;

        var updated = new MemoryFact
        {
            MemoryId    = row.MemoryId,
            ProfileId   = row.ProfileId,
            Subject     = row.Subject,
            Predicate   = row.Predicate,
            Object      = row.Object,
            Confidence  = row.Confidence,
            Sensitivity = row.Sensitivity,
            CreatedAt   = row.CreatedAt,
            UpdatedAt   = DateTimeOffset.UtcNow,
            SourceRef   = row.SourceRef
        };

        await _store.StoreFactAsync(updated);

        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "MEMORY_EDITED",
            Target = "memory_browser",
            Details = new() { ["item"] = $"fact:{row.MemoryId}", ["summary"] = $"{row.Subject} {row.Predicate} {row.Object}" }
        });

        await RefreshAsync();
    }

    private async Task SaveEditedEventAsync()
    {
        if (_selectedItem is not MemoryEventRow row) return;

        var updated = new MemoryEvent
        {
            EventId     = row.EventId,
            ProfileId   = row.ProfileId,
            Type        = row.Type,
            Title       = row.Title,
            Summary     = row.Summary,
            WhenIso     = row.WhenIso,
            Confidence  = row.Confidence,
            Sensitivity = row.Sensitivity,
            SourceRef   = row.SourceRef
        };

        await _store.StoreEventAsync(updated);

        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "MEMORY_EDITED",
            Target = "memory_browser",
            Details = new() { ["item"] = $"event:{row.EventId}", ["summary"] = row.Title }
        });

        await RefreshAsync();
    }

    private async Task SaveEditedChunkAsync()
    {
        if (_selectedItem is not MemoryChunkRow row) return;

        var updated = new MemoryChunk
        {
            ChunkId     = row.ChunkId,
            SourceType  = row.SourceType,
            SourceRef   = row.SourceRef,
            Text        = row.Text,
            WhenIso     = row.WhenIso,
            Sensitivity = row.Sensitivity
        };

        await _store.StoreChunkAsync(updated);

        _audit.Append(new AuditEvent
        {
            Actor  = "user",
            Action = "MEMORY_EDITED",
            Target = "memory_browser",
            Details = new() { ["item"] = $"chunk:{row.ChunkId}" }
        });

        await RefreshAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Row Models (mutable wrappers for DataGrid binding)
//
// DataGrid needs mutable properties for inline editing. These wrap the
// immutable domain records and expose get/set for each editable field.
// ─────────────────────────────────────────────────────────────────────────

public sealed class MemoryFactRow : ViewModelBase
{
    public MemoryFactRow(MemoryFact fact)
    {
        MemoryId    = fact.MemoryId;
        ProfileId   = fact.ProfileId;
        Subject     = fact.Subject;
        Predicate   = fact.Predicate;
        Object      = fact.Object;
        Confidence  = fact.Confidence;
        Sensitivity = fact.Sensitivity;
        CreatedAt   = fact.CreatedAt;
        UpdatedAt   = fact.UpdatedAt;
        SourceRef   = fact.SourceRef;
    }

    public string         MemoryId    { get; }
    public string?        ProfileId   { get; set; }
    public string         Subject     { get; set; }
    public string         Predicate   { get; set; }
    public string         Object      { get; set; }
    public double         Confidence  { get; set; }
    public Sensitivity    Sensitivity { get; set; }
    public DateTimeOffset CreatedAt   { get; }
    public DateTimeOffset UpdatedAt   { get; }
    public string?        SourceRef   { get; set; }
}

public sealed class MemoryEventRow : ViewModelBase
{
    public MemoryEventRow(MemoryEvent evt)
    {
        EventId     = evt.EventId;
        ProfileId   = evt.ProfileId;
        Type        = evt.Type;
        Title       = evt.Title;
        Summary     = evt.Summary;
        WhenIso     = evt.WhenIso;
        Confidence  = evt.Confidence;
        Sensitivity = evt.Sensitivity;
        SourceRef   = evt.SourceRef;
    }

    public string          EventId     { get; }
    public string?         ProfileId   { get; set; }
    public string          Type        { get; set; }
    public string          Title       { get; set; }
    public string?         Summary     { get; set; }
    public DateTimeOffset? WhenIso     { get; }
    public double          Confidence  { get; set; }
    public Sensitivity     Sensitivity { get; set; }
    public string?         SourceRef   { get; set; }
}

public sealed class MemoryChunkRow : ViewModelBase
{
    public MemoryChunkRow(MemoryChunk chunk)
    {
        ChunkId    = chunk.ChunkId;
        SourceType = chunk.SourceType;
        SourceRef  = chunk.SourceRef;
        Text       = chunk.Text;
        WhenIso    = chunk.WhenIso;
        Sensitivity = chunk.Sensitivity;
    }

    public string          ChunkId     { get; }
    public string          SourceType  { get; set; }
    public string?         SourceRef   { get; set; }
    public string          Text        { get; set; }
    public DateTimeOffset? WhenIso     { get; }
    public Sensitivity     Sensitivity { get; set; }
}
