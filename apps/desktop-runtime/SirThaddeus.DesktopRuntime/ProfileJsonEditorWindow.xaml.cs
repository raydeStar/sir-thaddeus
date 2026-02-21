using System.Text.Json;
using System.Windows;

namespace SirThaddeus.DesktopRuntime;

public partial class ProfileJsonEditorWindow : Window
{
    public string DisplayNameValue => DisplayNameTextBox.Text;
    public string ProfileJsonValue => ProfileJsonTextBox.Text;

    public ProfileJsonEditorWindow(string displayName, string profileJson)
    {
        InitializeComponent();
        DisplayNameTextBox.Text = displayName ?? "";
        ProfileJsonTextBox.Text = string.IsNullOrWhiteSpace(profileJson) ? "{}" : profileJson;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var displayName = (DisplayNameTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            System.Windows.MessageBox.Show(
                this,
                "Display Name cannot be empty.",
                "Validation",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var rawJson = string.IsNullOrWhiteSpace(ProfileJsonTextBox.Text)
            ? "{}"
            : ProfileJsonTextBox.Text;

        try
        {
            using var _ = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Profile JSON is invalid.\n\n{ex.Message}",
                "Invalid JSON",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
