using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SirThaddeus.UI.Avalonia.ViewModels;

public sealed class WorkflowChecklistItemViewModel
{
    public string Id { get; init; } = string.Empty;
    public int Order { get; init; }
    public string State { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string StateIcon { get; init; } = "\u25CB";  // ○
    public string Title { get; init; } = string.Empty;
    public string StatusNote { get; init; } = string.Empty;
}

public sealed class ChatSourceCardItem : INotifyPropertyChanged
{
    private static readonly HttpClient ImageHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private Bitmap? _thumbnailImage;
    private Bitmap? _faviconImage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Excerpt { get; init; } = string.Empty;
    public string PublishedLabel { get; init; } = string.Empty;
    public string ThumbnailUrl { get; init; } = string.Empty;
    public string FaviconBase64 { get; init; } = string.Empty;

    public Bitmap? ThumbnailImage
    {
        get => _thumbnailImage;
        private set
        {
            _thumbnailImage = value;
            OnPropertyChanged(nameof(ThumbnailImage));
            OnPropertyChanged(nameof(HasThumbnailImage));
        }
    }

    public Bitmap? FaviconImage
    {
        get => _faviconImage;
        private set
        {
            _faviconImage = value;
            OnPropertyChanged(nameof(FaviconImage));
            OnPropertyChanged(nameof(HasFaviconImage));
        }
    }

    public bool HasThumbnailImage => _thumbnailImage is not null;
    public bool HasFaviconImage => _faviconImage is not null;
    public bool HasExcerpt => !string.IsNullOrWhiteSpace(Excerpt);
    public bool HasMetaLine => !string.IsNullOrWhiteSpace(MetaLine);
    public string DomainInitial => string.IsNullOrWhiteSpace(Domain)
        ? "•"
        : Domain.Trim()[0].ToString().ToUpperInvariant();

