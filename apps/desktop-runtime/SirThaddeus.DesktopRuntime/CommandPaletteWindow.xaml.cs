using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using RadioButton = System.Windows.Controls.RadioButton;
using SirThaddeus.DesktopRuntime.ViewModels;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SirThaddeus.DesktopRuntime;

/// <summary>
/// Chat window for direct LLM conversation + memory browser.
/// Opened via global hotkey (Ctrl+Space). The header has two
/// view tabs: Chat (default) and Memory.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private CommandPaletteViewModel? _viewModel;
    private MemoryBrowserViewModel? _memoryBrowserVm;
    private ProfileBrowserViewModel? _profileBrowserVm;
    private SettingsViewModel? _settingsVm;
    private bool _memoryLoaded;
    private bool _profileLoaded;
    private bool _settingsLoaded;

    public CommandPaletteWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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
    // View Tab Switching (Chat ↔ Memory)
    // ─────────────────────────────────────────────────────────────────

    private void ChatTab_Click(object sender, RoutedEventArgs e)     => ActivateTab("Chat");
    private void MemoryTab_Click(object sender, RoutedEventArgs e)   => ActivateTab("Memory");
    private void ProfileTab_Click(object sender, RoutedEventArgs e)  => ActivateTab("Profile");
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ActivateTab("Settings");

    private void ActivateTab(string tab)
    {
        ChatTabButton.IsChecked     = tab == "Chat";
        MemoryTabButton.IsChecked   = tab == "Memory";
        ProfileTabButton.IsChecked  = tab == "Profile";
        SettingsTabButton.IsChecked = tab == "Settings";

        ChatView.Visibility     = tab == "Chat"     ? Visibility.Visible : Visibility.Collapsed;
        MemoryView.Visibility   = tab == "Memory"   ? Visibility.Visible : Visibility.Collapsed;
        ProfileView.Visibility  = tab == "Profile"  ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tab == "Settings"  ? Visibility.Visible : Visibility.Collapsed;
        InputArea.Visibility    = tab == "Chat"      ? Visibility.Visible : Visibility.Collapsed;
        NewChatButton.Visibility = tab == "Chat"     ? Visibility.Visible : Visibility.Collapsed;

        switch (tab)
        {
            case "Chat":
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

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
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
}
