using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;

namespace SirThaddeus.DesktopRuntime.ViewModels;

// ─────────────────────────────────────────────────────────────────────────
// Chat Message Display Model
//
// Each message in the conversation gets one of these. The Role drives
// XAML DataTriggers that flip alignment, colors, and sizing — no
// converters or template selectors needed.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Visual role classification for chat messages.
/// </summary>
public enum ChatMessageRole
{
    /// <summary>User-submitted text (right-aligned, accent bubble).</summary>
    User,

    /// <summary>LLM response (left-aligned, surface bubble).</summary>
    Assistant,

    /// <summary>Tool invocation summary (subtle, bordered).</summary>
    ToolActivity,

    /// <summary>System status / connection info (centered, no bubble).</summary>
    Status
}

/// <summary>
/// ViewModel for a single chat bubble in the conversation feed.
/// </summary>
public sealed class ChatMessageViewModel : ViewModelBase
{
    private string _content = string.Empty;
    private int    _carouselPage;

    /// <summary>How many source cards are visible per carousel page.</summary>
    private const int CardsPerPage = 2;

    public ChatMessageViewModel()
    {
        // Refresh carousel bindings whenever cards are added/removed
        SourceCards.CollectionChanged += OnSourceCardsChanged;

        PrevPageCommand = new RelayCommand(
            () => { if (CanGoLeft) CarouselPage--; });

        NextPageCommand = new RelayCommand(
            () => { if (CanGoRight) CarouselPage++; });
    }

    /// <summary>
    /// Determines visual treatment (alignment, color, size).
    /// </summary>
    public required ChatMessageRole Role { get; init; }

    /// <summary>
    /// The displayable text content.
    /// Mutable so we can update "Thinking..." placeholders in-place.
    /// </summary>
    public string Content
    {
        get => _content;
        set => SetProperty(ref _content, value);
    }

    public DateTime Timestamp { get; init; } = DateTime.Now;

    public string TimeDisplay => Timestamp.ToString("HH:mm");

    // ── Source Cards (populated for web search results) ───────────────

    /// <summary>
    /// Rich source cards extracted from web search tool results.
    /// Rendered as a horizontal card row below the message text.
    /// </summary>
    public ObservableCollection<SourceCardViewModel> SourceCards { get; } = [];

    /// <summary>
    /// True when this message has source cards to display.
    /// Drives conditional visibility in XAML.
    /// </summary>
    public bool HasSourceCards => SourceCards.Count > 0;

    // ── Carousel Paging ──────────────────────────────────────────────

    /// <summary>Current zero-based page index in the carousel.</summary>
    public int CarouselPage
    {
        get => _carouselPage;
        set
        {
            if (SetProperty(ref _carouselPage, value))
                RefreshCarouselBindings();
        }
    }

    /// <summary>
    /// The subset of source cards visible on the current carousel page.
    /// XAML binds its ItemsControl to this instead of the full collection.
    /// </summary>
    public IReadOnlyList<SourceCardViewModel> VisibleCards
        => [.. SourceCards.Skip(_carouselPage * CardsPerPage).Take(CardsPerPage)];

    /// <summary>Total number of pages (ceil(cards / 2)).</summary>
    public int CarouselPageCount
        => SourceCards.Count > 0
            ? (int)Math.Ceiling((double)SourceCards.Count / CardsPerPage)
            : 0;

    public bool CanGoLeft  => _carouselPage > 0;
    public bool CanGoRight => _carouselPage < CarouselPageCount - 1;

