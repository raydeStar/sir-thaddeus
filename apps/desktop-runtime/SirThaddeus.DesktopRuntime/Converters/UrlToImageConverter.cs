using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace SirThaddeus.DesktopRuntime.Converters;

// ─────────────────────────────────────────────────────────────────────────
// URL → BitmapImage Converter
//
// Converts an absolute HTTP(S) URL string into a BitmapImage that WPF
// loads asynchronously. Used for article thumbnails (og:image).
//
// The URL originates from an audited MCP tool call (WebSearch), so
// the network request is traceable and user-initiated. Not a surprise
// outbound call — the user explicitly asked for search results.
//
// Returns null on failure so the XAML fallback kicks in.
// ─────────────────────────────────────────────────────────────────────────

public sealed class UrlToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != "http" && uri.Scheme != "https")
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource       = uri;
            image.CacheOption     = BitmapCacheOption.OnDemand; // Async download
            image.DecodePixelWidth = 240;                       // Match card width, save memory
            image.CreateOptions   = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();

            return image;
        }
        catch
        {
            // Corrupt URL or unsupported format — show placeholder
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
