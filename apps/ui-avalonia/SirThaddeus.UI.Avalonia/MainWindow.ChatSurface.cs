using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FluentIcons.Common;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private void PromptBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateComposerState();
    }

    private void PromptBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!ShouldSendPromptOnKeyDown(e))
        {
            return;
        }

        e.Handled = true;
        SendButton_Click(sender, new RoutedEventArgs());
    }

    private void SuggestionChipButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_submitInProgress || sender is not Button { DataContext: SuggestionChipItem chip })
        {
            return;
        }

        if (chip.ActionKind == SuggestionActionKind.OpenAuditTrail)
        {
            SetActiveView(SettingsTabButton);
            SettingsTabControl.SelectedItem = AuditTabItem;
            _ = RefreshAuditAsync();
            AddRecentActivity(Symbol.History, "Audit trail opened", "Switched to the audit tab for inspection.", "Opened", "Audit: read-only records");
            return;
        }

        PromptBox.Text = chip.PromptText;
        PromptBox.CaretIndex = PromptBox.Text.Length;
        PromptBox.Focus();
        AddRecentActivity(chip.IconSymbol, chip.Label, "Command prepared in the composer.", "Prepared", "Awaiting explicit approval for external actions", chip.PromptText);
        UpdateComposerState();
    }

    private bool ShouldSendPromptOnKeyDown(KeyEventArgs e)
    {
        if (!_uiSettings.SendOnEnter)
        {
            return false;
        }

        if (e.Key is not (Key.Enter or Key.Return))
        {
            return false;
        }

        return !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
               && !e.KeyModifiers.HasFlag(KeyModifiers.Control)
               && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
    }

    private void UpdateComposerState()
    {
        var hasPrompt = !string.IsNullOrWhiteSpace(PromptBox.Text);
        var runActive = !string.IsNullOrWhiteSpace(_activeRunId);
        var stopAllActive = runActive || IsVoiceResponseActive() || _voiceStatusLabel is "Listening" or "Responding" or "Working";
        SendButton.IsEnabled = hasPrompt && !runActive && !_submitInProgress;
        SendButton.IsVisible = !runActive;
        SendButton.Opacity = hasPrompt ? 1.0 : 0.92;
        SendButton.Background = hasPrompt
            ? (IBrush?)this.FindResource("AccentPrimary") ?? Brushes.DodgerBlue
            : (IBrush?)this.FindResource("BackgroundTertiary") ?? Brushes.DimGray;
        SendButton.Foreground = hasPrompt
            ? Brushes.White
            : (IBrush?)this.FindResource("TextSecondary") ?? Brushes.Gray;
        StopButton.IsEnabled = runActive;
        StopButton.IsVisible = runActive;
        StopAllButton.Opacity = stopAllActive ? 1.0 : 0.38;
        UpdateRuntimeStatusStrip();
    }

    private void UpdateChatActionState()
    {
        // Actions are now per-message via triple-dot flyouts.
    }

    private void SyncLastMessageCacheFromCurrentSession()
    {
        _lastUserPrompt = _currentSession.Messages.LastOrDefault(m => m.Role == "user")?.Content;
        var lastAssistant = _currentSession.Messages.LastOrDefault(m => m.Role == "assistant" && !m.IsPending);
        _lastAssistantMessage = lastAssistant?.Content;
        _lastAssistantSources = lastAssistant is not null && lastAssistant.SourceCards.Count > 0
            ? lastAssistant.SourceCards.Select(card => card.Url).ToArray()
            : string.IsNullOrWhiteSpace(_lastAssistantMessage)
                ? Array.Empty<string>()
                : ExtractUrls(_lastAssistantMessage);
    }

    private void UpdateHeaderConnectionControls()
    {
        ConnectButton.IsVisible = false;
        ConnectButton.IsEnabled = !_isConnecting;

        var connected = _runtimeApiClient is not null;
        ConnectionStatusDot.Fill = connected
            ? (IBrush?)this.FindResource("Success") ?? Brushes.LightGreen
            : (IBrush?)this.FindResource("TextTertiary") ?? Brushes.Gray;

        ToolTip.SetTip(ConnectionStatusButton, connected ? "Connected" : "Disconnected");
        UpdateRuntimeStatusStrip();
    }

    private void UpdateLandingEmptyStateVisibility()
    {
        var showEmptyState = _currentSession.Messages.Count == 0;
        var activeConversation = !showEmptyState && ChatTabButton.IsChecked == true;
        ChatScroller.VerticalScrollBarVisibility = showEmptyState ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        HomeCommandStage.IsVisible = showEmptyState;
        RuntimeStatusStrip.IsVisible = showEmptyState;
        EmptyHero.IsVisible = showEmptyState;
        SuggestionChipsPanel.IsVisible = showEmptyState;
        ChatMessagesList.IsVisible = !showEmptyState;
        ChatSurfaceLayout.MaxWidth = showEmptyState ? 860 : 1120;
        ChatSurfaceLayout.Margin = showEmptyState ? new Thickness(24, 0, 24, 0) : new Thickness(32, 18, 32, 0);
        HomeCommandStage.Margin = showEmptyState ? new Thickness(0, -24, 0, 40) : new Thickness(0);
        ChatMessagesList.Margin = showEmptyState ? new Thickness(0, 20, 0, 36) : new Thickness(0, 24, 0, 30);
        InputBarLayout.MaxWidth = showEmptyState ? 760 : 1120;
        InputBar.Padding = showEmptyState ? new Thickness(24, 8, 24, 20) : new Thickness(28, 12, 28, 16);
        ConnectionStatusText.IsVisible = activeConversation;
        ConversationTitleText.IsVisible = false;
        ChatComposer.SetLayoutMode(activeConversation: !showEmptyState);
    }

    private void InitializeSuggestionChips()
    {
        _suggestionChips.Clear();
        _suggestionChips.Add(new SuggestionChipItem(Symbol.Screenshot, "Summarize this screen", "Summarize the current screen and call out what matters.", SuggestionActionKind.FillPrompt));
        _suggestionChips.Add(new SuggestionChipItem(Symbol.SearchInfo, "Inspect current page", "Inspect the current page and tell me what stands out.", SuggestionActionKind.FillPrompt));
        _suggestionChips.Add(new SuggestionChipItem(Symbol.FolderOpen, "Review file or folder", "Review this file or folder and call out the important findings.", SuggestionActionKind.FillPrompt));
    }

    private void UpdateRuntimeStatusStrip()
    {
        if (RuntimeStatusStrip is null)
        {
            return;
        }

        var runtimeValue = _isConnecting
            ? "Starting"
            : !string.IsNullOrWhiteSpace(_activeRunId)
                ? "Busy"
                : _runtimeApiClient is not null
                    ? "Ready"
                    : "Offline";

        var runtimeBrush = runtimeValue switch
        {
            "Ready" => ResolveThemeBrush("Success", Brushes.LightGreen),
            "Busy" => ResolveThemeBrush("AccentPrimary", Brushes.DodgerBlue),
            "Starting" => ResolveThemeBrush("AccentPrimary", Brushes.DodgerBlue),
            _ => ResolveThemeBrush("TextTertiary", Brushes.Gray)
        };

        var modelConnected = _runtimeApiClient is not null;

        _runtimeStatusItems.Clear();
        _runtimeStatusItems.Add(new RuntimeStatusItem(Symbol.WindowShield, "Runtime", runtimeValue, runtimeBrush));
        _runtimeStatusItems.Add(new RuntimeStatusItem(Symbol.Shield, "Permissions", "Explicit approval", ResolveThemeBrush("TextSecondary", Brushes.LightGray)));
        _runtimeStatusItems.Add(new RuntimeStatusItem(Symbol.History, "Audit", "Active", ResolveThemeBrush("TextSecondary", Brushes.LightGray)));

        var runtimeStamp = $"{runtimeValue}|{(modelConnected ? "connected" : "offline")}|{_voiceStatusLabel}";
        if (!string.Equals(_lastRuntimeActivityStamp, runtimeStamp, StringComparison.Ordinal))
        {
            _lastRuntimeActivityStamp = runtimeStamp;
        }
    }

    private sealed class SuggestionChipItem(Symbol iconSymbol, string label, string promptText, SuggestionActionKind actionKind)
    {
        public Symbol IconSymbol { get; } = iconSymbol;
        public string Label { get; } = label;
        public string PromptText { get; } = promptText;
        public SuggestionActionKind ActionKind { get; } = actionKind;
    }

    private enum SuggestionActionKind
    {
        FillPrompt,
        OpenAuditTrail
    }

    private sealed class RuntimeStatusItem(Symbol iconSymbol, string label, string value, IBrush accentBrush)
    {
        public Symbol IconSymbol { get; } = iconSymbol;
        public string Label { get; } = label;
        public string Value { get; } = value;
        public IBrush AccentBrush { get; } = accentBrush;
    }
}