    /// <summary>Page indicator label, e.g. "1 / 3". Empty if only one page.</summary>
    public string CarouselPageLabel
        => CarouselPageCount > 1
            ? $"{_carouselPage + 1} / {CarouselPageCount}"
            : "";

    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }

    // ── XAML-friendly role checks (avoids enum converters) ───────────
    public bool IsUser         => Role == ChatMessageRole.User;
    public bool IsAssistant    => Role == ChatMessageRole.Assistant;
    public bool IsToolActivity => Role == ChatMessageRole.ToolActivity;
    public bool IsStatus       => Role == ChatMessageRole.Status;

    // ── Internals ────────────────────────────────────────────────────

    private void OnSourceCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _carouselPage = 0;
        OnPropertyChanged(nameof(HasSourceCards));
        RefreshCarouselBindings();
    }

    private void RefreshCarouselBindings()
    {
        OnPropertyChanged(nameof(VisibleCards));
        OnPropertyChanged(nameof(CarouselPageCount));
        OnPropertyChanged(nameof(CanGoLeft));
        OnPropertyChanged(nameof(CanGoRight));
        OnPropertyChanged(nameof(CarouselPageLabel));
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Source Card ViewModel
//
// Represents a single web search source rendered as a compact card.
// Includes favicon data (base64) with letter avatar fallback.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// ViewModel for a web search source card (thumbnail + title + summary).
/// </summary>
public sealed class SourceCardViewModel
{
    // ── Catppuccin Mocha palette for letter avatar backgrounds ────────
    private static readonly string[] AvatarColors =
    [
        "#CBA6F7",  // Mauve
        "#89B4FA",  // Blue
        "#94E2D5",  // Teal
        "#FAB387",  // Peach
        "#A6E3A1",  // Green
        "#F9E2AF"   // Yellow
    ];

    /// <summary>Page title.</summary>
    public required string Title { get; init; }

    /// <summary>Full URL of the source page (used for click-to-open).</summary>
    public required string Url { get; init; }

    /// <summary>Domain name (e.g. "techcrunch.com").</summary>
    public required string Domain { get; init; }

    /// <summary>Short excerpt / summary paragraph from the page.</summary>
    public string Excerpt { get; init; } = "";

    /// <summary>
    /// Base64-encoded favicon data URI (e.g. "data:image/png;base64,...").
    /// Empty if unavailable — UI shows letter avatar.
    /// </summary>
    public string FaviconBase64 { get; init; } = "";

    /// <summary>
    /// Article thumbnail URL (og:image / twitter:image).
    /// Loaded asynchronously by WPF — URL originates from audited MCP tool call.
    /// </summary>
    public string ThumbnailUrl { get; init; } = "";

    /// <summary>True when a favicon image is available.</summary>
    public bool HasFavicon => !string.IsNullOrEmpty(FaviconBase64);

    /// <summary>True when a thumbnail image URL is available.</summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    /// <summary>First letter of the domain, uppercased, for letter avatar.</summary>
    public string AvatarLetter { get; init; } = "?";

    /// <summary>Background color for the letter avatar circle (Catppuccin palette).</summary>
    public string AvatarColor { get; init; } = AvatarColors[0];

    /// <summary>
    /// Creates a source card from structured data with auto-assigned avatar.
    /// </summary>
    public static SourceCardViewModel Create(
        string title, string url, string domain, string excerpt,
        string? favicon, string? thumbnail, int index)
    {
        var letter = !string.IsNullOrEmpty(domain)
            ? domain[0].ToString().ToUpperInvariant()
            : "?";

        var color = AvatarColors[index % AvatarColors.Length];

        return new SourceCardViewModel
        {
            Title         = title.Length > 80 ? title[..77] + "..." : title,
            Url           = url,
            Domain        = domain,
            Excerpt       = excerpt,
            FaviconBase64 = favicon ?? "",
            ThumbnailUrl  = thumbnail ?? "",
            AvatarLetter  = letter,
            AvatarColor   = color
        };
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Activity Log Entry (Debug Split Pane)
//
// Lightweight entries for the right-side activity log. Color-coded by
// kind so tool calls, results, and errors are visually distinct.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Classification for activity log entries.
/// </summary>
public enum LogEntryKind
{
    Info,
    ToolInput,
    ToolOutput,
    Error
}

/// <summary>
/// A single entry in the activity/debug log pane.
/// </summary>
public sealed class LogEntry
{
    public required string    Text      { get; init; }
    public required LogEntryKind Kind   { get; init; }
    public DateTime           Timestamp { get; init; } = DateTime.Now;

    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    // ── XAML-friendly kind checks ────────────────────────────────────
    public bool IsInfo       => Kind == LogEntryKind.Info;
    public bool IsToolInput  => Kind == LogEntryKind.ToolInput;
    public bool IsToolOutput => Kind == LogEntryKind.ToolOutput;
    public bool IsError      => Kind == LogEntryKind.Error;
}
