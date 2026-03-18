using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ModelContextProtocol.Server;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Screen Observation Tools
//
// Reads the active screen using a layered strategy:
//   1. Accessibility tree (UI Automation)
//   2. Active window context (title/process metadata)
//   3. Browser URL from UI Automation address bar
//   4. HTTP page extraction for browser windows
//   5. OCR fallback for unsupported windows
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

    // ═══════════════════════════════════════════════════════════════════
    // MCP Tool: ScreenCapture
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool, Description(
        "Captures the user's current screen context with a layered reader. " +
        "It first tries the active window accessibility tree, then browser " +
        "page extraction when a browser URL can be read from the address " +
        "bar, and only falls back to OCR when structured UI data is not " +
        "available. Use this when the user asks what is on their screen, " +
        "asks to summarize a page, or wants you to read what they are " +
        "looking at.")]
    public static async Task<string> ScreenCapture(
        [Description(
            "'active_window' (default) or 'full_screen'. Use 'full_screen' " +
            "only when the user explicitly asks about the whole monitor."
        )] string target = "active_window",
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Screen Report ===");

        WindowInfo? windowInfo = null;
        int screenW = 0, screenH = 0;
        try
        {
            SetProcessDpiAwareness();
            windowInfo = GetActiveWindowInfo();
            (screenW, screenH) = GetPrimaryScreenSize();
        }
        catch
        {
        }

        AppendWindowContext(sb, windowInfo, screenW, screenH);

        UiaReadResult? uiaResult = null;
        try
        {
            uiaResult = await UiaScreenReader.ReadForegroundWindowAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            uiaResult = UiaReadResult.Empty("Accessibility tree unavailable");
        }

        var browserProcessName = uiaResult?.ProcessName ?? windowInfo?.ProcessName;
        var isBrowser = IsBrowserProcess(browserProcessName);

        if (uiaResult is { IsEmpty: false })
        {
            sb.AppendLine();
            sb.AppendLine("Source: Accessibility Tree");
            sb.AppendLine();
            sb.AppendLine(uiaResult.Text);

            if (isBrowser && !string.IsNullOrWhiteSpace(uiaResult.BrowserUrl))
            {
                await TryFetchBrowserPageAsync(sb, uiaResult.BrowserUrl!, cancellationToken, "Accessibility Tree URL");
            }
            else if (isBrowser)
            {
                sb.AppendLine();
                sb.AppendLine("=== Browser Page Content ===");
                sb.AppendLine("(Browser detected, but the address bar could not be read from the accessibility tree.)");
            }

            return sb.ToString();
        }

        var browserFetchSucceeded = false;
        if (isBrowser && !string.IsNullOrWhiteSpace(uiaResult?.BrowserUrl))
        {
            browserFetchSucceeded = await TryFetchBrowserPageAsync(
                sb,
                uiaResult.BrowserUrl!,
                cancellationToken,
                "Accessibility Tree URL");

            if (browserFetchSucceeded)
                return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine("Source: Active Window Context");
        if (!string.IsNullOrWhiteSpace(uiaResult?.FailureReason))
            sb.AppendLine($"Accessibility: {uiaResult.FailureReason}");

        await AppendOcrFallbackAsync(sb, target, windowInfo, screenW, screenH).ConfigureAwait(false);
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

    private static void AppendWindowContext(StringBuilder sb, WindowInfo? windowInfo, int screenW, int screenH)
    {
        if (windowInfo is not null)
        {
            sb.AppendLine($"Window: \"{windowInfo.Title}\"");
            sb.AppendLine($"Process: {windowInfo.ProcessName} (PID {windowInfo.ProcessId})");
        }
        else
        {
            sb.AppendLine("Window: (unavailable)");
            sb.AppendLine("Process: (unavailable)");
        }

        if (screenW > 0 && screenH > 0)
            sb.AppendLine($"Screen: {screenW}x{screenH}");
    }

    private static async Task AppendOcrFallbackAsync(
        StringBuilder sb,
        string target,
        WindowInfo? windowInfo,
        int screenW,
        int screenH)
    {
        Bitmap? bitmap = null;
        try
        {
            var captureRect = ResolveCaptureRect(target, windowInfo?.Bounds, screenW, screenH);
            bitmap = CaptureRegion(captureRect);

            sb.AppendLine();
            sb.AppendLine("Source: OCR Fallback");
            sb.AppendLine($"Captured Region: {captureRect.Width}x{captureRect.Height} ({target})");
            sb.AppendLine();
            sb.AppendLine("=== OCR Text ===");

            var ocrText = await RunOcrAsync(bitmap).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ocrText))
            {
                sb.AppendLine("No readable text detected.");
                return;
            }

            var trimmed = ocrText.Length > MaxOcrChars
                ? ocrText[..MaxOcrChars] + "\n[...truncated]"
                : ocrText;

            sb.AppendLine(trimmed);
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("Source: OCR Fallback");
            sb.AppendLine($"(Screen text extraction failed: {ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    internal static Rectangle ResolveCaptureRect(string target, Rectangle? windowBounds, int screenW, int screenH)
    {
        if (target.Equals("full_screen", StringComparison.OrdinalIgnoreCase))
        {
            return new Rectangle(0, 0,
                screenW > 0 ? screenW : 1920,
                screenH > 0 ? screenH : 1080);
        }

        if (windowBounds is { Width: > 0, Height: > 0 } bounds)
            return bounds;

        return new Rectangle(0, 0,
            screenW > 0 ? screenW : 1920,
            screenH > 0 ? screenH : 1080);
    }

    private static async Task<bool> TryFetchBrowserPageAsync(
        StringBuilder sb,
        string url,
        CancellationToken cancellationToken,
        string sourceLabel)
    {
        sb.AppendLine();
        sb.AppendLine("=== Browser Page Content ===");
        sb.AppendLine($"URL Source: {sourceLabel}");
        sb.AppendLine($"Detected URL: {url}");

        try
        {
            var result = await ContentExtractor.ExtractAsync(url, 15, cancellationToken);

            if (result.Error is not null)
            {
                sb.AppendLine($"(Page fetch failed: {result.Error}.)");
                return false;
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
                          "and is much more accurate than OCR text. Prefer this content " +
                          "for summarization and answering questions about the page.");
            return true;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(Page fetch error: {ex.GetType().Name}: {ex.Message}.)");
            return false;
        }
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
