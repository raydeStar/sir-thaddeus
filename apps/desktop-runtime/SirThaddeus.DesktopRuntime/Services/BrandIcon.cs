using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

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
            var isLightMode = false;
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("SystemUsesLightTheme") is int lightMode)
                {
                    isLightMode = lightMode == 1;
                }
            }

            var iconName = isLightMode ? "sir-thaddeus-tray-dark.ico" : "sir-thaddeus-tray.ico";
            var icoPath = ResolveOutputPath(iconName);
            
            if (File.Exists(icoPath))
            {
                return new Icon(icoPath, 16, 16);
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
        // AppContext.BaseDirectory works correctly in single-file publish
        // (Assembly.Location returns empty string in that scenario).
        var baseDir = AppContext.BaseDirectory;
        
        // Try assets/icons/ first (standard layout), then bin/assets/icons/ (ZIP layout)
        var standard = Path.Combine(baseDir, "assets", "icons", fileName);
        if (File.Exists(standard)) return standard;
        
        var binPath = Path.Combine(baseDir, "bin", "assets", "icons", fileName);
        if (File.Exists(binPath)) return binPath;
        
        return standard; // Return standard path even if missing (callers handle null)
    }
}
