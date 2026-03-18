using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SirThaddeus.UI.Avalonia;

public sealed record AudioDeviceOption(int DeviceNumber, string ProductName, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioDeviceOption> GetInputDevices()
    {
        var devices = new List<AudioDeviceOption>
        {
            new(-1, "", "System Default")
        };

        if (!OperatingSystem.IsWindows())
        {
            return devices;
        }

        try
        {
            var count = WaveInEvent.DeviceCount;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var caps = WaveInEvent.GetCapabilities(i);
                    devices.Add(new AudioDeviceOption(i, caps.ProductName, caps.ProductName));
                }
                catch
                {
                    // Ignore individual device enumeration failures.
                }
            }
        }
        catch
        {
            // Ignore hardware enumeration failures.
        }

        return devices;
    }

    public static IReadOnlyList<AudioDeviceOption> GetOutputDevices()
    {
        var devices = new List<AudioDeviceOption>
        {
            new(-1, "", "System Default")
        };

        if (!OperatingSystem.IsWindows())
        {
            return devices;
        }

        try
        {
            var count = checked((int)waveOutGetNumDevs());
            for (var i = 0; i < count; i++)
            {
                try
                {
                    if (waveOutGetDevCaps((nint)i, out var caps, Marshal.SizeOf<WaveOutCaps>()) == 0)
                    {
                        devices.Add(new AudioDeviceOption(i, caps.ProductName, caps.ProductName));
                    }
                }
                catch
                {
                    // Ignore individual device enumeration failures.
                }
            }
        }
        catch
        {
            // Ignore hardware enumeration failures.
        }

        return devices;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetDevCaps(nint deviceId, out WaveOutCaps caps, int size);

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

