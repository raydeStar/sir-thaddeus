using Avalonia.Controls;
using Avalonia.Interactivity;
using SirThaddeus.UI.Avalonia.ViewModels;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private void ConversationButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleConversationDrawer(!ConversationDrawer.IsVisible);
    }

    private void ToggleConversationDrawer(bool show)
    {
        ConversationDrawer.IsVisible = show;
        if (show)
        {
            ActionDrawer.IsVisible = false;
            ProgressDrawer.IsVisible = false;
        }
    }

    private void CloseConversationDrawerButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleConversationDrawer(false);
    }

    private void ChatHistoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is not ChatSessionItem session || ReferenceEquals(session, _currentSession))
        {
            return;
        }

        _currentSession = session;
        ChatMessagesList.ItemsSource = _currentSession.Messages;
        UpdateLandingEmptyStateVisibility();
        SyncLastMessageCacheFromCurrentSession();
        UpdateChatActionState();
        UpdateComposerState();

        UpdateConversationTitle();
        LoadBriefingForSession(session);
        SetActiveView(ChatTabButton);
        ToggleConversationDrawer(false);
    }

    private void ClearHistoryButton_Click(object? sender, RoutedEventArgs e)
    {
        _chatHistory.Clear();
        _briefingBySession.Clear();
        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Add(_currentSession);
        ChatHistoryList.SelectedItem = _currentSession;

        ChatMessagesList.ItemsSource = _currentSession.Messages;
        UpdateLandingEmptyStateVisibility();
        SyncLastMessageCacheFromCurrentSession();
        UpdateChatActionState();
        UpdateComposerState();

        UpdateConversationTitle();
        LoadBriefingForSession(_currentSession);
    }

    private void NewChatButton_Click(object? sender, RoutedEventArgs e)
    {
        StartNewChat();
    }

    private void StartNewChat()
    {
        _eventStreamCancellation?.Cancel();
        _eventStreamCancellation?.Dispose();
        _eventStreamCancellation = null;

        _activeRunId = null;
        _pendingPermissionRequestId = null;
        _assistantBuffersByRunId.Clear();
        _lastUserPrompt = null;
        _pendingUserPrompt = null;
        _lastAssistantMessage = null;
        _lastAssistantSources = Array.Empty<string>();

        _ = _runtimeApiClient?.ClearSessionAsync(CancellationToken.None);

        _currentSession = new ChatSessionItem("New Chat");
        _chatHistory.Insert(0, _currentSession);
        ChatHistoryList.SelectedItem = _currentSession;

        _activityDrawerVm.Clear();
        SessionSummaryText.Text = _activityDrawerVm.SessionSummaryText;
        SessionTimeRangeText.Text = string.Empty;

        ChatMessagesList.ItemsSource = _currentSession.Messages;
        UpdateLandingEmptyStateVisibility();

        PromptBox.Text = string.Empty;
        ResetPermissionRequestUi();

        _attachedDocument = null;
        UpdateAttachmentUi();
        SyncLastMessageCacheFromCurrentSession();
        UpdateChatActionState();
        UpdateComposerState();

        UpdateConversationTitle();
        LoadBriefingForSession(_currentSession);
        SetActiveView(ChatTabButton);
        ToggleConversationDrawer(false);
    }

    private void BumpSessionToTop(ChatSessionItem session)
    {
        var index = _chatHistory.IndexOf(session);
        if (index > 0)
        {
            _chatHistory.Move(index, 0);
            ChatHistoryList.SelectedItem = session;
        }
    }

    private string BuildSessionTitle(string prompt)
    {
        var trimmed = prompt.Trim();
        if (trimmed.Length <= 34)
        {
            return trimmed;
        }

        return trimmed[..31].TrimEnd() + "...";
    }

    private void UpdateConversationTitle()
    {
        ConversationTitleText.Text = string.Empty;
        ConversationTitleText.IsVisible = false;
    }
}