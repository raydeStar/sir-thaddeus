using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace SirThaddeus.UI.Avalonia.ViewModels;

public class ChatMessageItem : INotifyPropertyChanged
{
    private string _role = string.Empty;
    private string _content = string.Empty;
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
            OnPropertyChanged(nameof(AuthorLabel));
        }
    }

    public string Content
    {
        get => _content;
        set { _content = value; OnPropertyChanged(nameof(Content)); }
    }

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
    public string AuthorLabel => Role switch { "user" => "You", "assistant" => "Sir Thaddeus", _ => "System" };
    public string TimeDisplay => _timestamp.ToString("t");

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ChatSessionItem : INotifyPropertyChanged
{
    private string _title;
    private DateTimeOffset _updatedAtUtc;
    
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

        var last = Messages.LastOrDefault();
        if (last != null && last.Role == "assistant")
        {
            if (last.IsPending)
            {
                last.Content = delta;
                last.IsPending = false;
            }
            else
            {
                last.AppendContent(delta);
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
        var last = Messages.LastOrDefault();
        if (last is null || last.Role != "assistant" || !last.IsPending)
        {
            return;
        }

        Messages.Remove(last);
        MarkUpdated();
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
