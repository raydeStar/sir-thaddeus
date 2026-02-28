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
    private void KillOrphanedVoiceHostProcesses()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("SirThaddeus.VoiceHost"))
                {
                    using (process)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                var pid = process.Id;
                                process.Kill(entireProcessTree: true);
                                process.WaitForExit(2_000);
                                WriteAudit("VOICEHOST_ORPHAN_KILLED", "ok", new Dictionary<string, object>
                                {
                                    ["pid"] = pid,
                                    ["source"] = "manual_stop"
                                });
                            }
                        }
                        catch { /* best effort */ }
                    }
                }
            }
            catch { /* best effort */ }
        }

    private void PersistSessionState(string baseUrl, int port, int? processId)
        {
            try
            {
                var dir = Path.GetDirectoryName(_sessionStatePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var payload = JsonSerializer.Serialize(new
                {
                    baseUrl,
                    port,
                    pid = processId,
                    updatedAtUtc = DateTimeOffset.UtcNow
                });
                File.WriteAllText(_sessionStatePath, payload);
            }
            catch
            {
                // diagnostics-only write
            }
        }

    private void TryReapStaleSessionProcess()
        {
            if (Interlocked.Exchange(ref _staleSessionReaped, 1) == 1)
                return;

            // If another runtime instance is alive, do not reap shared voice infra.
            if (HasAnotherDesktopRuntimeAlive())
                return;

            // Phase 1: Kill session-tracked PID (existing behavior).
            try
            {
                if (File.Exists(_sessionStatePath))
                {
                    var json = File.ReadAllText(_sessionStatePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("pid", out var pidElem) &&
                            pidElem.TryGetInt32(out var pid) && pid > 0)
                        {
                            try
                            {
                                using var process = Process.GetProcessById(pid);
                                if (!process.HasExited &&
                                    process.ProcessName.Contains("VoiceHost", StringComparison.OrdinalIgnoreCase))
                                {
                                    process.Kill(entireProcessTree: true);
                                    process.WaitForExit(2_000);
                                    WriteAudit("VOICEHOST_STALE_PROCESS_REAPED", "ok", new Dictionary<string, object>
                                    {
                                        ["pid"] = pid,
                                        ["source"] = "session_file"
                                    });
                                }
                            }
                            catch { /* PID may no longer exist */ }
                        }
                    }

                    try { File.Delete(_sessionStatePath); } catch { /* best effort */ }
                }
            }
            catch { /* best effort */ }

            // Phase 2: Kill any orphaned VoiceHost processes by name.
            // Catches stale processes from other installations (e.g., packaged
            // releases) that hold the port range but aren't tracked in session state.
            try
            {
                foreach (var process in Process.GetProcessesByName("SirThaddeus.VoiceHost"))
                {
                    using (process)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                var pid = process.Id;
                                process.Kill(entireProcessTree: true);
                                process.WaitForExit(2_000);
                                WriteAudit("VOICEHOST_STALE_PROCESS_REAPED", "ok", new Dictionary<string, object>
                                {
                                    ["pid"] = pid,
                                    ["source"] = "process_name_scan"
                                });
                            }
                        }
                        catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteAudit("VOICEHOST_STALE_PROCESS_REAP_FAILED", "error", new Dictionary<string, object>
                {
                    ["message"] = ex.Message
                });
            }
        }

    private static bool HasAnotherDesktopRuntimeAlive()
        {
            try
            {
                var currentPid = Environment.ProcessId;
                foreach (var process in Process.GetProcessesByName("SirThaddeus.DesktopRuntime"))
                {
                    using (process)
                    {
                        if (process.Id == currentPid)
                            continue;

                        if (!process.HasExited)
                            return true;
                    }
                }
            }
            catch
            {
                // Best effort detection only.
            }

            return false;
        }
}
