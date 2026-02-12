using NAudio.Wave;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Lightweight record for audio device dropdown items.
/// Stores both the raw product name (for persistence matching)
/// and a human-friendly display label.
/// </summary>
public sealed record AudioDeviceInfo(int DeviceNumber, string ProductName, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Enumerates available audio input and output devices via NAudio.
/// All methods are safe to call from any thread and swallow hardware
/// enumeration failures gracefully — a missing sound card should never
/// crash the settings panel.
/// </summary>
public static class AudioDeviceEnumerator
{
    // ─────────────────────────────────────────────────────────────────
    // Input (Recording) Devices
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all available recording devices.
    /// Device 0 is the system default input and is labeled accordingly.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            int count = WaveIn.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var caps = WaveIn.GetCapabilities(i);
                    var display = i == 0
                        ? $"{caps.ProductName} (Default)"
                        : caps.ProductName;
                    devices.Add(new AudioDeviceInfo(i, caps.ProductName, display));
                }
                catch
                {
                    // Individual device enumeration failure — skip silently.
                }
            }
        }
        catch
        {
            // Hardware enumeration unavailable (e.g. no sound card).
        }

        return devices;
    }

    // ─────────────────────────────────────────────────────────────────
    // Output (Playback) Devices
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all available playback devices.
    /// Device -1 (WAVE_MAPPER) is always included as "System Default".
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>
        {
            new(-1, "", "System Default")
        };

        try
        {
            int count = WaveOut.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var caps = WaveOut.GetCapabilities(i);
                    devices.Add(new AudioDeviceInfo(i, caps.ProductName, caps.ProductName));
                }
                catch
                {
                    // Individual device enumeration failure — skip silently.
                }
            }
        }
        catch
        {
            // Hardware enumeration unavailable.
        }

        return devices;
    }

    // ─────────────────────────────────────────────────────────────────
    // Name → Device Number Resolution
    //
    // NAudio truncates WinMM product names to 32 chars, so we also
    // attempt a prefix match when the stored name is longer.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a persisted device name to a NAudio device number for input.
    /// Returns 0 (system default) when the name is empty or no match is found.
    /// </summary>
    public static int ResolveInputDeviceNumber(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return 0;

        try
        {
            int count = WaveIn.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var caps = WaveIn.GetCapabilities(i);
                    if (NameMatches(caps.ProductName, deviceName))
                        return i;
                }
                catch { /* skip */ }
            }
        }
        catch { /* enumeration unavailable */ }

        return 0;
    }

    /// <summary>
    /// Resolves a persisted device name to a NAudio device number for output.
    /// Returns -1 (WAVE_MAPPER / system default) when no match is found.
    /// </summary>
    public static int ResolveOutputDeviceNumber(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return -1;

        try
        {
            int count = WaveOut.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var caps = WaveOut.GetCapabilities(i);
                    if (NameMatches(caps.ProductName, deviceName))
                        return i;
                }
                catch { /* skip */ }
            }
        }
        catch { /* enumeration unavailable */ }

        return -1;
    }

    private static bool NameMatches(string capsName, string storedName)
    {
        if (capsName.Equals(storedName, StringComparison.OrdinalIgnoreCase))
            return true;

        // NAudio caps names are truncated to ~31 chars — try prefix match
        if (storedName.Length > 31 &&
            capsName.StartsWith(storedName[..31], StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
