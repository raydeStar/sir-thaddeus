using System.Runtime.InteropServices;
using SirThaddeus.AuditLog;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Input-only push-to-talk service.
/// Emits MicDown/MicUp/Shutup events and does not own recording,
/// playback, or orchestration state transitions.
/// </summary>
public sealed class PushToTalkService : IDisposable
{
    // ─────────────────────────────────────────────────────────────────────
    // Win32 Interop for Low-Level Keyboard Hook
    // ─────────────────────────────────────────────────────────────────────

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    // ─────────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────────

    private readonly IAuditLogger _auditLogger;
    private readonly uint _legacyPttKey;
    private readonly KeyChord? _pttChord;
    private readonly KeyChord? _shutupChord;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;
    private bool _isListening;
    private bool _shutupLatched;
    private readonly HashSet<uint> _pressedKeys = [];
    private bool _disposed;

    /// <summary>
    /// Raised when the configured push-to-talk input is pressed.
    /// </summary>
    public event Action? MicDown;

    /// <summary>
    /// Raised when the configured push-to-talk input is released.
    /// </summary>
    public event Action? MicUp;

    /// <summary>
    /// Raised when the configured "shutup" chord is pressed.
    /// </summary>
    public event Action? Shutup;

    /// <summary>
    /// Creates a new push-to-talk service.
    /// </summary>
    /// <param name="auditLogger">Audit logger for recording events.</param>
    /// <param name="legacyPttKey">Legacy single-key PTT binding (for backwards compatibility).</param>
    /// <param name="pttChord">Optional chord PTT binding (e.g. Ctrl+Shift+Space).</param>
    /// <param name="shutupChord">Optional chord for immediate cancellation.</param>
    public PushToTalkService(
        IAuditLogger auditLogger,
        string legacyPttKey = "F13",
        string? pttChord = "Ctrl+Shift+Space",
        string? shutupChord = "Ctrl+Shift+Escape")
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _legacyPttKey = ParseVirtualKeyOrDefault(legacyPttKey, 0x7C);
        _pttChord = ParseChord(pttChord);
        _shutupChord = ParseChord(shutupChord);
    }

    /// <summary>
    /// Starts the PTT keyboard hook.
    /// </summary>
    public bool Start()
    {
        if (_hookId != IntPtr.Zero)
            return true; // Already started

        _hookProc = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        
        _hookId = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            _hookProc,
            GetModuleHandle(curModule.ModuleName!),
            0);

        if (_hookId == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _auditLogger.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "PTT_HOOK_FAILED",
                Result = "failed",
                Details = new Dictionary<string, object>
                {
                    ["error"] = error
                }
            });
            return false;
        }

        _auditLogger.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "PTT_ENABLED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["legacyPttKey"] = $"0x{_legacyPttKey:X2}",
                ["pttChord"] = _pttChord?.RawText ?? "",
                ["shutupChord"] = _shutupChord?.RawText ?? ""
            }
        });

        return true;
    }

    /// <summary>
    /// Stops the PTT keyboard hook and any active recording.
    /// </summary>
    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        if (_isListening)
        {
            _isListening = false;
        }

        _auditLogger.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "PTT_DISABLED",
            Result = "ok"
        });
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vkCode = (uint)Marshal.ReadInt32(lParam);
            var message = (int)wParam;

            if (message == WM_KEYDOWN)
            {
                _pressedKeys.Add(vkCode);
                HandleKeyDown(vkCode);
            }
            else if (message == WM_KEYUP)
            {
                HandleKeyUp(vkCode);
                _pressedKeys.Remove(vkCode);
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void HandleKeyDown(uint vkCode)
    {
        if (!_isListening && (MatchesLegacyPtt(vkCode) || MatchesChordOnKeyDown(vkCode, _pttChord)))
        {
            _isListening = true;
            MicDown?.Invoke();
            _auditLogger.Append(new AuditEvent
            {
                Actor = "user",
                Action = "VOICE_MIC_DOWN",
                Result = "ok"
            });
        }

        if (!_shutupLatched && MatchesChordOnKeyDown(vkCode, _shutupChord))
        {
            _shutupLatched = true;
            Shutup?.Invoke();
            _auditLogger.Append(new AuditEvent
            {
                Actor = "user",
                Action = "VOICE_SHUTUP",
                Result = "ok"
            });
        }
    }

    private void HandleKeyUp(uint vkCode)
    {
        if (_isListening && (MatchesLegacyPtt(vkCode) || IsChordTriggerKey(vkCode, _pttChord)))
        {
            _isListening = false;
            MicUp?.Invoke();
            _auditLogger.Append(new AuditEvent
            {
                Actor = "user",
                Action = "VOICE_MIC_UP",
                Result = "ok"
            });
        }

        if (_shutupChord is not null && vkCode == _shutupChord.TriggerKey)
        {
            _shutupLatched = false;
        }
    }

    /// <summary>
    /// Emits a Shutup event from non-keyboard paths (for example UI button).
    /// </summary>
    public void RequestShutup()
    {
        Shutup?.Invoke();
        _auditLogger.Append(new AuditEvent
        {
            Actor = "user",
            Action = "VOICE_SHUTUP",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["source"] = "request"
            }
        });
    }

    /// <summary>
    /// Gets whether PTT input is currently held.
    /// </summary>
    public bool IsListening => _isListening;

    private bool MatchesLegacyPtt(uint vkCode) => vkCode == _legacyPttKey;

    private bool IsChordTriggerKey(uint vkCode, KeyChord? chord)
        => chord is not null && vkCode == chord.TriggerKey;

    private bool MatchesChordOnKeyDown(uint vkCode, KeyChord? chord)
    {
        if (chord is null || vkCode != chord.TriggerKey)
            return false;

        return AreModifiersPressed(chord.Modifiers);
    }

    private bool AreModifiersPressed(KeyModifiers required)
    {
        if (required.HasFlag(KeyModifiers.Control) &&
            !(IsPressed(0xA2) || IsPressed(0xA3)))
            return false;

        if (required.HasFlag(KeyModifiers.Shift) &&
            !(IsPressed(0xA0) || IsPressed(0xA1)))
            return false;

        if (required.HasFlag(KeyModifiers.Alt) &&
            !(IsPressed(0xA4) || IsPressed(0xA5)))
            return false;

        if (required.HasFlag(KeyModifiers.Win) &&
            !(IsPressed(0x5B) || IsPressed(0x5C)))
            return false;

        return true;
    }

    private bool IsPressed(uint vkCode) => _pressedKeys.Contains(vkCode);

    private static KeyChord? ParseChord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var raw = text.Trim();
        var parts = raw.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

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

        if (!TryParseVirtualKey(parts[^1], out var trigger))
            return null;

        return new KeyChord(raw, trigger, modifiers);
    }

    private static uint ParseVirtualKeyOrDefault(string? keyText, uint fallback)
        => TryParseVirtualKey(keyText, out var vk) ? vk : fallback;

    private static bool TryParseVirtualKey(string? keyText, out uint vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(keyText))
            return false;

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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
