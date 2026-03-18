using System;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace SirThaddeus.UI.Avalonia;

/// <summary>
/// A lightweight <see cref="SelectableTextBlock"/> that renders a small
/// subset of Markdown inline formatting (bold, italic) as rich inlines.
/// </summary>
public sealed class MarkdownTextBlock : SelectableTextBlock
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    // Matches **bold**, *italic*, _italic_ — non-greedy, single-line only.
    // Order matters: bold (**) must be tried before italic (*).
    private static readonly Regex InlineMarkdownRegex = new(
        @"\*\*(.+?)\*\*" +          // group 1: bold
        @"|(?<!\w)\*([^*\r\n]+?)\*(?!\w)" +  // group 2: italic (asterisk)
        @"|(?<!\w)_([^_\r\n]+?)_(?!\w)",      // group 3: italic (underscore)
        RegexOptions.Compiled,
        RegexTimeout);

    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string>(nameof(Markdown), defaultValue: "");

    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (block, _) => block.RebuildInlines());
    }

    private void RebuildInlines()
    {
        Inlines?.Clear();
        var text = Markdown;
        if (string.IsNullOrEmpty(text))
            return;

        Inlines ??= new InlineCollection();
        int pos = 0;

        try
        {
            foreach (Match match in InlineMarkdownRegex.Matches(text))
            {
                // Plain text before this match.
                if (match.Index > pos)
                    Inlines.Add(new Run(text[pos..match.Index]));

                if (match.Groups[1].Success)
                {
                    // **bold**
                    Inlines.Add(new Run(match.Groups[1].Value)
                    {
                        FontWeight = FontWeight.Bold
                    });
                }
                else if (match.Groups[2].Success)
                {
                    // *italic*
                    Inlines.Add(new Run(match.Groups[2].Value)
                    {
                        FontStyle = FontStyle.Italic
                    });
                }
                else if (match.Groups[3].Success)
                {
                    // _italic_
                    Inlines.Add(new Run(match.Groups[3].Value)
                    {
                        FontStyle = FontStyle.Italic
                    });
                }

                pos = match.Index + match.Length;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Fall back to plain text on pathological input.
            Inlines.Clear();
            Inlines.Add(new Run(text.Replace("**", "", StringComparison.Ordinal)));
            return;
        }

        // Remaining plain text after the last match.
        if (pos < text.Length)
            Inlines.Add(new Run(text[pos..]));
    }
}
