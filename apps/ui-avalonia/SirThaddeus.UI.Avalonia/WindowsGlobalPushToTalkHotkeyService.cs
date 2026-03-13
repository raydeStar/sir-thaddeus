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

    private const uint LeftCtrl = 0xA2;
    private const uint RightCtrl = 0xA3;
    private const uint Ctrl = 0x11;
    private const uint LeftShift = 0xA0;
    private const uint RightShift = 0xA1;
    private const uint Shift = 0x10;
    private const uint LeftAlt = 0xA4;
    private const uint RightAlt = 0xA5;
    private const uint Alt = 0x12;
    private const uint LeftWin = 0x5B;
    private const uint RightWin = 0x5C;

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
    private readonly KeyChord _pttChord;
    private readonly KeyChord _cancelChord;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookId;
    private bool _bindingActive;
    private bool _cancelBindingLatched;
    private bool _disposed;

    public WindowsGlobalPushToTalkHotkeyService(
        string? pttChord = "Ctrl+Alt+M",
        string? cancelChord = "Ctrl+Alt+Escape")
    {
        _pttChord = ParseChord(pttChord, new KeyChord("Ctrl+Alt+M", 0x4D, KeyModifiers.Control | KeyModifiers.Alt));
        _cancelChord = ParseChord(cancelChord, new KeyChord("Ctrl+Alt+Escape", 0x1B, KeyModifiers.Control | KeyModifiers.Alt));
    }

    public string BindingText => _pttChord.RawText;

    public string CancelBindingText => _cancelChord.RawText;

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
                if (!_bindingActive && MatchesChordOnKeyDown(vkCode, _pttChord))
                {
                    _bindingActive = true;
                    TryRaise(Pressed);
                }

                if (!_cancelBindingLatched && MatchesChordOnKeyDown(vkCode, _cancelChord))
                {
                    _cancelBindingLatched = true;
                    TryRaise(CancelRequested);
                }
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                _pressedKeys.Remove(vkCode);
                if (_bindingActive && !IsChordHeld(_pttChord))
                {
                    _bindingActive = false;
                    TryRaise(Released);
                }

                if (vkCode == _cancelChord.TriggerKey)
                {
                    _cancelBindingLatched = false;
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool MatchesChordOnKeyDown(uint vkCode, KeyChord chord)
    {
        return vkCode == chord.TriggerKey && AreModifiersPressed(chord.Modifiers);
    }

    private bool IsChordHeld(KeyChord chord)
    {
        return IsPressed(chord.TriggerKey) && AreModifiersPressed(chord.Modifiers);
    }

    private bool AreModifiersPressed(KeyModifiers required)
    {
        if (required.HasFlag(KeyModifiers.Control) &&
            !(IsPressed(LeftCtrl) || IsPressed(RightCtrl) || IsPressed(Ctrl)))
        {
            return false;
        }

        if (required.HasFlag(KeyModifiers.Shift) &&
            !(IsPressed(LeftShift) || IsPressed(RightShift) || IsPressed(Shift)))
        {
            return false;
        }

        if (required.HasFlag(KeyModifiers.Alt) &&
            !(IsPressed(LeftAlt) || IsPressed(RightAlt) || IsPressed(Alt)))
        {
            return false;
        }

        if (required.HasFlag(KeyModifiers.Win) && !(IsPressed(LeftWin) || IsPressed(RightWin)))
        {
            return false;
        }

        return true;
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

    private static KeyChord ParseChord(string? text, KeyChord fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var raw = text.Trim();
        var parts = raw.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !TryParseVirtualKey(parts[^1], out var trigger))
        {
            return fallback;
        }

        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var token = parts[i];
            if (token.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Control;
            }
            else if (token.Equals("shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Shift;
            }
            else if (token.Equals("alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Alt;
            }
            else if (token.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("meta", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Win;
            }
        }

        return new KeyChord(raw, trigger, modifiers);
    }

    private static bool TryParseVirtualKey(string? keyText, out uint vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(keyText))
        {
            return false;
        }

        var key = keyText.Trim();

        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            vk = hex;
            return true;
        }

        if ((key.StartsWith('F') || key.StartsWith('f')) &&
            int.TryParse(key[1..], out var fn) &&
            fn is >= 1 and <= 24)
        {
            vk = (uint)(0x6F + fn);
            return true;
        }

        if (key.Equals("space", StringComparison.OrdinalIgnoreCase))
        {
            vk = 0x20;
            return true;
        }

        if (key.Equals("escape", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("esc", StringComparison.OrdinalIgnoreCase))
        {
            vk = 0x1B;
            return true;
        }

        if (key.Equals("enter", StringComparison.OrdinalIgnoreCase))
        {
            vk = 0x0D;
            return true;
        }

        if (key.Equals("tab", StringComparison.OrdinalIgnoreCase))
        {
            vk = 0x09;
            return true;
        }

        if (key.Equals("backspace", StringComparison.OrdinalIgnoreCase))
        {
            vk = 0x08;
            return true;
        }

        if (key.Length == 1)
        {
            var c = key[0];
            if (char.IsLetter(c))
            {
                vk = (uint)char.ToUpperInvariant(c);
                return true;
            }

            if (char.IsDigit(c))
            {
                vk = (uint)c;
                return true;
            }
        }

        return false;
    }

    [Flags]
    private enum KeyModifiers
    {
        None = 0,
        Control = 1 << 0,
        Shift = 1 << 1,
        Alt = 1 << 2,
        Win = 1 << 3
    }

    private sealed record KeyChord(string RawText, uint TriggerKey, KeyModifiers Modifiers);
}
