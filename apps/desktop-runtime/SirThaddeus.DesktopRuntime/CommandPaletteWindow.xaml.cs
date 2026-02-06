using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using SirThaddeus.DesktopRuntime.ViewModels;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SirThaddeus.DesktopRuntime;

/// <summary>
/// Chat window for direct LLM conversation.
/// Opened via global hotkey (Ctrl+Space).
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private CommandPaletteViewModel? _viewModel;

    public CommandPaletteWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Binds the ViewModel and wires auto-scroll on new messages/log entries.
    /// </summary>
    public void SetViewModel(CommandPaletteViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // Scroll to the latest message whenever one is appended
        viewModel.MessageAdded += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                ChatScroller.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        };

        // Scroll the activity log to the latest entry
        viewModel.LogEntryAdded += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                LogScroller.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        };
    }

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
                if (_viewModel?.SendCommand.CanExecute(null) == true)
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
        ChatInput?.Focus();
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
