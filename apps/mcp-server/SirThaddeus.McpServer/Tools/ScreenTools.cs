using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
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
// These are observation-only tools — they read the screen but never
// modify it. All results are text; no raw image data crosses the wire.
//
// Bounds:
//   - Capture limited to primary monitor (or active window rect).
//   - OCR text capped at 8 000 characters.
//   - Single capture per call, no video/streaming.
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class ScreenTools
{
    private const int MaxOcrChars = 8_000;

    // ═══════════════════════════════════════════════════════════════════
    // MCP Tool: ScreenCapture
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool, Description(
        "Captures a screenshot and extracts all visible text using OCR. " +
        "Returns the active window info, screen resolution, and readable " +
        "text from the display. Use when the user asks about what's on " +
        "their screen, needs help with something visible, or wants you " +
        "to analyze the current display.")]
    public static async Task<string> ScreenCapture(
        [Description("'full_screen' or 'active_window'")] string target = "full_screen",
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
        sb.AppendLine();
        if (bitmap != null)
        {
            try
            {
                var ocrText = await RunOcrAsync(bitmap);

                if (string.IsNullOrWhiteSpace(ocrText))
                {
                    sb.AppendLine("=== Extracted Text ===");
                    sb.AppendLine("(No readable text detected on screen)");
                }
                else
                {
                    var trimmed = ocrText.Length > MaxOcrChars
                        ? ocrText[..MaxOcrChars] + "\n[...truncated]"
                        : ocrText;

                    sb.AppendLine("=== Extracted Text ===");
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
