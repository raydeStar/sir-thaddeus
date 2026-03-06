using System.Globalization;
using System.Windows.Data;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfSolidBrush = System.Windows.Media.SolidColorBrush;

namespace SirThaddeus.DesktopRuntime.Converters;

// ─────────────────────────────────────────────────────────────────────────
// String → Brush Converter
//
// Converts a hex color string (e.g. "#CBA6F7") to a SolidColorBrush.
// Used for dynamically-colored letter avatars on source cards.
// ─────────────────────────────────────────────────────────────────────────

public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try
            {
                var color = (WpfColor)WpfColorConverter.ConvertFromString(hex);
                var brush = new WpfSolidBrush(color);
                brush.Freeze();
                return brush;
            }
            catch { /* fall through */ }
        }

        return WpfBrushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
