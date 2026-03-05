using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;

namespace SirThaddeus.DesktopRuntime.Services;

public sealed partial class VoiceHostProcessManager
{
    /// <summary>
        /// Checks whether the Visual C++ 2015-2022 x64 Redistributable is installed.
        /// CTranslate2 (used by faster-whisper) requires it for native DLL loading.
        /// Returns a user-friendly warning message, or null if the runtime is present.
        /// </summary>
        internal static string? CheckVcRedistInstalled()
        {
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                // The VC++ 2015-2022 x64 redist registers under this key.
                // "Installed" DWORD = 1 means it's present.
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
                if (key is not null)
                {
                    var installed = key.GetValue("Installed");
                    if (installed is int i && i == 1)
                        return null; // present
                }

                // Fallback: check if the DLL itself exists in System32.
                var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (File.Exists(Path.Combine(sys32, "vcruntime140.dll")))
                    return null;
            }
            catch
            {
                // Registry access failed — can't confirm either way, assume OK.
                return null;
            }

            return "Visual C++ Redistributable is not installed. "
                 + "Speech recognition may crash without it. "
                 + "Download it from: https://aka.ms/vs/17/release/vc_redist.x64.exe";
        }

    private string ResolveVoiceHostPath()
        {
            const string exeName = "SirThaddeus.VoiceHost.exe";
            var baseDir = AppContext.BaseDirectory;

            var adjacent = Path.Combine(baseDir, exeName);
            if (File.Exists(adjacent))
                return adjacent;

            var dir = new DirectoryInfo(baseDir);
            while (dir is null == false && dir.Name != "apps")
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                // Give up if we can't find 'apps'
                return Path.Combine(baseDir, exeName);
            }

            var voiceHostBinDebug = Path.Combine(
                dir.FullName,
                "voice-host", "SirThaddeus.VoiceHost",
                "bin", "Debug");

            if (Directory.Exists(voiceHostBinDebug))
            {
                string? newest = null;
                var newestTime = DateTime.MinValue;
                foreach (var tfmDir in Directory.GetDirectories(voiceHostBinDebug))
                {
                    var candidate = Path.Combine(tfmDir, exeName);
                    if (!File.Exists(candidate))
                        continue;

                    var writeTime = File.GetLastWriteTimeUtc(candidate);
                    if (writeTime > newestTime)
                    {
                        newest = candidate;
                        newestTime = writeTime;
                    }
                }

                if (newest is not null)
                    return newest;
            }

            return Path.GetFullPath(Path.Combine(
                voiceHostBinDebug,
                "net10.0",
                exeName));
        }
}