    public string MetaLine
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Domain))
            {
                return PublishedLabel;
            }

            if (string.IsNullOrWhiteSpace(PublishedLabel))
            {
                return Domain;
            }

            return Domain + " | " + PublishedLabel;
        }
    }

    public void BeginLoadImages()
    {
        _ = LoadImagesAsync();
    }

    private async Task LoadImagesAsync()
    {
        if (!string.IsNullOrWhiteSpace(FaviconBase64))
        {
            var favicon = TryDecodeBitmap(FaviconBase64);
            if (favicon is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => FaviconImage = favicon);
            }
        }

        if (string.IsNullOrWhiteSpace(ThumbnailUrl))
        {
            return;
        }

        var thumbnail = await TryDownloadBitmapAsync(ThumbnailUrl);
        if (thumbnail is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ThumbnailImage = thumbnail);
        }
    }

    private static Bitmap? TryDecodeBitmap(string encoded)
    {
        try
        {
            var normalized = encoded.Trim();
            var commaIndex = normalized.IndexOf(',');
            if (normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
            {
                normalized = normalized[(commaIndex + 1)..];
            }

            var bytes = Convert.FromBase64String(normalized);
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Bitmap?> TryDownloadBitmapAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                return null;
            }

            var bytes = await ImageHttpClient.GetByteArrayAsync(uri);
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ChatMessageItem : INotifyPropertyChanged
{
    /// <summary>
    /// When true, <see cref="TimeDisplay"/> uses 24-hour (HH:mm) format.
    /// Set once from app settings at startup and whenever the user toggles the preference.
    /// </summary>
    public static bool Use24HourTime { get; set; }

    /// <summary>
    /// The display name shown for user messages (e.g. preferred name from active profile).
    /// Defaults to "You" until a profile with a preferred name is loaded.
    /// </summary>
    public static string UserDisplayName { get; set; } = "You";

    private string _role = string.Empty;
    private string _content = string.Empty;
    private string _thoughtContent = string.Empty;
    private string _planContent = string.Empty;
    private string _toolSummary = string.Empty;
    private bool _isThoughtExpanded;
    private bool _isPlanExpanded;
    private DateTimeOffset _timestamp = DateTimeOffset.Now;
    private bool _isPending;

    public ChatMessageItem()
    {
        SourceCards.CollectionChanged += SourceCards_CollectionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ChatSourceCardItem> SourceCards { get; } = [];

    public string Role
    {
        get => _role;
        set
        {
            _role = value;
            OnPropertyChanged(nameof(Role));
            OnPropertyChanged(nameof(IsUser));
            OnPropertyChanged(nameof(IsAssistant));
            OnPropertyChanged(nameof(IsSystem));
            OnPropertyChanged(nameof(IsToolActivity));
            OnPropertyChanged(nameof(IsStatus));
            OnPropertyChanged(nameof(AuthorLabel));
            OnPropertyChanged(nameof(RoleLabel));
        }
    }

    public string Content
    {
        get => _content;
        set { _content = value; OnPropertyChanged(nameof(Content)); }
    }

    /// <summary>
    /// Model reasoning trace rendered in a collapsible expander.
    /// </summary>
    public string ThoughtContent
    {
        get => _thoughtContent;
        set
        {
            _thoughtContent = value;
            OnPropertyChanged(nameof(ThoughtContent));
            OnPropertyChanged(nameof(HasThoughtContent));
        }
    }

    /// <summary>True when this message has extracted thought text to display.</summary>
    public bool HasThoughtContent => !string.IsNullOrWhiteSpace(_thoughtContent);

    /// <summary>Expanded/collapsed state for the thought expander.</summary>
    public bool IsThoughtExpanded
    {
        get => _isThoughtExpanded;
        set { _isThoughtExpanded = value; OnPropertyChanged(nameof(IsThoughtExpanded)); }
    }

    /// <summary>
    /// Typed plan contract displayed in a collapsible expander above the response.
    /// </summary>
    public string PlanContent
    {
        get => _planContent;
        set
        {
            _planContent = value;
            OnPropertyChanged(nameof(PlanContent));
            OnPropertyChanged(nameof(HasPlanContent));
        }
    }

    /// <summary>True when this message has a plan to display.</summary>
    public bool HasPlanContent => !string.IsNullOrWhiteSpace(_planContent);

    /// <summary>Expanded/collapsed state for the plan expander.</summary>
    public bool IsPlanExpanded
    {
        get => _isPlanExpanded;
        set { _isPlanExpanded = value; OnPropertyChanged(nameof(IsPlanExpanded)); }
    }

    /// <summary>
    /// Compact tool-call summary shown as an inline footer on the message.
    /// </summary>
    public string ToolSummary
    {
        get => _toolSummary;
        set
        {
            _toolSummary = value;
            OnPropertyChanged(nameof(ToolSummary));
            OnPropertyChanged(nameof(HasToolSummary));
        }
    }

    /// <summary>True when this message has a tool summary footer to display.</summary>
    public bool HasToolSummary => !string.IsNullOrWhiteSpace(_toolSummary);

    public bool HasSourceCards => SourceCards.Count > 0;

    /// <summary>
    /// Original user prompt associated with this assistant response (for Retry).
    /// </summary>
    public string RetryPrompt { get; set; } = "";

    public bool IsPending
    {
        get => _isPending;
        set
        {
            if (_isPending == value)
            {
                return;
            }

            _isPending = value;
            OnPropertyChanged(nameof(IsPending));
        }
    }

    public void AppendContent(string delta)
    {
        _content += delta;
        OnPropertyChanged(nameof(Content));
    }

    public void SetSourceCards(System.Collections.Generic.IEnumerable<ChatSourceCardItem> cards)
    {
        SourceCards.Clear();
        foreach (var card in cards)
        {
            SourceCards.Add(card);
        }
    }

    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsSystem => Role == "system";
    public bool IsToolActivity => Role == "tool";
    public bool IsStatus => Role == "status";
    public string AuthorLabel => Role switch
    {
        "user" => UserDisplayName,
        "assistant" => "Sir Thaddeus",
        "tool" => "Tool Activity",
        "status" => "System",
        _ => "System"
    };

    /// <summary>Uppercase role tag for the message header.</summary>
    public string RoleLabel => Role switch
    {
        "user" => "",
        "assistant" => "",
        "tool" => "tool",
        "status" => "status",
        _ => ""
    };

    public string TimeDisplay => _timestamp.ToString(Use24HourTime ? "HH:mm" : "t");

    private void SourceCards_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSourceCards));
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ChatSessionItem : INotifyPropertyChanged
{
    private string _title;
    private DateTimeOffset _updatedAtUtc;
    public string ConversationId { get; } = $"chat-{Guid.NewGuid():N}";
    
    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    public ChatSessionItem(string title)
    {
        _title = title;
        _updatedAtUtc = DateTimeOffset.UtcNow;
    }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            OnPropertyChanged(nameof(Title));
        }
    }

    public string UpdatedLabel => _updatedAtUtc.LocalDateTime.ToString("g");

    public string Preview
    {
        get
        {
            var text = Messages.LastOrDefault()?.Content?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text)) return "No messages yet.";
            if (text.Length <= 96) return text;
            return text[..93] + "...";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AddMessage(string role, string text)
    {
        Messages.Add(new ChatMessageItem { Role = role, Content = text });
        MarkUpdated();
    }

    public void AddPendingAssistantMessage(string text = "Thinking...")
    {
        var last = Messages.LastOrDefault();
        if (last is not null && last.Role == "assistant" && last.IsPending)
        {
            last.Content = text;
        }
        else
        {
            Messages.Add(new ChatMessageItem
            {
                Role = "assistant",
                Content = text,
                IsPending = true
            });
        }

        MarkUpdated();
    }

    public void AppendToLastAssistantMessage(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        // Search backwards — system messages may have been inserted after the pending assistant msg.
        ChatMessageItem? lastAssistant = null;
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "assistant")
            {
                lastAssistant = Messages[i];
                break;
            }
        }

        if (lastAssistant != null)
        {
            if (lastAssistant.IsPending)
            {
                lastAssistant.Content = delta;
                lastAssistant.IsPending = false;
            }
            else
            {
                lastAssistant.AppendContent(delta);
            }
        }
        else
        {
            AddMessage("assistant", delta);
        }
        MarkUpdated();
    }

    public void ClearPendingAssistantMessage()
    {
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "assistant" && Messages[i].IsPending)
            {
                Messages.RemoveAt(i);
                MarkUpdated();
                return;
            }
        }
    }

    private void MarkUpdated()
    {
        _updatedAtUtc = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(Preview));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
