using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Centralized brand icon loading for tray + WPF windows.
/// Uses pre-generated .ico files from `assets/icons/`.
/// </summary>
public static class BrandIcon
{
    private static readonly Lazy<Icon> CachedTrayIcon = new(LoadTrayIcon);
    private static readonly Lazy<ImageSource?> CachedWindowIcon = new(LoadWindowIcon);

    /// <summary>
    /// 16x16 white-on-transparent icon for the system tray.
    /// Falls back to <see cref="SystemIcons.Application"/> on failure.
    /// </summary>
    public static Icon TrayIcon => CachedTrayIcon.Value;

    /// <summary>
    /// Multi-size icon as <see cref="ImageSource"/> for WPF window title bars.
    /// Returns null if rendering fails (WPF will use default).
    /// </summary>
    public static ImageSource? WindowIcon => CachedWindowIcon.Value;

    private static Icon LoadTrayIcon()
    {
        try
        {
            var icoPath = ResolveOutputPath("sir-thaddeus-tray.ico");
            if (File.Exists(icoPath))
            {
                // Clone through stream to avoid long-lived file locks.
                using var fs = File.OpenRead(icoPath);
                return new Icon(fs, 16, 16);
            }
        }
        catch
        {
            // Fall through to default.
        }

        return SystemIcons.Application;
    }

    private static ImageSource? LoadWindowIcon()
    {
        try
        {
            var icoPath = ResolveOutputPath("sir-thaddeus.ico");
            if (File.Exists(icoPath))
            {
                // BitmapImage only picks one frame from a multi-frame .ico
                // (usually the smallest). IconBitmapDecoder reads all frames
                // so we can hand WPF the largest one — it will downscale
                // cleanly for title bars (16px) while the taskbar and
                // Alt+Tab get a crisp high-res version.
                using var fs = File.OpenRead(icoPath);
                var decoder = new IconBitmapDecoder(
                    fs,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                var best = decoder.Frames
                    .OrderByDescending(f => f.PixelWidth)
                    .FirstOrDefault();

                best?.Freeze();
                return best;
            }
        }
        catch
        {
            // Fall through.
        }

        return null;
    }

    private static string ResolveOutputPath(string fileName)
    {
        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        return Path.Combine(asmDir, "assets", "icons", fileName);
    }
}
