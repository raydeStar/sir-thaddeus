using System.Diagnostics;
using System.Windows.Navigation;

namespace SirThaddeus.DesktopRuntime.Views;

public partial class SourcesFlyout : System.Windows.Controls.UserControl
{
    public SourcesFlyout()
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
}
