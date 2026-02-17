using System.Windows;
using System.Windows.Input;

namespace SirThaddeus.DesktopRuntime;

/// <summary>
/// Manual location prompt shown during onboarding.
/// </summary>
public partial class LocationPromptWindow : Window
{
    public LocationPromptWindow()
    {
        InitializeComponent();

        var brandIcon = Services.BrandIcon.WindowIcon;
        if (brandIcon is not null)
            Icon = brandIcon;

        Loaded += (_, _) =>
        {
            ManualLocationTextBox.Focus();
            Keyboard.Focus(ManualLocationTextBox);
        };
    }

    public string ManualLocationValue => (ManualLocationTextBox.Text ?? "").Trim();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ManualLocationValue))
            return;

        DialogResult = true;
        Close();
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ManualLocationTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(ManualLocationValue);
    }

    private void ManualLocationTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !SaveButton.IsEnabled)
            return;

        SaveButton_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }
}
