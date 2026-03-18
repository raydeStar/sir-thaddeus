using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SirThaddeus.UI.Avalonia;

internal sealed class ConfirmationDialogWindow : Window
{
    private ConfirmationDialogWindow(string title, string message, string confirmText)
    {
        Title = title;
        Width = 460;
        Height = 220;
        MinWidth = 380;
        MinHeight = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = Brushes.White
        };
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                CreateButton("Cancel", () => Close(false)),
                CreateButton(confirmText, () => Close(true))
            }
        };
        Grid.SetRow(buttonRow, 1);

        var layout = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 14
        };
        layout.Children.Add(messageBlock);
        layout.Children.Add(buttonRow);

        Content = layout;
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, string confirmText)
    {
        var window = new ConfirmationDialogWindow(title, message, confirmText);
        return window.ShowDialog<bool>(owner);
    }

    private static Button CreateButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 90
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
