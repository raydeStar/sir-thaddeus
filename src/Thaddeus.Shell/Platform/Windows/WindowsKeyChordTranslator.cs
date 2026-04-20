using System.Runtime.Versioning;
using Thaddeus.Shell.Platform;

namespace Thaddeus.Shell.Platform.Windows;

/// <summary>
/// Translates platform-agnostic <see cref="KeyChord"/> values into the Win32
/// <c>RegisterHotKey</c> argument shape (a modifier bit-set and a virtual-key
/// code).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsKeyChordTranslator
{
    // MOD_* constants from winuser.h. MOD_NOREPEAT (0x4000) is added so that
    // holding the chord doesn't fire a flood of WM_HOTKEY messages.
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    /// <summary>
    /// Converts a chord into Win32 modifier flags and a virtual-key code.
    /// Throws <see cref="ArgumentException"/> when the key name is not
    /// recognised or when the chord is empty.
    /// </summary>
    public static (uint Modifiers, uint VirtualKey) Translate(KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (string.IsNullOrWhiteSpace(chord.Key))
        {
            throw new ArgumentException("Chord key is required.", nameof(chord));
        }

        var mods = MOD_NOREPEAT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Control)) mods |= MOD_CONTROL;
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= MOD_SHIFT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= MOD_ALT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Super)) mods |= MOD_WIN;

        var vk = ResolveVirtualKey(chord.Key);
        return (mods, vk);
    }

    /// <summary>Resolves a key name (e.g. "Space", "F8", "A") to a virtual-key code.</summary>
    public static uint ResolveVirtualKey(string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        var key = keyName.Trim();

        if (NamedKeys.TryGetValue(key, out var named))
        {
            return named;
        }

        // Letters A-Z map directly to their ASCII upper-case codepoint.
        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            if (ch is >= 'A' and <= 'Z') return ch;
            if (ch is >= '0' and <= '9') return ch;
        }

        // Function keys F1..F24 → VK_F1 (0x70) .. VK_F24 (0x87).
        if ((key.Length == 2 || key.Length == 3) &&
            (key[0] == 'F' || key[0] == 'f') &&
            int.TryParse(key.AsSpan(1), out var fnIndex) &&
            fnIndex is >= 1 and <= 24)
        {
            return (uint)(0x70 + fnIndex - 1);
        }

        throw new ArgumentException($"Unknown key name '{keyName}'.", nameof(keyName));
    }

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Esc"] = 0x1B,
        ["Tab"] = 0x09,
        ["Backspace"] = 0x08,
        ["Delete"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
    };
}
