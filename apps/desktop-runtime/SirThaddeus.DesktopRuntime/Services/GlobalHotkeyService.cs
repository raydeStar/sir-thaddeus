using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Service for registering and managing global hotkeys via Win32 API.
/// Automatically unregisters hotkeys on disposal.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    // ─────────────────────────────────────────────────────────────────────
    // Win32 Interop
    // ─────────────────────────────────────────────────────────────────────

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Modifier keys for hotkey registration.
    /// </summary>
    [Flags]
    public enum Modifiers : uint
    {
        None = 0x0000,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000 // Prevents repeated triggers when holding
    }

    /// <summary>
    /// Virtual key codes for common keys.
    /// </summary>
    public static class VirtualKeys
    {
        public const uint Space = 0x20;
        public const uint Enter = 0x0D;
        public const uint Escape = 0x1B;
        public const uint Tab = 0x09;
    }

    // ─────────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────────

    private readonly Window _owner;
    private readonly IntPtr _hwnd;
    private readonly HwndSource _hwndSource;
    private readonly Dictionary<int, Action> _registeredHotkeys = [];
    private int _nextHotkeyId = 1;
    private bool _disposed;

    /// <summary>
    /// Creates a new hotkey service attached to the specified WPF window.
    /// </summary>
    public GlobalHotkeyService(Window owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        
        var helper = new WindowInteropHelper(_owner);
        helper.EnsureHandle();
        _hwnd = helper.Handle;

        _hwndSource = HwndSource.FromHwnd(_hwnd)
            ?? throw new InvalidOperationException("Could not obtain HwndSource from window handle.");
        
        _hwndSource.AddHook(WndProc);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a global hotkey and returns its ID.
    /// </summary>
    /// <param name="modifiers">Modifier keys (Ctrl, Alt, Shift, Win).</param>
    /// <param name="virtualKey">The virtual key code.</param>
    /// <param name="callback">Action to invoke when hotkey is pressed.</param>
    /// <returns>The hotkey ID, or -1 if registration failed.</returns>
    public int Register(Modifiers modifiers, uint virtualKey, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);

        var id = _nextHotkeyId++;

        // Add NoRepeat to prevent stuttering when held
        var modsWithNoRepeat = modifiers | Modifiers.NoRepeat;

        if (!RegisterHotKey(_hwnd, id, (uint)modsWithNoRepeat, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine(
                $"[GlobalHotkeyService] Failed to register hotkey (id={id}, error={error})");
            return -1;
        }

        _registeredHotkeys[id] = callback;
        return id;
    }

    /// <summary>
    /// Unregisters a specific hotkey by its ID.
    /// </summary>
    /// <param name="hotkeyId">The ID returned from Register.</param>
    /// <returns>True if unregistered successfully.</returns>
    public bool Unregister(int hotkeyId)
    {
        if (_disposed || !_registeredHotkeys.ContainsKey(hotkeyId))
            return false;

        var result = UnregisterHotKey(_hwnd, hotkeyId);
        _registeredHotkeys.Remove(hotkeyId);
        return result;
    }

    /// <summary>
    /// Unregisters all registered hotkeys.
    /// </summary>
    public void UnregisterAll()
    {
        if (_disposed) return;

        foreach (var id in _registeredHotkeys.Keys.ToList())
        {
            UnregisterHotKey(_hwnd, id);
        }
        _registeredHotkeys.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Message Processing
    // ─────────────────────────────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registeredHotkeys.TryGetValue((int)wParam, out var callback))
        {
            handled = true;
            try
            {
                callback.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GlobalHotkeyService] Hotkey callback exception: {ex.Message}");
            }
        }

        return IntPtr.Zero;
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDisposable
    // ─────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _hwndSource.RemoveHook(WndProc);
    }
}
