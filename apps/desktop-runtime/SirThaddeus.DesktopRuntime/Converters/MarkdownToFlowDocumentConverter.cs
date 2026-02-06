using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;

namespace SirThaddeus.DesktopRuntime.Converters;

/// <summary>
/// Converts a small subset of Markdown-ish text into a WPF FlowDocument.
///
/// Why this exists:
/// - The app uses local, smaller models that often output lightweight Markdown
///   (e.g. **bold** section labels + "- " bullets).
/// - A TextBox can't render inline formatting.
/// - FlowDocument renders formatting AND remains selectable/copyable.
///
/// Supported:
/// - Bold: **like this**
/// - Headings: "# Heading" / "## Heading" / "### Heading"
/// - Bullet lines: "- item" / "* item"
///
/// Intentionally not supported (yet): links, code blocks, tables, images.
/// Keep it simple and predictable.
/// </summary>
public sealed class MarkdownToFlowDocumentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        return Markdownish.ToFlowDocument(text);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static class Markdownish
    {
        private const double ParagraphSpacing = 6;

        public static FlowDocument ToFlowDocument(string raw)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                // Font/Foreground are inherited from the viewer control.
            };

            var text = NormalizeNewlines(raw);
            if (string.IsNullOrWhiteSpace(text))
                return doc;

            var lines = text.Split('\n');

            List? currentList = null;

            foreach (var lineRaw in lines)
            {
                var line = lineRaw.TrimEnd();

                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushListIfAny(doc, ref currentList);
                    // Preserve a little breathing room between paragraphs.
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, ParagraphSpacing) });
                    continue;
                }

                if (TryParseBullet(line, out var bulletText))
                {
                    currentList ??= new List
                    {
                        MarkerStyle = TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, ParagraphSpacing)
                    };

                    var itemPara = new Paragraph { Margin = new Thickness(0) };
                    AddInlines(itemPara, bulletText);
                    currentList.ListItems.Add(new ListItem(itemPara));
                    continue;
                }

                FlushListIfAny(doc, ref currentList);

                var trimmed = line.Trim();

                if (TryParseHeading(trimmed, out var headingText, out var headingLevel))
                {
                    var para = new Paragraph { Margin = new Thickness(0, 2, 0, ParagraphSpacing) };
                    var bold = new Bold(new Run(headingText));

                    // A lightweight visual hierarchy (no giant typography in chat).
                    para.FontWeight = FontWeights.SemiBold;
                    para.FontSize = headingLevel switch
                    {
                        1 => 15,
                        2 => 14,
                        _ => 13.5
                    };

                    para.Inlines.Add(bold);
                    doc.Blocks.Add(para);
                    continue;
                }

                // Treat a full bold line (e.g. **Major Market Moves:**) as a mini heading.
                if (TryParseBoldLineHeading(trimmed, out var boldHeading))
                {
                    var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                    para.FontWeight = FontWeights.SemiBold;
                    para.FontSize = 13.5;
                    para.Inlines.Add(new Bold(new Run(boldHeading)));
                    doc.Blocks.Add(para);
                    continue;
                }

                var p = new Paragraph { Margin = new Thickness(0, 0, 0, ParagraphSpacing) };
                AddInlines(p, trimmed);
                doc.Blocks.Add(p);
            }

            FlushListIfAny(doc, ref currentList);
            return doc;
        }

        private static void FlushListIfAny(FlowDocument doc, ref List? list)
        {
            if (list is null)
                return;

            doc.Blocks.Add(list);
            list = null;
        }

        private static bool TryParseBullet(string line, out string bulletText)
        {
            var t = line.TrimStart();
            if (t.StartsWith("- "))
            {
                bulletText = t[2..].Trim();
                return true;
            }
            if (t.StartsWith("* "))
            {
                bulletText = t[2..].Trim();
                return true;
            }

            bulletText = "";
            return false;
        }

        private static bool TryParseHeading(string line, out string headingText, out int level)
        {
            // Minimal Markdown headings: #, ##, ###
            headingText = "";
            level = 0;

            var i = 0;
            while (i < line.Length && line[i] == '#')
                i++;

            if (i is < 1 or > 3)
                return false;

            if (i < line.Length && line[i] == ' ')
            {
                level = i;
                headingText = line[(i + 1)..].Trim();
                return headingText.Length > 0;
            }

            return false;
        }

        private static bool TryParseBoldLineHeading(string line, out string headingText)
        {
            headingText = "";

            if (!line.StartsWith("**", StringComparison.Ordinal) ||
                !line.EndsWith("**", StringComparison.Ordinal) ||
                line.Length < 5)
                return false;

            var inner = line[2..^2].Trim();
            if (inner.Length == 0 || inner.Length > 80)
                return false;

            // Avoid treating mid-sentence bold as a heading.
            if (inner.Contains("**", StringComparison.Ordinal))
                return false;

            headingText = inner;
            return true;
        }

        private static void AddInlines(Paragraph paragraph, string text)
        {
            var i = 0;
            while (i < text.Length)
            {
                var start = text.IndexOf("**", i, StringComparison.Ordinal);
                if (start < 0)
                {
                    paragraph.Inlines.Add(new Run(text[i..]));
                    return;
                }

                var end = text.IndexOf("**", start + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    // Unbalanced markers — just render the remainder as plain text.
                    paragraph.Inlines.Add(new Run(text[i..]));
                    return;
                }

                if (start > i)
                    paragraph.Inlines.Add(new Run(text[i..start]));

                var boldText = text[(start + 2)..end];
                if (boldText.Length > 0)
                    paragraph.Inlines.Add(new Bold(new Run(boldText)));

                i = end + 2;
            }
        }

        private static string NormalizeNewlines(string s)
            => s.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}

