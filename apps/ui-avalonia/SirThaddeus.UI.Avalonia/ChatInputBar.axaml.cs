using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace SirThaddeus.UI.Avalonia;

public partial class ChatInputBar : UserControl
{
    public ChatInputBar()
    {
        InitializeComponent();
        PromptBox.GotFocus += PromptBox_GotFocus;
        PromptBox.LostFocus += PromptBox_LostFocus;
        SetComposerFocusVisual(false);
    }

    private void PromptBox_GotFocus(object? sender, RoutedEventArgs e)
    {
        SetComposerFocusVisual(true);
    }

    public void SetLayoutMode(bool activeConversation)
    {
        ComposerShell.CornerRadius = activeConversation ? new CornerRadius(22) : new CornerRadius(26);
        ComposerShell.Padding = activeConversation ? new Thickness(12, 10) : new Thickness(10, 10);
        ComposerShell.MinWidth = activeConversation ? 720 : 600;
        ComposerShell.MinHeight = activeConversation ? 60 : 0;
        PromptBox.MinHeight = activeConversation ? 48 : 48;
        PromptBox.Padding = activeConversation ? new Thickness(0, 12, 0, 12) : new Thickness(0, 12, 0, 12);
    }

    private void PromptBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        SetComposerFocusVisual(false);
    }

    private void SetComposerFocusVisual(bool focused)
    {
        var key = focused ? "AccentPrimary" : "BorderMuted";
        var brush = this.FindResource(key);
        if (brush is IBrush resolved)
        {
            ComposerShell.BorderBrush = resolved;
        }
    }
}
