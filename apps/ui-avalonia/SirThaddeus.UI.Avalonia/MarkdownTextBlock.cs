using System;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace SirThaddeus.UI.Avalonia;

/// <summary>
/// A lightweight <see cref="SelectableTextBlock"/> that renders a small subset of
/// Markdown inline formatting (<c>**bold**</c>, <c>*italic*</c>, <c>_italic_</c>)
/// as rich <see cref="Inline"/> runs.
/// <para>
/// Used in the chat message list to give assistant replies basic text emphasis
/// without pulling in a full Markdown rendering library.
/// </para>
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Bold (<c>**</c>) is matched before italic (<c>*</c>) to avoid ambiguity.</item>
///   <item>Word-boundary guards prevent mid-word false positives for <c>*</c> and <c>_</c>.</item>
///   <item>A 100 ms regex timeout protects against pathological back-tracking; on timeout
///         the control falls back to unstyled plain text.</item>
/// </list>
/// </remarks>
public sealed partial class MarkdownTextBlock : SelectableTextBlock
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    // ── Source-generated regexes ────────────────────────────────────────
    // Bold (**) must be tried before italic (*) in the combined pattern.

    [GeneratedRegex(@"\*\*(.+?)\*\*|(?<!\w)\*([^*\r\n]+?)\*(?!\w)|(?<!\w)_([^_\r\n]+?)_(?!\w)",
        RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex InlineMarkdownRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex BoldStripRegex();

    [GeneratedRegex(@"(?<!\w)\*([^*\r\n]+?)\*(?!\w)", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex ItalicAsteriskStripRegex();

    [GeneratedRegex(@"(?<!\w)_([^_\r\n]+?)_(?!\w)", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex ItalicUnderscoreStripRegex();

    // ── Styled property ────────────────────────────────────────────────

    /// <summary>
    /// The raw Markdown source text. When set, inlines are rebuilt automatically.
    /// </summary>
    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string>(nameof(Markdown), defaultValue: "");

    /// <summary>
    /// Gets or sets the raw Markdown source text that drives the rendered inlines.
    /// </summary>
    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            static (block, _) => block.RebuildInlines());
    }

    // ── Inline rendering ───────────────────────────────────────────────

    /// <summary>
    /// Parses <see cref="Markdown"/> and replaces the control's inline collection
    /// with styled <see cref="Run"/> elements. Falls back to plain text on timeout.
    /// </summary>
    private void RebuildInlines()
    {
        var text = Markdown;

        // Keep Text in sync for clipboard / accessibility / screen-readers.
        Text = StripMarkdown(text);

        Inlines?.Clear();
        if (string.IsNullOrEmpty(text))
            return;

        Inlines ??= new InlineCollection();
        int pos = 0;

        try
        {
            foreach (Match match in InlineMarkdownRegex().Matches(text))
            {
                if (match.Index > pos)
                    Inlines.Add(new Run(text[pos..match.Index]));

                if (match.Groups[1].Success)
                {
                    Inlines.Add(new Run(match.Groups[1].Value) { FontWeight = FontWeight.Bold });
                }
                else if (match.Groups[2].Success)
                {
                    Inlines.Add(new Run(match.Groups[2].Value) { FontStyle = FontStyle.Italic });
                }
                else if (match.Groups[3].Success)
                {
                    Inlines.Add(new Run(match.Groups[3].Value) { FontStyle = FontStyle.Italic });
                }

                pos = match.Index + match.Length;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            Inlines.Clear();
            Inlines.Add(new Run(Text ?? string.Empty));
            return;
        }

        if (pos < text.Length)
            Inlines.Add(new Run(text[pos..]));
    }

    /// <summary>
    /// Returns a plain-text copy of <paramref name="text"/> with Markdown markers removed.
    /// Used to populate <see cref="SelectableTextBlock.Text"/> for clipboard and accessibility.
    /// </summary>
    private static string StripMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var plain = BoldStripRegex().Replace(text, "$1");
        plain = ItalicAsteriskStripRegex().Replace(plain, "$1");
        plain = ItalicUnderscoreStripRegex().Replace(plain, "$1");
        return plain;
    }
}
