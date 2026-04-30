using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thaddeus.Shell.Platform.Windows;

/// <summary>
/// Windows global-shortcut adapter built on <c>RegisterHotKey</c>. Owns a
/// dedicated background thread that hosts the message queue, registers
/// hotkeys against that thread (NULL hwnd routes WM_HOTKEY to the calling
/// thread's queue), and pumps messages to surface <see cref="Triggered"/>.
///
/// All native calls are gated behind the Windows platform check; non-Windows
/// hosts use <see cref="StubGlobalShortcutAdapter"/> via the DI factory.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalShortcutAdapter : IGlobalShortcutAdapter
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_USER = 0x0400;
    private const uint WM_REGISTER = WM_USER + 1;
    private const uint WM_UNREGISTER = WM_USER + 2;
    private const uint WM_QUIT = 0x0012;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    private readonly ILogger<WindowsGlobalShortcutAdapter> _logger;
    private readonly Thread _pumpThread;
    private readonly ManualResetEventSlim _pumpReady = new();
    private readonly ConcurrentDictionary<string, int> _idsByName = new();
    private readonly ConcurrentDictionary<int, string> _namesById = new();
    private readonly ConcurrentDictionary<int, (uint Mods, uint Vk)> _registeredById = new();
    private readonly ConcurrentDictionary<int, byte> _activeHolds = new();
    private readonly ConcurrentDictionary<int, (uint Mods, uint Vk, TaskCompletionSource<bool> Tcs)> _pendingRegistrations = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingUnregistrations = new();
    private uint _pumpThreadId;
    private int _nextId;
    private bool _disposed;

    /// <summary>Initialises the adapter and starts the message-pump thread.</summary>
    public WindowsGlobalShortcutAdapter(ILogger<WindowsGlobalShortcutAdapter> logger)
    {
        _logger = logger;
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "Thaddeus.GlobalShortcutPump",
        };
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();
        _pumpReady.Wait();
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public event EventHandler<string>? Triggered;

    /// <inheritdoc />
    public event EventHandler<string>? Released;

    /// <inheritdoc />
    public Task<bool> RegisterAsync(string id, KeyChord chord, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(chord);
        ThrowIfDisposed();

        if (_idsByName.ContainsKey(id))
        {
            _logger.LogWarning("shortcut.windows.duplicate id={Id}", id);
            return Task.FromResult(false);
        }

        var (mods, vk) = WindowsKeyChordTranslator.Translate(chord);
        var hotkeyId = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRegistrations[hotkeyId] = (mods, vk, tcs);

        if (!PostThreadMessage(_pumpThreadId, WM_REGISTER, new IntPtr(hotkeyId), IntPtr.Zero))
        {
            _pendingRegistrations.TryRemove(hotkeyId, out _);
            return Task.FromResult(false);
        }

        // Bind the public id only on success so a failed registration leaves no trace.
        return tcs.Task.ContinueWith(t =>
        {
            if (t.Result)
            {
                _idsByName[id] = hotkeyId;
                _namesById[hotkeyId] = id;
                _registeredById[hotkeyId] = (mods, vk);
            }
            return t.Result;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <inheritdoc />
    public Task UnregisterAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ThrowIfDisposed();

        if (!_idsByName.TryRemove(id, out var hotkeyId))
        {
            return Task.CompletedTask;
        }
        _namesById.TryRemove(hotkeyId, out _);
        _registeredById.TryRemove(hotkeyId, out _);
        _activeHolds.TryRemove(hotkeyId, out _);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingUnregistrations[hotkeyId] = tcs;
        if (!PostThreadMessage(_pumpThreadId, WM_UNREGISTER, new IntPtr(hotkeyId), IntPtr.Zero))
        {
            _pendingUnregistrations.TryRemove(hotkeyId, out _);
        }
        return tcs.Task;
    }

    private void PumpLoop()
    {
        _pumpThreadId = GetCurrentThreadId();
        // Force the thread to have a message queue before any PostThreadMessage races in.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        _pumpReady.Set();

        while (true)
        {
            var ret = GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (ret <= 0) break; // -1 = error, 0 = WM_QUIT
            switch (msg.message)
            {
                case WM_HOTKEY:
                    HandleHotkey((int)msg.wParam);
                    break;
                case WM_REGISTER:
                    HandleRegister((int)msg.wParam);
                    break;
                case WM_UNREGISTER:
                    HandleUnregister((int)msg.wParam);
                    break;
            }
        }
    }

    private void HandleRegister(int hotkeyId)
    {
        if (!_pendingRegistrations.TryRemove(hotkeyId, out var entry)) return;
        var ok = RegisterHotKey(IntPtr.Zero, hotkeyId, entry.Mods, entry.Vk);
        if (!ok)
        {
            _logger.LogWarning(
                "shortcut.windows.register_failed id={Id} winError={Err}",
                hotkeyId, Marshal.GetLastWin32Error());
        }
        entry.Tcs.TrySetResult(ok);
    }

    private void HandleUnregister(int hotkeyId)
    {
        var ok = UnregisterHotKey(IntPtr.Zero, hotkeyId);
        if (_pendingUnregistrations.TryRemove(hotkeyId, out var tcs))
        {
            tcs.TrySetResult(ok);
        }
    }

    private void HandleHotkey(int hotkeyId)
    {
        if (!_namesById.TryGetValue(hotkeyId, out var name)) return;
        try
        {
            Triggered?.Invoke(this, name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "shortcut.windows.handler_threw id={Id}", name);
        }

        if (_registeredById.TryGetValue(hotkeyId, out var chord) && _activeHolds.TryAdd(hotkeyId, 0))
            _ = Task.Run(() => MonitorReleaseAsync(hotkeyId, name, chord.Mods, chord.Vk));
    }

    private async Task MonitorReleaseAsync(int hotkeyId, string name, uint mods, uint vk)
    {
        try
        {
            while (!_disposed && IsChordPressed(mods, vk))
                await Task.Delay(20).ConfigureAwait(false);

            try
            {
                Released?.Invoke(this, name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "shortcut.windows.release_handler_threw id={Id}", name);
            }
        }
        finally
        {
            _activeHolds.TryRemove(hotkeyId, out _);
        }
    }

    private static bool IsChordPressed(uint mods, uint vk)
    {
        if (!IsKeyPressed((int)vk)) return false;
        var pureMods = mods & ~MOD_NOREPEAT;
        if ((pureMods & MOD_CONTROL) != 0 && !IsAnyKeyPressed(0x11, 0xA2, 0xA3)) return false;
        if ((pureMods & MOD_SHIFT) != 0 && !IsAnyKeyPressed(0x10, 0xA0, 0xA1)) return false;
        if ((pureMods & MOD_ALT) != 0 && !IsAnyKeyPressed(0x12, 0xA4, 0xA5)) return false;
        if ((pureMods & MOD_WIN) != 0 && !IsAnyKeyPressed(0x5B, 0x5C)) return false;
        return true;
    }

    private static bool IsAnyKeyPressed(params int[] keys) => keys.Any(IsKeyPressed);

    private static bool IsKeyPressed(int key) => (GetAsyncKeyState(key) & unchecked((short)0x8000)) != 0;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsGlobalShortcutAdapter));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Tell pump thread to unregister everything then exit.
        foreach (var hotkeyId in _namesById.Keys.ToArray())
        {
            PostThreadMessage(_pumpThreadId, WM_UNREGISTER, new IntPtr(hotkeyId), IntPtr.Zero);
        }
        PostThreadMessage(_pumpThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        try { _pumpThread.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _pumpReady.Dispose();
    }
}
