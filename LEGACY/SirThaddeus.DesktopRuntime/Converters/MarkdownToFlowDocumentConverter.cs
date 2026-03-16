using System.Globalization;
using System.Diagnostics;
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
/// - HTML anchor tags: <a href="https://...">Label</a>
///
/// Intentionally not supported (yet): code blocks, tables, images.
/// Keep it simple and predictable.
/// </summary>
public sealed class MarkdownToFlowDocumentConverter : IValueConverter
{
    /// <summary>
    /// Fired when a recommendation hyperlink (stbrief:...) is clicked in chat.
    /// The window/view-model can use this to trigger a deep-dive briefing.
    /// </summary>
    public static event Action<string>? BriefRequested;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        return Markdownish.ToFlowDocument(text);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static class Markdownish
    {
        private const string BriefScheme = "stbrief";
        private const double ParagraphSpacing = 6;
        private static readonly System.Text.RegularExpressions.Regex AnchorTagRegex = new(
            "<a\\s+href\\s*=\\s*\"(?<url>[^\"]+)\"\\s*>(?<label>.*?)</a>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.Singleline);
        private static readonly System.Text.RegularExpressions.Regex NumberedItemRegex = new(
            "^\\s*(?<index>\\d{1,2})[\\.)]\\s+(?<label>.+?)\\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        public static FlowDocument ToFlowDocument(string raw)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0)
            };
            if (System.Windows.Application.Current?.TryFindResource("PrimaryFont") is System.Windows.Media.FontFamily pf)
            {
                doc.FontFamily = pf;
            }
            else
            {
                doc.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            }

            var text = NormalizeNewlines(raw);
            if (string.IsNullOrWhiteSpace(text))
                return doc;

            var lines = text.Split('\n');

            List? currentList = null;
            var inRecommendationSection = false;

            foreach (var lineRaw in lines)
            {
                var line = lineRaw.TrimEnd();
                var trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushListIfAny(doc, ref currentList);
                    // Preserve a little breathing room between paragraphs.
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, ParagraphSpacing) });
                    continue;
                }

                if (IsRecommendationSectionHeading(trimmed))
                    inRecommendationSection = true;
                else if (IsRecommendationSectionBreak(trimmed))
                    inRecommendationSection = false;

                if (TryParseRecommendationItem(trimmed, inRecommendationSection, out var ordinal, out var recommendationLabel))
                {
                    FlushListIfAny(doc, ref currentList);
                    var para = new Paragraph { Margin = new Thickness(0, 0, 0, ParagraphSpacing) };
                    para.Inlines.Add(new Run($"{ordinal}. "));
                    para.Inlines.Add(CreateBriefLink(recommendationLabel));
                    doc.Blocks.Add(para);
                    continue;
                }

                if (TryParseBoldNameRecommendation(trimmed, inRecommendationSection, out var boldName, out var suffix))
                {
                    FlushListIfAny(doc, ref currentList);
                    var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                    para.FontWeight = FontWeights.SemiBold;
                    para.FontSize = 13.5;
                    para.Inlines.Add(CreateBriefLink(boldName));
                    if (!string.IsNullOrWhiteSpace(suffix))
                        para.Inlines.Add(new Run($" {suffix}"));
                    doc.Blocks.Add(para);
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
                var boldStart = text.IndexOf("**", i, StringComparison.Ordinal);
                var anchorMatch = AnchorTagRegex.Match(text, i);
                var anchorStart = anchorMatch.Success ? anchorMatch.Index : -1;
                var nextTokenStart = MinPositive(boldStart, anchorStart);

                if (nextTokenStart < 0)
                {
                    paragraph.Inlines.Add(new Run(text[i..]));
                    return;
                }

                if (nextTokenStart > i)
                    paragraph.Inlines.Add(new Run(text[i..nextTokenStart]));

                if (anchorStart >= 0 && anchorStart == nextTokenStart)
                {
                    var url = anchorMatch.Groups["url"].Value.Trim();
                    var label = anchorMatch.Groups["label"].Value.Trim();

                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        var link = new Hyperlink(new Run(label.Length > 0 ? label : url))
                        {
                            NavigateUri = uri,
                            ToolTip = uri.AbsoluteUri,
                            Foreground = ResolveAccentBrush(),
                            TextDecorations = null
                        };
                        link.Click += (_, _) => HandleHyperlinkNavigation(uri);
                        paragraph.Inlines.Add(link);
                    }
                    else
                    {
                        paragraph.Inlines.Add(new Run(label.Length > 0 ? label : url));
                    }

                    i = anchorMatch.Index + anchorMatch.Length;
                    continue;
                }

                var boldEnd = text.IndexOf("**", boldStart + 2, StringComparison.Ordinal);
                if (boldEnd < 0)
                {
                    // Unbalanced markers — just render the remainder as plain text.
                    paragraph.Inlines.Add(new Run(text[nextTokenStart..]));
                    return;
                }

                var boldText = text[(boldStart + 2)..boldEnd];
                if (boldText.Length > 0)
                    paragraph.Inlines.Add(new Bold(new Run(boldText)));

                i = boldEnd + 2;
            }
        }

        private static int MinPositive(int first, int second)
        {
            if (first < 0) return second;
            if (second < 0) return first;
            return Math.Min(first, second);
        }

        private static Hyperlink CreateBriefLink(string recommendationLabel)
        {
            var escaped = Uri.EscapeDataString(recommendationLabel);
            var uri = new Uri($"{BriefScheme}:{escaped}");
            var link = new Hyperlink(new Run(recommendationLabel))
            {
                NavigateUri = uri,
                ToolTip = $"Create briefing for {recommendationLabel}",
                Foreground = ResolveAccentBrush(),
                TextDecorations = null
            };

            link.Click += (_, _) => HandleHyperlinkNavigation(uri);
            return link;
        }

        private static void HandleHyperlinkNavigation(Uri uri)
        {
            if (uri.Scheme.Equals(BriefScheme, StringComparison.OrdinalIgnoreCase))
            {
                var raw = uri.OriginalString;
                var payload = raw.Length > BriefScheme.Length + 1
                    ? raw[(BriefScheme.Length + 1)..]
                    : "";
                var label = Uri.UnescapeDataString(payload).Trim();

                if (!string.IsNullOrWhiteSpace(label))
                    BriefRequested?.Invoke(label);

                return;
            }

            OpenExternal(uri);
        }

        private static bool TryParseRecommendationItem(
            string line,
            bool inRecommendationSection,
            out string ordinal,
            out string recommendationLabel)
        {
            ordinal = "";
            recommendationLabel = "";

            if (!inRecommendationSection)
                return false;

            var match = NumberedItemRegex.Match(line);
            if (!match.Success)
                return false;

            var candidate = match.Groups["label"].Value.Trim();
            if (!LooksLikeRecommendationLabel(candidate))
                return false;

            ordinal = match.Groups["index"].Value;
            recommendationLabel = candidate;
            return true;
        }

        private static bool LooksLikeRecommendationLabel(string candidate)
        {
            if (candidate.Length is < 2 or > 90)
                return false;

            if (!candidate.Any(char.IsLetter))
                return false;

            if (candidate.Contains(':', StringComparison.Ordinal))
                return false;

            if (candidate.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length is < 1 or > 10)
                return false;

            var lower = candidate.ToLowerInvariant();
            if (lower.Contains("step ", StringComparison.Ordinal) ||
                lower.Contains("process", StringComparison.Ordinal) ||
                lower.Contains("fertilization", StringComparison.Ordinal) ||
                lower.Contains("pregnancy", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Catches bold-name recommendation entries like "**Restaurant Name** - $$$"
        /// or standalone "**Restaurant Name**" when in a recommendation section.
        /// Returns the name (for briefing link) and any trailing suffix (price, cuisine).
        /// </summary>
        private static bool TryParseBoldNameRecommendation(
            string line,
            bool inRecommendationSection,
            out string boldName,
            out string suffix)
        {
            boldName = "";
            suffix = "";

            if (!inRecommendationSection)
                return false;

            if (!line.StartsWith("**", StringComparison.Ordinal))
                return false;

            var closingBold = line.IndexOf("**", 2, StringComparison.Ordinal);
            if (closingBold < 0)
                return false;

            var candidate = line[2..closingBold].Trim();
            if (!LooksLikeRecommendationLabel(candidate))
                return false;

            boldName = candidate;

            var remainder = line[(closingBold + 2)..].Trim();
            if (remainder.StartsWith('-') || remainder.StartsWith('\u2013') || remainder.StartsWith('\u2014'))
                suffix = remainder;

            return true;
        }

        private static bool IsRecommendationSectionHeading(string line)
        {
            var lower = line.ToLowerInvariant();
            return lower.Contains("top picks", StringComparison.Ordinal) ||
                   lower.Contains("recommendations", StringComparison.Ordinal) ||
                   lower.Contains("recommended", StringComparison.Ordinal) ||
                   lower.Contains("best options", StringComparison.Ordinal) ||
                   lower.Contains("nearby options", StringComparison.Ordinal) ||
                   lower.Contains("restaurants near", StringComparison.Ordinal);
        }

        private static bool IsRecommendationSectionBreak(string line)
        {
            var lower = line.ToLowerInvariant();
            return lower.StartsWith("hours", StringComparison.Ordinal) ||
                   lower.StartsWith("reviews", StringComparison.Ordinal) ||
                   lower.StartsWith("details", StringComparison.Ordinal) ||
                   lower.StartsWith("sources", StringComparison.Ordinal) ||
                   lower.StartsWith("summary", StringComparison.Ordinal) ||
                   lower.StartsWith("what to expect", StringComparison.Ordinal) ||
                   lower.StartsWith("warnings", StringComparison.Ordinal);
        }

        private static void OpenExternal(Uri uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Link navigation is best-effort; keep rendering stable.
            }
        }

        /// <summary>
        /// Pulls the BlueBrush from the application resource dictionary so inline
        /// hyperlinks match the shared theme without hardcoding hex values.
        /// </summary>
        private static System.Windows.Media.Brush ResolveAccentBrush()
        {
            if (System.Windows.Application.Current?.TryFindResource("BlueBrush")
                is System.Windows.Media.Brush b)
                return b;

            // Fallback if theme is missing (design-time, tests).
            return new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x89, 0xB4, 0xFA));
        }

        private static string NormalizeNewlines(string s)
            => s.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}

