using System.Windows;
using System.Windows.Input;
using SirThaddeus.DesktopRuntime.ViewModels;

namespace SirThaddeus.DesktopRuntime.Views;

public partial class BriefingHistoryPanel : System.Windows.Controls.UserControl
{
    public BriefingHistoryPanel()
    {
        InitializeComponent();
    }

    private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BriefingHistoryEntry entry })
            return;

        if (DataContext is BriefingPanelViewModel vm)
        {
            vm.LoadHistoryCommand.Execute(entry);
        }
    }
}
