using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thaddeus.Shell.Platform.Windows;

/// <summary>
/// Windows tray adapter backed by a hidden native window and Shell_NotifyIcon.
/// It keeps the shell alive in the notification area and exposes a small popup menu.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayAdapter : ITrayAdapter
{
    private const uint WM_APP = 0x8000;
    private const uint WM_NULL = 0x0000;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_TRAYICON = WM_APP + 1;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int IconId = 1;
    private const int BaseMenuId = 1000;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(in WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(in MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly ILogger<WindowsTrayAdapter> _logger;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _pumpThread;
    private readonly WndProc _windowProc;
    private readonly string _className = "ThaddeusTrayWindow_" + Guid.NewGuid().ToString("N");
    private IntPtr _windowHandle;
    private bool _disposed;
    private bool _isOperational;
    private TrayMenu _menu = new(Array.Empty<TrayMenuItem>());

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    public WindowsTrayAdapter(ILogger<WindowsTrayAdapter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _windowProc = WindowProcedure;
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "Thaddeus.TrayPump",
        };
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();
        _ready.Wait();
    }

    public bool IsSupported => _isOperational;

    public Task InitializeAsync(TrayMenu menu, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(menu);
        Volatile.Write(ref _menu, menu);

        if (_isOperational)
        {
            UpdateTooltip();
            _logger.LogInformation("tray.windows.initialized items={Count}", menu.Items.Count);
        }
        else
        {
            _logger.LogWarning("tray.windows.unavailable items={Count}", menu.Items.Count);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_windowHandle != IntPtr.Zero)
        {
            PostMessage(_windowHandle, WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
        }

        try { _pumpThread.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _ready.Dispose();
        return ValueTask.CompletedTask;
    }

    private void PumpLoop()
    {
        try
        {
            var instance = GetModuleHandle(null);
            var icon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
            var cls = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                hInstance = instance,
                hIcon = icon,
                hIconSm = icon,
                lpszClassName = _className,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            };

            if (RegisterClassEx(in cls) == 0)
            {
                _logger.LogWarning("tray.windows.class_register_failed error={Error}", Marshal.GetLastWin32Error());
                _isOperational = false;
                return;
            }

            _windowHandle = CreateWindowEx(
                0,
                _className,
                "Sir Thaddeus Tray",
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);

            if (_windowHandle == IntPtr.Zero)
            {
                _logger.LogWarning("tray.windows.window_create_failed error={Error}", Marshal.GetLastWin32Error());
                _isOperational = false;
                return;
            }

            AddIcon(icon);
            _isOperational = true;

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(in msg);
                DispatchMessage(in msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tray.windows.pump_failed");
            _isOperational = false;
        }
        finally
        {
            _ready.Set();
            RemoveIcon();
            if (_windowHandle != IntPtr.Zero)
            {
                try { DestroyWindow(_windowHandle); } catch { /* best effort */ }
                _windowHandle = IntPtr.Zero;
            }
        }
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                HandleTrayMessage((uint)lParam.ToInt64());
                return IntPtr.Zero;
            case WM_DESTROY:
                RemoveIcon();
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private void HandleTrayMessage(uint message)
    {
        switch (message)
        {
            case WM_LBUTTONUP:
                InvokeDefaultAction();
                break;
            case WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }
    }

    private void InvokeDefaultAction()
    {
        var menu = Volatile.Read(ref _menu);
        if (menu.Items.Count == 0)
        {
            return;
        }

        InvokeMenuItem(menu.Items[0]);
    }

    private void ShowContextMenu()
    {
        var snapshot = Volatile.Read(ref _menu);
        if (snapshot.Items.Count == 0 || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        var popup = CreatePopupMenu();
        if (popup == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (var i = 0; i < snapshot.Items.Count; i++)
            {
                AppendMenu(popup, MF_STRING, (nuint)(BaseMenuId + i), snapshot.Items[i].Label);
            }

            if (!GetCursorPos(out var pt))
            {
                return;
            }

            SetForegroundWindow(_windowHandle);
            var selected = TrackPopupMenu(
                popup,
                TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD | TPM_RIGHTBUTTON,
                pt.X,
                pt.Y,
                0,
                _windowHandle,
                IntPtr.Zero);

            if (selected >= BaseMenuId)
            {
                var index = (int)selected - BaseMenuId;
                if (index >= 0 && index < snapshot.Items.Count)
                {
                    InvokeMenuItem(snapshot.Items[index]);
                }
            }
        }
        finally
        {
            DestroyMenu(popup);
            PostMessage(_windowHandle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void InvokeMenuItem(TrayMenuItem item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await item.Invoke().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "tray.windows.menu_item_failed id={Id}", item.Id);
            }
        });
    }

    private void AddIcon(IntPtr icon)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData(icon);
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            _logger.LogWarning("tray.windows.icon_add_failed error={Error}", Marshal.GetLastWin32Error());
            _isOperational = false;
        }
    }

    private void UpdateTooltip()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData(LoadIcon(IntPtr.Zero, IDI_APPLICATION));
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void RemoveIcon()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData(IntPtr.Zero);
        Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    private NOTIFYICONDATA CreateNotifyIconData(IntPtr icon) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _windowHandle,
        uID = IconId,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAYICON,
        hIcon = icon,
        szTip = "Sir Thaddeus",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };
}