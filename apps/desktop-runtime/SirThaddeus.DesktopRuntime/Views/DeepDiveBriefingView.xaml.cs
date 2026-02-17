using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace SirThaddeus.DesktopRuntime.Views;

public partial class DeepDiveBriefingView : System.Windows.Controls.UserControl
{
    private bool _historyExpanded;

    public DeepDiveBriefingView()
    {
        InitializeComponent();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Link open failures are non-fatal.
        }

        e.Handled = true;
    }

    private void HistoryToggle_Click(object sender, RoutedEventArgs e)
    {
        _historyExpanded = !_historyExpanded;

        if (_historyExpanded)
        {
            HistoryColumn.Width = new GridLength(200);
            HistoryPanel.Visibility = Visibility.Visible;
        }
        else
        {
            HistoryColumn.Width = new GridLength(0);
            HistoryPanel.Visibility = Visibility.Collapsed;
        }
    }
}
