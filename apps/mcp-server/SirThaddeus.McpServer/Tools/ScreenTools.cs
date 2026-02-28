using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Screen Observation Tools
//
// Captures the display and extracts visible text via Windows 10 OCR.
// When the active window is a web browser, also fetches the actual page
// content via HTTP for much richer text than OCR alone can provide.
// These are observation-only tools — they read the screen but never
// modify it. All results are text; no raw image data crosses the wire.
//
// Bounds:
//   - Capture limited to primary monitor (or active window rect).
//   - OCR text capped at 8 000 characters.
//   - Page content capped at 6 000 characters.
//   - Single capture per call, no video/streaming.
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class ScreenTools
{
    private const int MaxOcrChars = 8_000;
    private const int MaxPageChars = 6_000;

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
        "chromium", "iexplore", "ApplicationFrameHost" // Edge PWA / UWP
    };

    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""<>\]\)]{8,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ═══════════════════════════════════════════════════════════════════
    // MCP Tool: ScreenCapture
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool, Description(
        "Captures the user's screen and extracts visible text via OCR. " +
        "If the active window is a web browser, also fetches the actual " +
        "page content via HTTP for a much richer summary. Returns active " +
        "window info, screen resolution, OCR text, and (for browsers) " +
        "the full page content. Use this when the user asks about what " +
        "is on their screen, asks to summarize a page, or wants you to " +
        "read what they are looking at.")]
    public static async Task<string> ScreenCapture(
        [Description(
            "'full_screen' (default — captures the entire monitor, use this " +
            "almost always) or 'active_window' (only when user explicitly " +
            "asks about 'this window' or 'the active window')"
        )] string target = "full_screen",
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Screen Capture Report ===");

        // ── Phase 1: Window & display info (lightweight, rarely fails) ──
        WindowInfo? windowInfo = null;
        int screenW = 0, screenH = 0;
        try
        {
            SetProcessDpiAwareness();
            windowInfo = GetActiveWindowInfo();
            (screenW, screenH) = GetPrimaryScreenSize();

            sb.AppendLine($"Active Window: \"{windowInfo.Title}\"");
            sb.AppendLine($"Process: {windowInfo.ProcessName} (PID {windowInfo.ProcessId})");
            sb.AppendLine($"Screen: {screenW}x{screenH}");

            var isBrowser = IsBrowserProcess(windowInfo.ProcessName);
            sb.AppendLine($"Browser Detected: {(isBrowser ? "yes" : "no")}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[Window info error: {ex.GetType().Name}: {ex.Message}]");
        }

        // ── Phase 2: Screen capture (needs desktop access) ──────────────
        Bitmap? bitmap = null;
        try
        {
            Rectangle captureRect;
            if (target.Equals("active_window", StringComparison.OrdinalIgnoreCase) &&
                windowInfo?.Bounds is { Width: > 0, Height: > 0 } bounds)
            {
                captureRect = bounds;
            }
            else
            {
                captureRect = new Rectangle(0, 0,
                    screenW > 0 ? screenW : 1920,
                    screenH > 0 ? screenH : 1080);
            }

            bitmap = CaptureRegion(captureRect);
            sb.AppendLine($"Captured: {captureRect.Width}x{captureRect.Height} ({target})");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[Capture error: {ex.GetType().Name}: {ex.Message}]");
        }

        // ── Phase 3: OCR (requires WinRT, may fail on threading) ────────
        string? ocrText = null;
        sb.AppendLine();
        if (bitmap != null)
        {
            try
            {
                ocrText = await RunOcrAsync(bitmap);

                if (string.IsNullOrWhiteSpace(ocrText))
                {
                    sb.AppendLine("=== Extracted Text (OCR) ===");
                    sb.AppendLine("(No readable text detected on screen)");
                }
                else
                {
                    var trimmed = ocrText.Length > MaxOcrChars
                        ? ocrText[..MaxOcrChars] + "\n[...truncated]"
                        : ocrText;

                    sb.AppendLine("=== Extracted Text (OCR) ===");
                    sb.AppendLine(trimmed);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[OCR error: {ex.GetType().Name}: {ex.Message}]");
                sb.AppendLine("Screen was captured but text extraction failed.");
            }
            finally
            {
                bitmap.Dispose();
            }
        }
        else
        {
            sb.AppendLine("[No bitmap available for OCR]");
        }

        // ── Phase 4: Browser page fetch (if active window is a browser) ─
        if (windowInfo is not null && IsBrowserProcess(windowInfo.ProcessName))
        {
            await TryFetchBrowserPageAsync(sb, windowInfo, ocrText, cancellationToken);
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // MCP Tool: GetActiveWindow
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool, Description(
        "Returns information about the currently active (foreground) window: " +
        "title, process name, and PID. Lightweight alternative to full " +
        "screen capture when you just need to know what app the user is in.")]
    public static string GetActiveWindow()
    {
        try
        {
            var info = GetActiveWindowInfo();
            return $"Title: {info.Title}\n" +
                   $"Process: {info.ProcessName}\n" +
                   $"PID: {info.ProcessId}";
        }
        catch (Exception ex)
        {
            return $"Error getting active window: {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Browser Detection & Page Fetch
    // ─────────────────────────────────────────────────────────────────

    private static bool IsBrowserProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        return BrowserProcessNames.Contains(processName);
    }

    /// <summary>
    /// When the active window is a browser, try to extract a URL from the
    /// OCR text (the address bar is visible on screen) and fetch the
    /// actual page content for a much richer result.
    /// </summary>
    private static async Task TryFetchBrowserPageAsync(
        StringBuilder sb,
        WindowInfo windowInfo,
        string? ocrText,
        CancellationToken cancellationToken)
    {
        sb.AppendLine();

        // Try to extract a URL from the OCR text (address bar is captured)
        var url = TryExtractBrowserUrl(ocrText, windowInfo.Title);
        if (url is null)
        {
            sb.AppendLine("=== Browser Page Content ===");
            sb.AppendLine("(Could not determine the page URL from screen content. " +
                          "The OCR text above is the best available information.)");
            return;
        }

        sb.AppendLine($"Detected URL: {url}");

        try
        {
            var result = await ContentExtractor.ExtractAsync(url, 15, cancellationToken);

            if (result.Error is not null)
            {
                sb.AppendLine($"=== Browser Page Content ===");
                sb.AppendLine($"(Page fetch failed: {result.Error}. Using OCR text above.)");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("=== Browser Page Content (fetched via HTTP) ===");
            sb.AppendLine($"Title: \"{result.Title}\"");

            if (!string.IsNullOrWhiteSpace(result.Author))
                sb.AppendLine($"Author: {result.Author}");

            if (result.PublishedDate.HasValue)
                sb.AppendLine($"Date: {result.PublishedDate.Value:yyyy-MM-dd}");

            sb.AppendLine($"Source: {result.Domain}");
            sb.AppendLine($"Word Count: {result.WordCount:N0}");

            if (!result.IsArticle)
                sb.AppendLine("Extraction: basic (non-article page)");

            sb.AppendLine();
            sb.AppendLine("=== Content ===");
            var content = ContentExtractor.Truncate(result.TextContent, MaxPageChars);
            sb.AppendLine(content);

            sb.AppendLine();
            sb.AppendLine("NOTE: The content above was fetched directly from the web page " +
                          "and is much more accurate than the OCR text. Prefer this content " +
                          "for summarization and answering questions about the page.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"=== Browser Page Content ===");
            sb.AppendLine($"(Page fetch error: {ex.GetType().Name}: {ex.Message}. " +
                          "Using OCR text above.)");
        }
    }

    /// <summary>
    /// Extracts a URL from OCR text or the window title.
    /// The browser address bar is typically captured by OCR as a line
    /// containing https:// near the top of the text.
    /// </summary>
    private static string? TryExtractBrowserUrl(string? ocrText, string? windowTitle)
    {
        // First: look for URLs in the OCR text
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            // Search the first ~1500 chars (address bar is near the top)
            var searchRegion = ocrText.Length > 1500 ? ocrText[..1500] : ocrText;
            var match = UrlPattern.Match(searchRegion);
            if (match.Success)
            {
                var candidate = CleanOcrUrl(match.Value);
                if (IsPlausiblePageUrl(candidate))
                    return candidate;
            }

            // Fallback: search full OCR text
            match = UrlPattern.Match(ocrText);
            if (match.Success)
            {
                var candidate = CleanOcrUrl(match.Value);
                if (IsPlausiblePageUrl(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Cleans up OCR artifacts from a captured URL.
    /// </summary>
    private static string CleanOcrUrl(string raw)
    {
        // Trim trailing punctuation that OCR might attach
        var cleaned = raw.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'', '\u2026');

        // OCR sometimes captures trailing UI text
        var spaceIdx = cleaned.IndexOf(' ');
        if (spaceIdx > 0)
            cleaned = cleaned[..spaceIdx];

        return cleaned;
    }

    /// <summary>
    /// Checks if a URL looks like a real page URL (not an internal/resource URL).
    /// </summary>
    private static bool IsPlausiblePageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // Skip localhost, internal, and resource URLs
        if (uri.Host is "localhost" or "127.0.0.1" or "0.0.0.0")
            return false;

        // Must have a real domain
        if (!uri.Host.Contains('.'))
            return false;

        // Skip obvious non-page resources
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".gif") ||
            path.EndsWith(".css") || path.EndsWith(".js") || path.EndsWith(".ico"))
            return false;

        return true;
    }

    // ─────────────────────────────────────────────────────────────────
    // Screen Capture
    // ─────────────────────────────────────────────────────────────────

    private static Bitmap CaptureRegion(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size);
        return bitmap;
    }

    // ─────────────────────────────────────────────────────────────────
    // OCR via Windows.Media.Ocr
    // ─────────────────────────────────────────────────────────────────

    private static async Task<string> RunOcrAsync(Bitmap bitmap)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            return "(OCR unavailable — no language pack installed)";

        // Convert System.Drawing.Bitmap → BMP bytes → WinRT SoftwareBitmap
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
        var bytes = ms.ToArray();

        var winrtStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(winrtStream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        winrtStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(winrtStream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        var result = await engine.RecognizeAsync(softwareBitmap);
        return result.Text;
    }

    // ─────────────────────────────────────────────────────────────────
    // Active Window Info
    // ─────────────────────────────────────────────────────────────────

    private record WindowInfo(string Title, string ProcessName, int ProcessId, Rectangle Bounds);

    private static WindowInfo GetActiveWindowInfo()
    {
        var hWnd = GetForegroundWindow();

        // Title
        var titleBuf = new StringBuilder(512);
        GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
        var title = titleBuf.Length > 0 ? titleBuf.ToString() : "(untitled)";

        // Process
        GetWindowThreadProcessId(hWnd, out var pid);
        string processName;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch
        {
            processName = "unknown";
        }

        // Window bounds (prefer DWM extended frame for accurate sizing)
        var bounds = Rectangle.Empty;
        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out var dwmRect, Marshal.SizeOf<RECT>()) == 0)
        {
            bounds = new Rectangle(
                dwmRect.Left, dwmRect.Top,
                dwmRect.Right - dwmRect.Left,
                dwmRect.Bottom - dwmRect.Top);
        }
        else if (GetWindowRect(hWnd, out var rect))
        {
            bounds = new Rectangle(
                rect.Left, rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top);
        }

        return new WindowInfo(title, processName, (int)pid, bounds);
    }

    // ─────────────────────────────────────────────────────────────────
    // Screen Dimensions
    // ─────────────────────────────────────────────────────────────────

    private static (int Width, int Height) GetPrimaryScreenSize()
    {
        return (
            GetSystemMetrics(SM_CXSCREEN),
            GetSystemMetrics(SM_CYSCREEN));
    }

    // ─────────────────────────────────────────────────────────────────
    // DPI Awareness (call once before capture)
    // ─────────────────────────────────────────────────────────────────

    private static bool _dpiSet;

    private static void SetProcessDpiAwareness()
    {
        if (_dpiSet) return;
        _dpiSet = true;

        try
        {
            // Per-monitor DPI aware (Windows 8.1+)
            SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
        }
        catch
        {
            try { SetProcessDPIAware(); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Win32 P/Invoke
    // ─────────────────────────────────────────────────────────────────

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDPIAware();

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int awareness);
}
