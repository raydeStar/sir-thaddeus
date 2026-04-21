using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thaddeus.Runtime.Audio;

/// <summary>
/// Enumerates audio input/output devices via winmm.dll on Windows.
/// Returns an empty catalog on non-Windows platforms — devices are matched
/// by product name at playback time, so empty simply means "no picker data."
/// </summary>
public static class AudioDeviceEnumerator
{
    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<AudioDeviceInfo>();
        return WindowsEnumerator.GetInputDevices();
    }

    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<AudioDeviceInfo>();
        return WindowsEnumerator.GetOutputDevices();
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsEnumerator
    {
        public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                var count = checked((int)waveInGetNumDevs());
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        if (waveInGetDevCaps((nint)i, out var caps, Marshal.SizeOf<WaveInCaps>()) == 0)
                        {
                            list.Add(new AudioDeviceInfo(i, caps.ProductName, caps.ProductName));
                        }
                    }
                    catch { /* ignore per-device failures */ }
                }
            }
            catch { /* ignore full-enumeration failures */ }
            return list;
        }

        public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                var count = checked((int)waveOutGetNumDevs());
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        if (waveOutGetDevCaps((nint)i, out var caps, Marshal.SizeOf<WaveOutCaps>()) == 0)
                        {
                            list.Add(new AudioDeviceInfo(i, caps.ProductName, caps.ProductName));
                        }
                    }
                    catch { /* ignore per-device failures */ }
                }
            }
            catch { /* ignore full-enumeration failures */ }
            return list;
        }

        [DllImport("winmm.dll")]
        private static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern uint waveInGetDevCaps(nint deviceId, out WaveInCaps caps, int size);

        [DllImport("winmm.dll")]
        private static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern uint waveOutGetDevCaps(nint deviceId, out WaveOutCaps caps, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct WaveInCaps
        {
            public ushort ManufacturerId;
            public ushort ProductId;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string ProductName;
            public uint Formats;
            public ushort Channels;
            public ushort Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct WaveOutCaps
        {
            public ushort ManufacturerId;
            public ushort ProductId;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string ProductName;
            public uint Formats;
            public ushort Channels;
            public ushort Reserved;
            public uint Support;
        }
    }
}

/// <summary>Single audio device entry.</summary>
public sealed record AudioDeviceInfo(int DeviceNumber, string ProductName, string DisplayName);
