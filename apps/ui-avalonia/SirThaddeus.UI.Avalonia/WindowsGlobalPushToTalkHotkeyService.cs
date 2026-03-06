using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SirThaddeus.UI.Avalonia;

internal sealed class WindowsGlobalPushToTalkHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const uint TriggerKeyM = 0x4D;
    private const uint CancelKeyEscape = 0x1B;
    private const uint LeftCtrl = 0xA2;
    private const uint RightCtrl = 0xA3;
    private const uint Ctrl = 0x11;
    private const uint LeftAlt = 0xA4;
    private const uint RightAlt = 0xA5;
    private const uint Alt = 0x12;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly HashSet<uint> _pressedKeys = [];
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookId;
    private bool _bindingActive;
    private bool _cancelBindingLatched;
    private bool _disposed;

    public string BindingText => "Ctrl+Alt+M";

    public string CancelBindingText => "Ctrl+Alt+Esc";

    public bool IsRunning => _hookId != IntPtr.Zero;

    public string? FailureReason { get; private set; }

    public event Action? Pressed;

    public event Action? Released;

    public event Action? CancelRequested;

    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!OperatingSystem.IsWindows())
        {
            FailureReason = "Global PTT is only available on Windows.";
            return false;
        }

        if (_hookId != IntPtr.Zero)
        {
            return true;
        }

        _hookProc = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        _hookId = SetWindowsHookEx(WhKeyboardLl, _hookProc, moduleHandle, 0);
        if (_hookId == IntPtr.Zero)
        {
            FailureReason = $"Global hotkey hook failed (Win32 {Marshal.GetLastWin32Error()}).";
            return false;
        }

        FailureReason = null;
        return true;
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _bindingActive = false;
        _cancelBindingLatched = false;
        _pressedKeys.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            var vkCode = (uint)Marshal.ReadInt32(lParam);

            if (message is WmKeyDown or WmSysKeyDown)
            {
                _pressedKeys.Add(vkCode);
                if (!_bindingActive && vkCode == TriggerKeyM && AreModifiersPressed())
                {
                    _bindingActive = true;
                    TryRaise(Pressed);
                }

                if (!_cancelBindingLatched && vkCode == CancelKeyEscape && AreModifiersPressed())
                {
                    _cancelBindingLatched = true;
                    TryRaise(CancelRequested);
                }
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                _pressedKeys.Remove(vkCode);
                if (_bindingActive && !IsBindingHeld())
                {
                    _bindingActive = false;
                    TryRaise(Released);
                }

                if (vkCode == CancelKeyEscape)
                {
                    _cancelBindingLatched = false;
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool AreModifiersPressed()
    {
        return (IsPressed(LeftCtrl) || IsPressed(RightCtrl) || IsPressed(Ctrl))
            && (IsPressed(LeftAlt) || IsPressed(RightAlt) || IsPressed(Alt));
    }

    private bool IsBindingHeld()
    {
        return IsPressed(TriggerKeyM) && AreModifiersPressed();
    }

    private bool IsPressed(uint vkCode)
    {
        return _pressedKeys.Contains(vkCode);
    }

    private static void TryRaise(Action? callback)
    {
        try
        {
            callback?.Invoke();
        }
        catch
        {
            // Global hook callbacks must remain non-fatal.
        }
    }
}
