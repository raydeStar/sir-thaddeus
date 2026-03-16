using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace SirThaddeus.UI.Avalonia.ViewModels;

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
    private string _toolSummary = string.Empty;
    private bool _isThoughtExpanded;
    private DateTimeOffset _timestamp = DateTimeOffset.Now;
    private bool _isPending;

    public event PropertyChangedEventHandler? PropertyChanged;

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
        "user" => "COMMAND",
        "assistant" => "RESULT",
        "tool" => "TOOL ACTIVITY",
        "status" => "STATUS",
        _ => ""
    };

    public string TimeDisplay => _timestamp.ToString(Use24HourTime ? "HH:mm" : "t");

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
