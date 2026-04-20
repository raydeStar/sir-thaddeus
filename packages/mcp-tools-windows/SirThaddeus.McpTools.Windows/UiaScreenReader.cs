using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using Serilog;

namespace SirThaddeus.McpServer.Tools;

public static class UiaScreenReader
{
    private const int MaxElements = 200;
    private const int MaxStructuredElements = 300;
    private const int MaxTextLength = 6_000;
    private const int MaxDocumentTextLength = 1_500;
    private const int MaxBrowserEditCandidates = 40;

    public static Task<UiaReadResult> ReadForegroundWindowAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<UiaReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(ReadForegroundWindow());
            }
            catch
            {
                tcs.TrySetResult(UiaReadResult.Empty("Accessibility tree unavailable"));
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        }

        return tcs.Task;
    }

    public static UiaReadResult ReadForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return UiaReadResult.Empty("No foreground window");

        var windowTitle = GetWindowTitle(hwnd);
        var processName = GetProcessName(hwnd);

        try
        {
            IUIAutomation automation = new CUIAutomation8Class();
            var root = automation.ElementFromHandle(hwnd);
            if (root is null)
                return UiaReadResult.Empty("Accessibility tree unavailable", windowTitle, processName);

            var sb = new StringBuilder();
            var emittedCount = 0;
            var trueCondition = automation.CreateTrueCondition();
            WalkElement(root, trueCondition, sb, depth: 0, ref emittedCount);

            var text = Truncate(sb.ToString().Trim(), MaxTextLength);
            var browserUrl = TryGetBrowserUrl(automation, root);

            return new UiaReadResult
            {
                WindowTitle = windowTitle,
                ProcessName = processName,
                Text = text,
                Source = string.IsNullOrWhiteSpace(text) ? "context-only" : "uia",
                BrowserUrl = browserUrl,
                FailureReason = string.IsNullOrWhiteSpace(text)
                    ? "Accessibility tree returned no readable content"
                    : null
            };
        }
        catch
        {
            return UiaReadResult.Empty("Accessibility tree unavailable", windowTitle, processName);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Structured read — returns nodes with bounding rectangles
    // for reading-order sorting and chrome/content classification.
    // ═══════════════════════════════════════════════════════════════

    public static Task<UiaStructuredResult> ReadForegroundWindowStructuredAsync(
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<UiaStructuredResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(ReadForegroundWindowStructured());
            }
            catch
            {
                tcs.TrySetResult(UiaStructuredResult.Empty("Accessibility tree unavailable"));
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        return tcs.Task;
    }

    public static UiaStructuredResult ReadForegroundWindowStructured()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return UiaStructuredResult.Empty("No foreground window");

        var windowTitle = GetWindowTitle(hwnd);
        var processName = GetProcessName(hwnd);
        var processId = GetProcessId(hwnd);

        try
        {
            IUIAutomation automation = new CUIAutomation8Class();
            var root = automation.ElementFromHandle(hwnd);
            if (root is null)
                return UiaStructuredResult.Empty("Accessibility tree unavailable",
                    windowTitle, processName, processId);

            var nodes = new List<UiaNode>();
            var trueCondition = automation.CreateTrueCondition();
            WalkElementStructured(root, trueCondition, nodes, depth: 0);

            var browserUrl = TryGetBrowserUrl(automation, root);

            return new UiaStructuredResult
            {
                WindowTitle = windowTitle,
                ProcessName = processName,
                ProcessId = processId,
                BrowserUrl = browserUrl,
                Source = nodes.Count == 0 ? "context-only" : "uia",
                Nodes = nodes,
                FailureReason = nodes.Count == 0
                    ? "Accessibility tree returned no readable content"
                    : null
            };
        }
        catch
        {
            return UiaStructuredResult.Empty("Accessibility tree unavailable",
                windowTitle, processName, processId);
        }
    }

    private static void WalkElementStructured(
        IUIAutomationElement element,
        IUIAutomationCondition trueCondition,
        List<UiaNode> nodes,
        int depth)
    {
        if (nodes.Count >= MaxStructuredElements)
            return;

        try
        {
            var role = SafeGet(() => element.CurrentControlType);
            var name = NormalizeWhitespace(SafeGet(() => element.CurrentName));
            var value = NormalizeWhitespace(TryGetValue(element));
            var className = SafeGet(() => element.CurrentClassName);
            var automationId = SafeGet(() => element.CurrentAutomationId);
            var bounds = SafeGetBounds(element);

            // Emit any element with a recognized role and meaningful content
            if (role != 0)
            {
                nodes.Add(new UiaNode
                {
                    ControlType = role,
                    RoleLabel = RoleLabel(role),
                    Name = name,
                    Value = value,
                    ClassName = className,
                    AutomationId = automationId,
                    BoundsLeft = bounds.Left,
                    BoundsTop = bounds.Top,
                    BoundsRight = bounds.Right,
                    BoundsBottom = bounds.Bottom,
                    Depth = depth,
                });
            }

            if (nodes.Count >= MaxStructuredElements)
                return;

            var children = element.FindAll(TreeScope.TreeScope_Children, trueCondition);
            for (var i = 0; i < children.Length; i++)
            {
                var child = children.GetElement(i);
                WalkElementStructured(child, trueCondition, nodes, depth + 1);
                if (nodes.Count >= MaxStructuredElements)
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            Log.ForContext(typeof(UiaScreenReader))
                .Debug(ex, "UI element disappeared mid-walk (InvalidOperationException)");
        }
        catch (COMException ex)
        {
            Log.ForContext(typeof(UiaScreenReader))
                .Debug(ex, "UIA COM call failed during element walk (hresult={HResult:X8})", ex.HResult);
        }
    }

    private static (int Left, int Top, int Right, int Bottom) SafeGetBounds(IUIAutomationElement element)
    {
        try
        {
            var rect = element.CurrentBoundingRectangle;
            return (rect.left, rect.top, rect.right, rect.bottom);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }

    private static int GetProcessId(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            return (int)pid;
        }
        catch
        {
            return 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Legacy flat-text walk (kept for backward compatibility)
    // ═══════════════════════════════════════════════════════════════

    private static void WalkElement(
        IUIAutomationElement element,
        IUIAutomationCondition trueCondition,
        StringBuilder sb,
        int depth,
        ref int emittedCount)
    {
        if (emittedCount >= MaxElements || sb.Length >= MaxTextLength)
            return;

        try
        {
            var role = SafeGet(() => element.CurrentControlType);
            var name = NormalizeWhitespace(SafeGet(() => element.CurrentName));
            var value = NormalizeWhitespace(TryGetValue(element));

            if (ShouldEmit(role, name, value))
            {
                var prefix = new string(' ', depth * 2);
                sb.Append(prefix);
                sb.Append('[');
                sb.Append(RoleLabel(role));
                sb.Append("] ");

                if (!string.IsNullOrWhiteSpace(name))
                {
                    sb.Append(name);
                    if (!string.IsNullOrWhiteSpace(value) &&
                        !string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(": ");
                        sb.Append(value);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(value))
                {
                    sb.Append(value);
                }

                sb.AppendLine();
                emittedCount++;
            }

            if (emittedCount >= MaxElements || sb.Length >= MaxTextLength)
                return;

            var children = element.FindAll(TreeScope.TreeScope_Children, trueCondition);
            for (var i = 0; i < children.Length; i++)
            {
                var child = children.GetElement(i);
                WalkElement(child, trueCondition, sb, depth + 1, ref emittedCount);
                if (emittedCount >= MaxElements || sb.Length >= MaxTextLength)
                    break;
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }
    }

    private static string? TryGetBrowserUrl(IUIAutomation automation, IUIAutomationElement root)
    {
        try
        {
            var addressBarCondition = automation.CreatePropertyCondition(
                UIA_PropertyIds.UIA_AutomationIdPropertyId,
                "addressEditBox");

            var addressBar = root.FindFirst(
                TreeScope.TreeScope_Descendants,
                addressBarCondition);

            var candidate = TryNormalizeBrowserUrl(ReadElementValue(addressBar));
            if (candidate is not null)
                return candidate;

            var editCondition = automation.CreatePropertyCondition(
                UIA_PropertyIds.UIA_ControlTypePropertyId,
                UIA_ControlTypeIds.UIA_EditControlTypeId);

            var editElements = root.FindAll(TreeScope.TreeScope_Descendants, editCondition);

            for (var i = 0; i < editElements.Length && i < MaxBrowserEditCandidates; i++)
            {
                var element = editElements.GetElement(i);
                var value = ReadElementValue(element);
                var name = NormalizeWhitespace(SafeGet(() => element.CurrentName));
                var automationId = NormalizeWhitespace(SafeGet(() => element.CurrentAutomationId));

                if (!LooksLikeAddressBar(name, automationId, value))
                    continue;

                candidate = TryNormalizeBrowserUrl(value ?? name);
                if (candidate is not null)
                    return candidate;
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }

        return null;
    }

    private static bool LooksLikeAddressBar(string? name, string? automationId, string? value)
    {
        if (ContainsAny(automationId, "address", "addresseditbox", "searchbox"))
            return true;

        if (ContainsAny(name,
                "address and search bar",
                "search or enter web address",
                "address bar",
                "search with google or enter address"))
        {
            return true;
        }

        return TryNormalizeBrowserUrl(value) is not null;
    }

    private static string? ReadElementValue(IUIAutomationElement? element)
    {
        if (element is null)
            return null;

        return NormalizeWhitespace(TryGetValue(element));
    }

    private static string? TryGetValue(IUIAutomationElement element)
    {
        try
        {
            if (element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern valuePattern)
            {
                return valuePattern.CurrentValue;
            }

            if (element.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is IUIAutomationTextPattern textPattern)
            {
                return textPattern.DocumentRange?.GetText(MaxDocumentTextLength);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }
        catch (InvalidCastException)
        {
        }

        return null;
    }

    private static bool ShouldEmit(int role, string? name, string? value)
    {
        if (!IsContentRole(role))
            return false;

        return !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsContentRole(int role)
    {
        return role == UIA_ControlTypeIds.UIA_TextControlTypeId ||
               role == UIA_ControlTypeIds.UIA_EditControlTypeId ||
               role == UIA_ControlTypeIds.UIA_ButtonControlTypeId ||
               role == UIA_ControlTypeIds.UIA_ListItemControlTypeId ||
               role == UIA_ControlTypeIds.UIA_MenuItemControlTypeId ||
               role == UIA_ControlTypeIds.UIA_HyperlinkControlTypeId ||
               role == UIA_ControlTypeIds.UIA_DocumentControlTypeId ||
               role == UIA_ControlTypeIds.UIA_HeaderControlTypeId ||
               role == UIA_ControlTypeIds.UIA_DataItemControlTypeId;
    }

    private static string RoleLabel(int role)
    {
        if (role == UIA_ControlTypeIds.UIA_TextControlTypeId) return "Text";
        if (role == UIA_ControlTypeIds.UIA_EditControlTypeId) return "Edit";
        if (role == UIA_ControlTypeIds.UIA_ButtonControlTypeId) return "Button";
        if (role == UIA_ControlTypeIds.UIA_ListItemControlTypeId) return "ListItem";
        if (role == UIA_ControlTypeIds.UIA_MenuItemControlTypeId) return "MenuItem";
        if (role == UIA_ControlTypeIds.UIA_HyperlinkControlTypeId) return "Link";
        if (role == UIA_ControlTypeIds.UIA_DocumentControlTypeId) return "Document";
        if (role == UIA_ControlTypeIds.UIA_HeaderControlTypeId) return "Header";
        if (role == UIA_ControlTypeIds.UIA_DataItemControlTypeId) return "DataItem";
        return $"ControlType-{role}";
    }

    internal static string? TryNormalizeBrowserUrl(string? raw)
    {
        var candidate = NormalizeWhitespace(raw);
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        candidate = candidate.Trim('"', '\'', '(', ')', '[', ']', '{', '}', '<', '>', ',', ';');

        if (candidate.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            return absoluteUri.ToString();
        }

        if (candidate.Contains(' '))
            return null;

        if (candidate.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            var localhostCandidate = $"http://{candidate}";
            return Uri.TryCreate(localhostCandidate, UriKind.Absolute, out var localhostUri)
                ? localhostUri.ToString()
                : null;
        }

        if (candidate.Contains('.'))
        {
            var httpsCandidate = $"https://{candidate}";
            return Uri.TryCreate(httpsCandidate, UriKind.Absolute, out var httpsUri)
                ? httpsUri.ToString()
                : null;
        }

        return null;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength] + "\n[...truncated]";
    }

    private static string? NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var sb = new StringBuilder(value.Length);
        var lastWasWhitespace = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (lastWasWhitespace)
                    continue;

                sb.Append(' ');
                lastWasWhitespace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasWhitespace = false;
            }
        }

        return sb.ToString();
    }

    private static bool ContainsAny(string? value, params string[] candidates)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var candidate in candidates)
        {
            if (value.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static T? SafeGet<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var titleBuffer = new StringBuilder(512);
        GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);
        return titleBuffer.Length > 0 ? titleBuffer.ToString() : "(untitled)";
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}

public sealed record UiaReadResult
{
    public string WindowTitle { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string Text { get; init; } = "";
    public string Source { get; init; } = "";
    public string? BrowserUrl { get; init; }
    public string? FailureReason { get; init; }
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    public static UiaReadResult Empty(string reason, string windowTitle = "", string processName = "") => new()
    {
        WindowTitle = windowTitle,
        ProcessName = processName,
        Source = "empty",
        FailureReason = reason
    };
}

// ─────────────────────────────────────────────────────────────────────
// Structured result types for the new screen read pipeline
// ─────────────────────────────────────────────────────────────────────

public sealed record UiaNode
{
    public int ControlType { get; init; }
    public string RoleLabel { get; init; } = "";
    public string? Name { get; init; }
    public string? Value { get; init; }
    public string? ClassName { get; init; }
    public string? AutomationId { get; init; }
    public int BoundsLeft { get; init; }
    public int BoundsTop { get; init; }
    public int BoundsRight { get; init; }
    public int BoundsBottom { get; init; }
    public int Depth { get; init; }
}

public sealed record UiaStructuredResult
{
    public string WindowTitle { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public int ProcessId { get; init; }
    public string? BrowserUrl { get; init; }
    public string Source { get; init; } = "";
    public string? FailureReason { get; init; }
    public List<UiaNode> Nodes { get; init; } = new();
    public bool IsEmpty => Nodes.Count == 0;

    public static UiaStructuredResult Empty(
        string reason,
        string windowTitle = "",
        string processName = "",
        int processId = 0) => new()
    {
        WindowTitle = windowTitle,
        ProcessName = processName,
        ProcessId = processId,
        Source = "empty",
        FailureReason = reason
    };
}