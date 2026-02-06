using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace SirThaddeus.DesktopRuntime.Converters;

// ─────────────────────────────────────────────────────────────────────────
// Base64 → BitmapImage Converter
//
// Decodes a base64 data URI (or raw base64 string) into a WPF
// BitmapImage for display in an Image control. Returns null on
// failure so the XAML fallback (letter avatar) can kick in.
//
// All image data arrives pre-fetched from the MCP server — this
// converter makes zero network calls. I6 clean.
// ─────────────────────────────────────────────────────────────────────────

public sealed class Base64ToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string base64 || string.IsNullOrWhiteSpace(base64))
            return null;

        try
        {
            // Strip data URI prefix if present (e.g. "data:image/png;base64,")
            var commaIndex = base64.IndexOf(',');
            var raw = commaIndex >= 0 ? base64[(commaIndex + 1)..] : base64;

            var bytes = System.Convert.FromBase64String(raw);

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = 32;  // Small favicon, save memory
            image.EndInit();
            image.Freeze();  // Thread-safe

            return image;
        }
        catch
        {
            // Invalid base64 or corrupt image — fallback to letter avatar
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
