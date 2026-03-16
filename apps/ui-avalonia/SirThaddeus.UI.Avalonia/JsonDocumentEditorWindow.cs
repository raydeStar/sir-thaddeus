using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace SirThaddeus.UI.Avalonia;

internal sealed class JsonDocumentEditorWindow : Window
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly TextBox _editor;
    private readonly TextBlock _statusText;

    private JsonDocumentEditorWindow(string title, string instruction, string initialText)
    {
        Title = title;
        Width = 920;
        Height = 760;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _statusText = new TextBlock
        {
            Text = instruction,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            FontSize = 12
        };

        _editor = new TextBox
        {
            Text = initialText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 420
        };

        var formatButton = new Button
        {
            Content = "Format",
            MinWidth = 90
        };
        formatButton.Click += FormatButton_Click;

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90
        };
        cancelButton.Click += (_, _) => Close(null);

        var saveButton = new Button
        {
            Content = "Save",
            MinWidth = 90
        };
        saveButton.Click += (_, _) => Close(_editor.Text ?? "");

        var editorBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Child = _editor
        };
        Grid.SetRow(editorBorder, 1);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                formatButton,
                cancelButton,
                saveButton
            }
        };
        Grid.SetRow(buttonRow, 2);

        var layout = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12
        };
        layout.Children.Add(_statusText);
        layout.Children.Add(editorBorder);
        layout.Children.Add(buttonRow);

        Content = layout;

        Opened += (_, _) => _editor.Focus();
        KeyDown += JsonDocumentEditorWindow_KeyDown;
    }

    public static Task<string?> ShowAsync(Window owner, string title, string instruction, string initialText)
    {
        var window = new JsonDocumentEditorWindow(title, instruction, initialText);
        return window.ShowDialog<string?>(owner);
    }

    private void JsonDocumentEditorWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Enter)
        {
            e.Handled = true;
            Close(_editor.Text ?? "");
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
        }
    }

    private void FormatButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(_editor.Text ?? "", ReadOptions);
            _editor.Text = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            _statusText.Text = "JSON formatted.";
            _statusText.Foreground = Brushes.Gray;
        }
        catch (Exception ex)
        {
            _statusText.Text = "Format failed: " + ex.Message;
            _statusText.Foreground = Brushes.IndianRed;
        }
    }
}
