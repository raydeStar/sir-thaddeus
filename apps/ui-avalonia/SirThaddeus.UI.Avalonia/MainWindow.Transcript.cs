using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private void AppendTranscript(string line)
    {
        var wasEmpty = _currentSession.Messages.Count == 0;

        if (line.StartsWith("[user] "))
        {
            _currentSession.AddMessage("user", line[7..]);
        }
        else if (line.StartsWith("[assistant] "))
        {
            _currentSession.AppendToLastAssistantMessage(line[12..]);
        }
        else if (line.StartsWith("[voice] "))
        {
            _currentSession.AddMessage("user", line[8..]);
        }
        else if (line.StartsWith("[system] "))
        {
            _currentSession.AddMessage("system", line[9..]);
        }
        else if (line.StartsWith("[status] "))
        {
            _currentSession.AddMessage("status", line[9..]);
        }
        else if (line.StartsWith("[error] "))
        {
            _currentSession.AddMessage("system", line);
        }
        else
        {
            _currentSession.AddMessage("system", line);
        }

        if (wasEmpty && _currentSession.Messages.Count > 0)
        {
            UpdateLandingEmptyStateVisibility();
        }

        BumpSessionToTop(_currentSession);
        SyncLastMessageCacheFromCurrentSession();
        UpdateChatActionState();
        UpdateComposerState();
        UpdateConversationTitle();
        ScrollChatToBottom();
    }

    private void ScrollChatToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ChatScroller.Offset = new Vector(ChatScroller.Offset.X, double.MaxValue);
        }, DispatcherPriority.Background);
    }

    private IBrush ResolveThemeBrush(string key, IBrush fallback)
    {
        return (IBrush?)this.FindResource(key) ?? fallback;
    }
}
