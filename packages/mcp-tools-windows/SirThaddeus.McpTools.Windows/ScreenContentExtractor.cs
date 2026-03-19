using System.Text;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// Transforms raw UIA tree data into a structured, human-readable screen read.
/// Separates chrome from content, filters framework noise, sorts by visual
/// reading order, and detects the content type.
/// </summary>
public static class ScreenContentExtractor
{
    private const int MaxReadableContentChars = 2_000;

    // ─── Chrome detection ────────────────────────────────────────────

    private static readonly HashSet<int> ChromeContainerTypes =
    [
        50009,  // Menu
        50010,  // MenuBar
        50014,  // ScrollBar
        50015,  // Slider
        50017,  // StatusBar
        50021,  // ToolBar
        50022,  // ToolTip
        50027,  // Thumb
        50037,  // TitleBar
        50038,  // Separator
    ];

    private static readonly HashSet<string> ChromeButtonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Minimize", "Maximize", "Close", "Restore Down", "Restore",
        "Back", "Forward", "Refresh", "Reload",
        "New Tab", "New tab", "Settings and more",
    };

    // ─── Content type detection ──────────────────────────────────────

    private static readonly Dictionary<string, string> ProcessContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers
        ["chrome"] = "WebPage", ["msedge"] = "WebPage", ["firefox"] = "WebPage",
        ["brave"] = "WebPage", ["opera"] = "WebPage", ["vivaldi"] = "WebPage",
        ["chromium"] = "WebPage", ["ApplicationFrameHost"] = "WebPage",
        // Code editors
        ["Code"] = "Code", ["devenv"] = "Code", ["notepad++"] = "Code",
        ["cursor"] = "Code", ["sublime_text"] = "Code",
        // Documents
        ["WINWORD"] = "Document", ["EXCEL"] = "Document",
        ["AcroRd32"] = "Document", ["Acrobat"] = "Document",
        ["FoxitPDFReader"] = "Document", ["POWERPNT"] = "Document",
        ["notepad"] = "Document", ["wordpad"] = "Document",
        // Terminals
        ["WindowsTerminal"] = "Terminal", ["cmd"] = "Terminal",
        ["powershell"] = "Terminal", ["pwsh"] = "Terminal",
        ["ConEmuC64"] = "Terminal", ["ConEmu"] = "Terminal",
        // Math
        ["Calculator"] = "Math", ["calc"] = "Math",
    };

    private static readonly HashSet<string> SelfProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SirThaddeus", "SirThaddeus.UI.Avalonia", "SirThaddeus.Desktop",
    };

    // ─── Framework noise patterns ────────────────────────────────────

    private static readonly string[] FrameworkNamePrefixes =
    [
        "Avalonia.Controls.", "Avalonia.Layout.", "Avalonia.Media.",
        "Avalonia.Visual", "System.Windows.", "Microsoft.UI.",
        "Windows.UI.Xaml.", "ContentPresenter", "ItemsPresenter",
    ];

    // ═════════════════════════════════════════════════════════════════
    // Public API
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build a structured screen read from UIA nodes.
    /// </summary>
    public static ScreenReadResult Extract(
        string windowTitle,
        string processName,
        int processId,
        IReadOnlyList<UiaNode> nodes,
        string? browserUrl,
        string? browserPageContent)
    {
        var contentType = DetectContentType(processName, windowTitle);
        var result = new ScreenReadResult
        {
            ContentType = contentType,
            WindowContext = BuildWindowContext(windowTitle, processName, processId, browserUrl, contentType),
        };

        // Self-detection — brief summary, don't recursively describe own UI
        if (contentType == "Self")
        {
            result.ReadableContent = "Sir Thaddeus's own application window is in the foreground.";
            return result;
        }

        // Edge cases
        if (IsLockScreen(windowTitle, processName))
        {
            result.ContentType = "System";
            result.WindowContext = "Login/Lock Screen";
            result.ReadableContent = "A login or lock screen is visible. Credential fields are excluded for security.";
            result.Limitations = "Credential fields excluded.";
            return result;
        }

        // If we have fetched browser page content, prefer that over UIA text
        if (!string.IsNullOrWhiteSpace(browserPageContent))
        {
            result.ReadableContent = Truncate(browserPageContent);
            if (nodes.Count == 0)
                result.Limitations = "Content was fetched directly from the web page URL, not from the visible screen.";
            return result;
        }

        // Separate content from chrome and sort by reading order
        var contentNodes = FilterAndSort(nodes);

        if (contentNodes.Count == 0)
        {
            result.Limitations = nodes.Count == 0
                ? "No UI elements could be read from the accessibility tree."
                : "The screen contains primarily visual content (images/canvas) that couldn't be extracted as text. A screenshot would help.";
            return result;
        }

        // Build readable text
        var sb = new StringBuilder();
        var actionNames = new List<string>();
        var linkNames = new List<string>();

        foreach (var node in contentNodes)
        {
            if (sb.Length >= MaxReadableContentChars)
            {
                sb.AppendLine();
                sb.Append($"[Content truncated — showing first ~{MaxReadableContentChars} characters of visible text]");
                break;
            }

            AppendNodeText(sb, node, contentType);

            // Collect actions for summary
            if (node.RoleLabel is "Button" && !string.IsNullOrWhiteSpace(node.Name))
                actionNames.Add(node.Name);
            else if (node.RoleLabel is "Link" && !string.IsNullOrWhiteSpace(node.Name))
                linkNames.Add(node.Name);
        }

        result.ReadableContent = sb.ToString().Trim();

        // Actions summary
        result.AvailableActions = BuildActionSummary(actionNames, linkNames);

        return result;
    }

    /// <summary>
    /// Build a screen read from OCR fallback text.
    /// </summary>
    public static ScreenReadResult ExtractFromOcr(
        string windowTitle,
        string processName,
        int processId,
        string? ocrText,
        string? browserUrl)
    {
        var contentType = DetectContentType(processName, windowTitle);
        var result = new ScreenReadResult
        {
            ContentType = contentType,
            WindowContext = BuildWindowContext(windowTitle, processName, processId, browserUrl, contentType),
        };

        if (string.IsNullOrWhiteSpace(ocrText))
        {
            result.Limitations = "No readable text detected via OCR.";
            return result;
        }

        result.ReadableContent = Truncate(ocrText);
        result.Limitations = "Content was extracted via OCR and may contain recognition errors.";
        return result;
    }

    /// <summary>
    /// Build a screen read when no window is focused (desktop visible).
    /// </summary>
    public static ScreenReadResult EmptyDesktop() => new()
    {
        ContentType = "System",
        WindowContext = "Desktop",
        ReadableContent = "No application window is currently focused. The desktop is visible.",
    };

    // ═════════════════════════════════════════════════════════════════
    // Content type detection
    // ═════════════════════════════════════════════════════════════════

    internal static string DetectContentType(string processName, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(processName) && string.IsNullOrWhiteSpace(windowTitle))
            return "Unknown";

        if (SelfProcessNames.Contains(processName))
            return "Self";

        if (ProcessContentTypes.TryGetValue(processName, out var type))
            return type;

        // Title-based fallbacks
        var lowerTitle = windowTitle.ToLowerInvariant();
        if (lowerTitle.Contains("- visual studio code") || lowerTitle.Contains("- cursor"))
            return "Code";
        if (lowerTitle.Contains(".pdf") || lowerTitle.Contains("- word") || lowerTitle.Contains("- document"))
            return "Document";
        if (lowerTitle.EndsWith("calculator", StringComparison.OrdinalIgnoreCase))
            return "Math";

        return "Unknown";
    }

    // ═════════════════════════════════════════════════════════════════
    // Internals
    // ═════════════════════════════════════════════════════════════════

    private static string BuildWindowContext(
        string windowTitle, string processName, int processId,
        string? browserUrl, string contentType)
    {
        return contentType switch
        {
            "WebPage" when !string.IsNullOrWhiteSpace(browserUrl)
                => $"{processName} — \"{windowTitle}\" ({browserUrl})",
            "WebPage" or "Code" or "Terminal"
                => $"{processName} — \"{windowTitle}\"",
            _   => $"\"{windowTitle}\" ({processName}, PID {processId})"
        };
    }

    private static List<UiaNode> FilterAndSort(IReadOnlyList<UiaNode> nodes)
    {
        var content = new List<UiaNode>(nodes.Count);
        foreach (var node in nodes)
        {
            if (IsChrome(node))
                continue;
            if (IsFrameworkNoise(node))
                continue;
            if (!HasMeaningfulContent(node))
                continue;
            content.Add(node);
        }

        // Sort by visual reading order: top-to-bottom (row bands), then left-to-right
        content.Sort((a, b) =>
        {
            var rowA = a.BoundsTop / 20;
            var rowB = b.BoundsTop / 20;
            if (rowA != rowB) return rowA.CompareTo(rowB);
            return a.BoundsLeft.CompareTo(b.BoundsLeft);
        });

        return content;
    }

    private static bool IsChrome(UiaNode node)
    {
        if (ChromeContainerTypes.Contains(node.ControlType))
            return true;

        if (node.RoleLabel is "Button" && !string.IsNullOrWhiteSpace(node.Name) &&
            ChromeButtonNames.Contains(node.Name))
            return true;

        return false;
    }

    private static bool IsFrameworkNoise(UiaNode node)
    {
        // Name looks like a framework type name — e.g. "Avalonia.Controls.StackPanel"
        var name = node.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var prefix in FrameworkNamePrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
        }

        // ClassName is a framework container with no meaningful text
        var className = node.ClassName;
        if (!string.IsNullOrWhiteSpace(className))
        {
            foreach (var prefix in FrameworkNamePrefixes)
            {
                if (className.StartsWith(prefix, StringComparison.Ordinal))
                {
                    // If Name is purely a class name too, it's noise
                    if (string.IsNullOrWhiteSpace(name) || name == className)
                        return true;
                }
            }
        }

        return false;
    }

    private static bool HasMeaningfulContent(UiaNode node) =>
        !string.IsNullOrWhiteSpace(node.Name) || !string.IsNullOrWhiteSpace(node.Value);

    private static void AppendNodeText(StringBuilder sb, UiaNode node, string contentType)
    {
        var name = node.Name?.Trim();
        var value = node.Value?.Trim();

        // Build text: prefer value for editable fields, name otherwise
        string text;
        if (!string.IsNullOrWhiteSpace(value) &&
            !string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
        {
            text = !string.IsNullOrWhiteSpace(name) ? $"{name}: {value}" : value;
        }
        else
        {
            text = name ?? value ?? "";
        }

        if (string.IsNullOrWhiteSpace(text))
            return;

        // For code/terminal content, preserve raw text from edit/document fields
        if (contentType is "Code" or "Terminal" && node.RoleLabel is "Edit" or "Document")
        {
            sb.AppendLine(text);
            return;
        }

        switch (node.RoleLabel)
        {
            case "Header":
                sb.AppendLine($"## {text}");
                break;
            case "Link":
                sb.AppendLine($"[Link] {text}");
                break;
            case "Edit":
                sb.AppendLine($"[Input] {text}");
                break;
            case "Button":
                // Don't emit button labels in content — they go to actions summary
                break;
            default:
                sb.AppendLine(text);
                break;
        }
    }

    private static string BuildActionSummary(List<string> buttons, List<string> links)
    {
        var parts = new List<string>();
        var distinctButtons = buttons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var distinctLinks = links.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (distinctButtons.Count > 0)
        {
            var preview = string.Join(", ", distinctButtons.Take(5));
            parts.Add(distinctButtons.Count > 5
                ? $"{distinctButtons.Count} buttons ({preview}, ...)"
                : $"{distinctButtons.Count} buttons ({preview})");
        }

        if (distinctLinks.Count > 0)
        {
            var preview = string.Join(", ", distinctLinks.Take(5));
            parts.Add(distinctLinks.Count > 5
                ? $"{distinctLinks.Count} links ({preview}, ...)"
                : $"{distinctLinks.Count} links ({preview})");
        }

        return string.Join("; ", parts);
    }

    private static bool IsLockScreen(string windowTitle, string processName) =>
        processName.Equals("LogonUI", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("LockApp", StringComparison.OrdinalIgnoreCase) ||
        windowTitle.Contains("Windows Default Lock Screen", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxReadableContentChars)
            return text;

        return text[..MaxReadableContentChars].TrimEnd() +
               $"\n[Content truncated — showing first ~{MaxReadableContentChars} characters of visible text]";
    }
}
