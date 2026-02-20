using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SirThaddeus.DesktopRuntime.Converters;

using RadioButton = System.Windows.Controls.RadioButton;
using SirThaddeus.DesktopRuntime.ViewModels;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SirThaddeus.DesktopRuntime;

/// <summary>
/// Chat window for direct LLM conversation + memory browser.
/// Opened via global hotkey (Ctrl+Space). The header has two
/// view tabs: Chat (default) and Memory.
/// </summary>
public partial class MainWindow : Window
{
    private OverlayViewModel? _overlayViewModel;
    private CommandPaletteViewModel? _viewModel;
    private MemoryBrowserViewModel? _memoryBrowserVm;
    private ProfileBrowserViewModel? _profileBrowserVm;
    private SettingsViewModel? _settingsVm;
    private bool _memoryLoaded;
    private bool _profileLoaded;
    private bool _settingsLoaded;

    public MainWindow()
    {
        InitializeComponent();

        // Brand the window icon from the SVG silhouette.
        var brandIcon = Services.BrandIcon.WindowIcon;
        if (brandIcon is not null)
            Icon = brandIcon;

        Loaded += OnLoaded;
        IsVisibleChanged += OnVisibilityChanged;
        Closed += OnClosed;
        MarkdownToFlowDocumentConverter.BriefRequested += OnBriefRequested;
    }

    public void SetOverlayViewModel(OverlayViewModel vm)
    {
        _overlayViewModel = vm;
        HeaderArea.DataContext = vm;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Stop health polling when the command palette hides
        if (e.NewValue is false)
            _settingsVm?.StopVoiceHostHealthPolling();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        MarkdownToFlowDocumentConverter.BriefRequested -= OnBriefRequested;
    }

    // ─────────────────────────────────────────────────────────────────
    // ViewModel Binding
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Binds the Chat ViewModel and wires auto-scroll on new messages/log entries.
    /// </summary>
    public void SetViewModel(CommandPaletteViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.MessageAdded += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                ChatScroller.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        };

