using System.IO;
using System.Runtime.InteropServices;
using SirThaddeus.AuditLog;
using SirThaddeus.Core;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Push-to-talk service for voice input.
/// Holds a key to start listening, releases to stop.
/// Currently just records audio to a temp file - no transcription yet.
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
    // NAudio (or stub) for audio capture
    // Note: Full implementation would use NAudio; this is a stub skeleton
    // ─────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────────

    private readonly IAuditLogger _auditLogger;
    private readonly RuntimeController _runtimeController;
    private readonly uint _activationKey;
    private readonly string _audioFolder;
    private readonly Func<string, Task>? _onRecordingComplete;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;
    private bool _isListening;
    private string? _currentRecordingPath;
    private DateTime _recordingStartTime;
    private bool _disposed;

    /// <summary>
    /// Creates a new push-to-talk service.
    /// </summary>
    /// <param name="auditLogger">Audit logger for recording events.</param>
    /// <param name="runtimeController">Runtime controller for state management.</param>
    /// <param name="activationKey">Virtual key code for the PTT key (default: F13 = 0x7C).</param>
    /// <param name="onRecordingComplete">
    /// Optional callback invoked with the audio file path when recording stops.
    /// This is the hook point for the transcription -> orchestrator pipeline.
    /// </param>
    public PushToTalkService(
        IAuditLogger auditLogger,
        RuntimeController runtimeController,
        uint activationKey = 0x7C, // VK_F13
        Func<string, Task>? onRecordingComplete = null)
    {
        _auditLogger = auditLogger;
        _runtimeController = runtimeController;
        _activationKey = activationKey;
        _onRecordingComplete = onRecordingComplete;

        // Create audio folder
        _audioFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SirThaddeus",
            "audio");
        Directory.CreateDirectory(_audioFolder);
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
                ["activation_key"] = $"0x{_activationKey:X2}"
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
            StopRecording();
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

            if (vkCode == _activationKey)
            {
                if (wParam == WM_KEYDOWN && !_isListening)
                {
                    StartRecording();
                }
                else if (wParam == WM_KEYUP && _isListening)
                {
                    StopRecording();
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void StartRecording()
    {
        _isListening = true;
        _recordingStartTime = DateTime.UtcNow;
        _currentRecordingPath = Path.Combine(
            _audioFolder,
            $"ptt_{_recordingStartTime:yyyyMMdd_HHmmss}.wav");

        // Update runtime state to Listening
        _runtimeController.SetState(AssistantState.Listening, "PTT key held");

        _auditLogger.Append(new AuditEvent
        {
            Actor = "user",
            Action = "PTT_RECORDING_START",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["path"] = _currentRecordingPath
            }
        });

        // Note: Actual audio recording would be implemented here using NAudio
        // For now this is just a skeleton that creates an empty placeholder
        CreatePlaceholderWavFile(_currentRecordingPath);
    }

    private void StopRecording()
    {
        _isListening = false;
        var duration = DateTime.UtcNow - _recordingStartTime;
        var recordedPath = _currentRecordingPath;

        // Transition: Listening -> Thinking (orchestrator will move to Idle on completion)
        _runtimeController.SetState(AssistantState.Idle, "PTT key released");

        _auditLogger.Append(new AuditEvent
        {
            Actor = "user",
            Action = "PTT_RECORDING_STOP",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["path"] = recordedPath ?? "",
                ["duration_ms"] = (int)duration.TotalMilliseconds
            }
        });

        _currentRecordingPath = null;

        // Fire the pipeline callback if registered (async fire-and-forget)
        if (_onRecordingComplete != null && recordedPath != null)
        {
            _ = Task.Run(async () =>
            {
                try { await _onRecordingComplete(recordedPath); }
                catch (Exception ex)
                {
                    _auditLogger.Append(new AuditEvent
                    {
                        Actor = "runtime",
                        Action = "PTT_PIPELINE_ERROR",
                        Result = "error",
                        Details = new Dictionary<string, object>
                        {
                            ["error"] = ex.Message
                        }
                    });
                }
            });
        }
    }

    /// <summary>
    /// Creates a minimal valid WAV file header as a placeholder.
    /// Real implementation would use NAudio for actual recording.
    /// </summary>
    private static void CreatePlaceholderWavFile(string path)
    {
        // Create a minimal valid WAV file (44-byte header + no data)
        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36); // File size - 8 (placeholder)
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write("fmt "u8);
        bw.Write(16); // Chunk size
        bw.Write((short)1); // Audio format (PCM)
        bw.Write((short)1); // Num channels
        bw.Write(16000); // Sample rate
        bw.Write(32000); // Byte rate
        bw.Write((short)2); // Block align
        bw.Write((short)16); // Bits per sample

        // data chunk
        bw.Write("data"u8);
        bw.Write(0); // Data size (empty for placeholder)
    }

    /// <summary>
    /// Gets whether PTT is currently listening (key held).
    /// </summary>
    public bool IsListening => _isListening;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
