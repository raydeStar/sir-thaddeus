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
    private bool _memoryLoaded;

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

    // ─────────────────────────────────────────────────────────────────
    // View Tab Switching (Chat ↔ Memory)
    // ─────────────────────────────────────────────────────────────────

    private void ChatTab_Click(object sender, RoutedEventArgs e)
    {
        ChatTabButton.IsChecked  = true;
        MemoryTabButton.IsChecked = false;
        ShowChatView();
    }

    private void MemoryTab_Click(object sender, RoutedEventArgs e)
    {
        ChatTabButton.IsChecked  = false;
        MemoryTabButton.IsChecked = true;
        ShowMemoryView();
    }

    private void ShowChatView()
    {
        ChatView.Visibility   = Visibility.Visible;
        MemoryView.Visibility = Visibility.Collapsed;
        InputArea.Visibility  = Visibility.Visible;
        NewChatButton.Visibility = Visibility.Visible;
        ChatInput?.Focus();
    }

    private async void ShowMemoryView()
    {
        ChatView.Visibility   = Visibility.Collapsed;
        MemoryView.Visibility = Visibility.Visible;
        InputArea.Visibility  = Visibility.Collapsed;
        NewChatButton.Visibility = Visibility.Collapsed;

        // Lazy-load the memory data on first show
        if (!_memoryLoaded && _memoryBrowserVm is not null)
        {
            _memoryLoaded = true;
            await _memoryBrowserVm.LoadAsync();
        }

        MemorySearchBox?.Focus();
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
    // DataGrid Edit Commit
    // ─────────────────────────────────────────────────────────────────

    private void DataGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;

        // Defer the save until the binding has updated the row model
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
        else
            MemorySearchBox?.Focus();
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