        viewModel.LogEntryAdded += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                LogScroller.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        };

        viewModel.BriefingReady += () =>
        {
            Dispatcher.BeginInvoke(() => ActivateTab("Briefing"));
        };
    }

    /// <summary>
    /// Binds the Memory Browser ViewModel. The memory panel uses its
    /// own DataContext so it doesn't collide with the chat bindings.
    /// </summary>
    public void SetMemoryBrowserViewModel(MemoryBrowserViewModel vm)
    {
        _memoryBrowserVm = vm;
        MemoryView.DataContext = vm;
    }

    /// <summary>
    /// Binds the Profile Browser ViewModel. Like the memory panel,
    /// it has its own DataContext.
    /// </summary>
    public void SetProfileBrowserViewModel(ProfileBrowserViewModel vm)
    {
        _profileBrowserVm = vm;
        ProfileView.DataContext = vm;
    }

    /// <summary>
    /// Binds the Settings ViewModel. Own DataContext, own panel.
    /// </summary>
    public void SetSettingsViewModel(SettingsViewModel vm)
    {
        _settingsVm = vm;
        SettingsView.DataContext = vm;
    }

    // ─────────────────────────────────────────────────────────────────
    // View Tab Switching
    // ─────────────────────────────────────────────────────────────────

    private void ChatTab_Click(object sender, RoutedEventArgs e)     => ActivateTab("Chat");
    private void BriefingTab_Click(object sender, RoutedEventArgs e)  => ActivateTab("Briefing");
    private void MemoryTab_Click(object sender, RoutedEventArgs e)   => ActivateTab("Memory");
    private void ProfileTab_Click(object sender, RoutedEventArgs e)  => ActivateTab("Profile");
    private void LogsTab_Click(object sender, RoutedEventArgs e)     => ActivateTab("Logs");
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ActivateTab("Settings");

    private void ActivateTab(string tab)
    {
        ChatTabButton.IsChecked     = tab == "Chat";
        BriefingTabButton.IsChecked = tab == "Briefing";
        MemoryTabButton.IsChecked   = tab == "Memory";
        ProfileTabButton.IsChecked  = tab == "Profile";
        LogsTabButton.IsChecked     = tab == "Logs";
        SettingsTabButton.IsChecked = tab == "Settings";

        ChatView.Visibility     = tab == "Chat"     ? Visibility.Visible : Visibility.Collapsed;
        BriefingView.Visibility = tab == "Briefing" ? Visibility.Visible : Visibility.Collapsed;
        MemoryView.Visibility   = tab == "Memory"   ? Visibility.Visible : Visibility.Collapsed;
        ProfileView.Visibility  = tab == "Profile"  ? Visibility.Visible : Visibility.Collapsed;
        LogsView.Visibility     = tab == "Logs"     ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tab == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        InputArea.Visibility    = tab == "Chat" ? Visibility.Visible : Visibility.Collapsed;
        NewChatButton.Visibility = tab == "Chat"    ? Visibility.Visible : Visibility.Collapsed;

        // Stop health polling when leaving Settings tab
        if (tab != "Settings")
            _settingsVm?.StopVoiceHostHealthPolling();

        switch (tab)
        {
            case "Chat":
            case "Briefing":
                ChatInput?.Focus();
                break;

            case "Memory":
                LazyLoadMemory();
                MemorySearchBox?.Focus();
                break;

            case "Profile":
                LazyLoadProfile();
                break;

            case "Settings":
                LazyLoadSettings();
                break;
        }
    }

    private async void LazyLoadMemory()
    {
        if (!_memoryLoaded && _memoryBrowserVm is not null)
        {
            _memoryLoaded = true;
            await _memoryBrowserVm.LoadAsync();
        }
    }

    private async void LazyLoadProfile()
    {
        if (!_profileLoaded && _profileBrowserVm is not null)
        {
            _profileLoaded = true;
            await _profileBrowserVm.LoadAsync();
        }
    }

    private async void LazyLoadSettings()
    {
        if (!_settingsLoaded && _settingsVm is not null)
        {
            _settingsLoaded = true;
            await _settingsVm.LoadAsync();
        }

        // Start polling VoiceHost health while the Settings tab is visible
        _settingsVm?.StartVoiceHostHealthPolling();
    }

    private void ClearManualLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsVm is null)
            return;

        _settingsVm.LocationLabel = "";

        if (_settingsVm.SaveCommand.CanExecute(null))
            _settingsVm.SaveCommand.Execute(null);
    }

    // ─────────────────────────────────────────────────────────────────
    // Memory Sub-tab Switching (Facts | Events | Chunks)
    // ─────────────────────────────────────────────────────────────────

    private void MemorySubTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tab) return;

        // Show the correct DataGrid
        FactsGrid.Visibility  = tab == "Facts"  ? Visibility.Visible : Visibility.Collapsed;
        EventsGrid.Visibility = tab == "Events" ? Visibility.Visible : Visibility.Collapsed;
        ChunksGrid.Visibility = tab == "Chunks" ? Visibility.Visible : Visibility.Collapsed;

        // Notify the ViewModel to switch tabs (triggers data refresh)
        if (tab == "Facts")       _memoryBrowserVm?.ShowFactsCommand.Execute(null);
        else if (tab == "Events") _memoryBrowserVm?.ShowEventsCommand.Execute(null);
        else if (tab == "Chunks") _memoryBrowserVm?.ShowChunksCommand.Execute(null);
    }

    // ─────────────────────────────────────────────────────────────────
    // Profile Sub-tab Switching (Profiles | Nuggets)
    // ─────────────────────────────────────────────────────────────────

    private void ProfileSubTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tab) return;

        ProfilesGrid.Visibility     = tab == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        NuggetsGrid.Visibility      = tab == "Nuggets"  ? Visibility.Visible : Visibility.Collapsed;

        // Toggle footer buttons
        ProfileAddButton.Visibility      = tab == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        NuggetAddButton.Visibility       = tab == "Nuggets"  ? Visibility.Visible : Visibility.Collapsed;
        ProfileDeleteButton.Visibility   = tab == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        NuggetDeleteButton.Visibility    = tab == "Nuggets"  ? Visibility.Visible : Visibility.Collapsed;
        NuggetPaginationPanel.Visibility = tab == "Nuggets"  ? Visibility.Visible : Visibility.Collapsed;

        if (tab == "Profiles")     _profileBrowserVm?.ShowProfilesCommand.Execute(null);
        else if (tab == "Nuggets") _profileBrowserVm?.ShowNuggetsCommand.Execute(null);
    }

    // ─────────────────────────────────────────────────────────────────
    // DataGrid Edit Commit
    // ─────────────────────────────────────────────────────────────────

    private void DataGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (e.Row.Item)
            {
                case MemoryFactRow:
                    _memoryBrowserVm?.SaveFactCommand.Execute(null);
                    break;
                case MemoryEventRow:
                    _memoryBrowserVm?.SaveEventCommand.Execute(null);
                    break;
                case MemoryChunkRow:
                    _memoryBrowserVm?.SaveChunkCommand.Execute(null);
                    break;
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ProfileGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Row.Item is ProfileCardRow)
                _profileBrowserVm?.SaveProfileCommand.Execute(null);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void NuggetGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Row.Item is NuggetRow)
                _profileBrowserVm?.SaveNuggetCommand.Execute(null);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ─────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ChatInput.Focus();
    }

    // ─────────────────────────────────────────────────────────────────
    // Voice Test Panel (Hold-to-Talk Button)
    // ─────────────────────────────────────────────────────────────────

    private void PttButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null) return;

        if (sender is System.Windows.Controls.Button btn)
            btn.CaptureMouse();

        _viewModel.IsVoiceActive = true;
        _viewModel.VoiceStatusText = "Listening...";
        _viewModel.VoiceTranscriptText = "";
        _viewModel.VoiceMicDown?.Invoke();
        e.Handled = true;
    }

    private void PttButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null) return;

        if (sender is System.Windows.Controls.Button btn && btn.IsMouseCaptured)
            btn.ReleaseMouseCapture();

        _viewModel.VoiceMicUp?.Invoke();
        e.Handled = true;
    }

    private void PttButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // If the user drags off the button while holding, treat as mouse-up.
        if (_viewModel is null) return;

        if (sender is System.Windows.Controls.Button btn && btn.IsMouseCaptured)
        {
            btn.ReleaseMouseCapture();
            _viewModel.VoiceMicUp?.Invoke();
        }
    }

    private void VoiceStopButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.VoiceShutup?.Invoke();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+1..6 tab shortcuts
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
        switch (e.Key)
        {
            case Key.D1: ActivateTab("Chat");     e.Handled = true; return;
            case Key.D2: ActivateTab("Briefing"); e.Handled = true; return;
            case Key.D3: ActivateTab("Memory");   e.Handled = true; return;
            case Key.D4: ActivateTab("Profile");  e.Handled = true; return;
            case Key.D5: ActivateTab("Logs");     e.Handled = true; return;
            case Key.D6: ActivateTab("Settings"); e.Handled = true; return;
        }
        }

        switch (e.Key)
        {
            case Key.Escape:
                _viewModel?.Close();
                e.Handled = true;
                break;

            case Key.Enter when !e.IsRepeat:
                // Only send in chat mode, not when editing a DataGrid cell
                if (ChatView.Visibility == Visibility.Visible &&
                    _viewModel?.SendCommand.CanExecute(null) == true)
                {
                    _viewModel.SendCommand.Execute(null);
                }
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Re-focuses input when the window is re-shown. Conversation persists.
    /// </summary>
    public void Reset()
    {
        if (ChatView.Visibility == Visibility.Visible)
            ChatInput?.Focus();
        else if (MemoryView.Visibility == Visibility.Visible)
            MemorySearchBox?.Focus();
        else if (ProfileView.Visibility == Visibility.Visible)
            NuggetSearchBox?.Focus();
        // Settings tab: no specific element to focus
    }

    /// <summary>
    /// Opens a source card's URL in the default browser.
    /// This is a user-initiated action (click), not an agent action.
    /// </summary>
    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessageViewModel message })
            return;

        var text = (message.Content ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard access can fail in restricted desktop states.
        }
    }

    private void RetryMessage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (sender is not FrameworkElement { DataContext: ChatMessageViewModel message })
            return;

        _viewModel.RetryMessage(message);
    }

    private async void ReadAloudMessage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (sender is not FrameworkElement { DataContext: ChatMessageViewModel message })
            return;

        await _viewModel.ReadAloudMessageAsync(message);
    }

    /// <summary>
    /// Opens a source card's URL in the default browser.
    /// This is a user-initiated action (click), not an agent action.
    /// </summary>
    private void SourceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceCardViewModel card } &&
            !string.IsNullOrWhiteSpace(card.Url))
        {
            try
            {
                Process.Start(new ProcessStartInfo(card.Url) { UseShellExecute = true });
            }
            catch
            {
                // If the browser can't be launched, just ignore
            }
        }
    }

    private void OnBriefRequested(string recommendationName)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel is null)
                return;

            ActivateTab("Chat");
            _viewModel.RequestDeepDiveBriefing(recommendationName);
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // Chat History Sidebar
    // ─────────────────────────────────────────────────────────────────

    private bool _chatHistoryExpanded;

    private void ChatHistoryToggle_Click(object sender, RoutedEventArgs e)
    {
        _chatHistoryExpanded = !_chatHistoryExpanded;

        if (_chatHistoryExpanded)
        {
            ChatHistoryColumn.Width = new System.Windows.GridLength(200);
            ChatHistoryPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ChatHistoryColumn.Width = new System.Windows.GridLength(0);
            ChatHistoryPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ChatHistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatSessionSnapshot session })
            return;

        _viewModel?.LoadChatSessionCommand.Execute(session);
    }
}
