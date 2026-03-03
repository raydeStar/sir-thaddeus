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
    private (bool Started, string Error) StartManagedProcess(
            string hostPath,
            int port,
            VoiceSettings settings)
        {
            try
            {
                StopManagedProcessIfAny();

                var args = $"--port {port} --bind 127.0.0.1 --mode proxy-first";
                if (!string.IsNullOrWhiteSpace(settings.AsrEndpoint))
                    args += $" --asr-upstream {QuoteArg(settings.AsrEndpoint.Trim())}";
                if (!string.IsNullOrWhiteSpace(settings.TtsEndpoint))
                    args += $" --tts-upstream {QuoteArg(settings.TtsEndpoint.Trim())}";
                args += $" --tts-engine {QuoteArg(settings.GetNormalizedTtsEngine())}";
                var configuredSttEngine = settings.GetNormalizedSttEngine();
                // Interactive voice should always boot with faster-whisper.
                // Qwen is reserved for transcription jobs.
                const string frontendSttEngine = "faster-whisper";
                args += $" --stt-engine {QuoteArg(frontendSttEngine)}";

                var resolvedSttModelId = ResolveFrontendSttModelId(settings, configuredSttEngine);
                if (!string.IsNullOrWhiteSpace(resolvedSttModelId))
                    args += $" --stt-model-id {QuoteArg(resolvedSttModelId)}";
                var resolvedSttLanguage = settings.GetResolvedSttLanguage();
                if (!string.IsNullOrWhiteSpace(resolvedSttLanguage))
                    args += $" --stt-language {QuoteArg(resolvedSttLanguage)}";

                var resolvedTtsModelId = settings.GetResolvedTtsModelId();
                if (!string.IsNullOrWhiteSpace(resolvedTtsModelId))
                    args += $" --tts-model-id {QuoteArg(resolvedTtsModelId)}";

                var resolvedTtsVoiceId = settings.GetResolvedTtsVoiceId();
                if (!string.IsNullOrWhiteSpace(resolvedTtsVoiceId))
                    args += $" --tts-voice-id {QuoteArg(resolvedTtsVoiceId)}";

                var startInfo = new ProcessStartInfo
                {
                    FileName = hostPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(hostPath) ?? AppContext.BaseDirectory
                };

                var process = _processStarter(startInfo);
                if (process is null)
                {
                    WriteAudit("VOICEHOST_PROCESS_START_FAILED", "error", new Dictionary<string, object>
                    {
                        ["path"] = hostPath,
                        ["port"] = port
                    });
                    return (false, "Process.Start returned null.");
                }

                var logPath = Path.Combine(AppContext.BaseDirectory, "voicehost-debug.log");
                var logLock = new object();
                static bool IsDebugLogError(string level, string line)
                {
                    if (string.Equals(level, "ERR", StringComparison.OrdinalIgnoreCase))
                    {
                        var lowered = line.ToLowerInvariant();
                        return lowered.Contains("error") ||
                               lowered.Contains("exception") ||
                               lowered.Contains("fatal") ||
                               lowered.Contains("traceback") ||
                               lowered.Contains("critical");
                    }

                    if (string.Equals(level, "SYS", StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Contains("exited with code", StringComparison.OrdinalIgnoreCase) &&
                               !line.EndsWith(" code 0", StringComparison.OrdinalIgnoreCase);
                    }

                    return false;
                }

                void WriteLog(string level, string? data)
                {
                    if (string.IsNullOrWhiteSpace(data)) return;
                    var line = data.Trim();
                    if (!IsDebugLogError(level, line))
                        return;

                    try
                    {
                        lock (logLock)
                        {
                            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [{level}] {line}{Environment.NewLine}");
                        }
                    }
                    catch { }
                }

                static bool ShouldMirrorLineToAudit(string level, string line)
                {
                    if (string.Equals(level, "ERR", StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (string.Equals(level, "OUT", StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Contains("[VOICE_TTS_READY]", StringComparison.OrdinalIgnoreCase) ||
                               line.Contains("Application startup complete", StringComparison.OrdinalIgnoreCase) ||
                               line.Contains("Uvicorn running", StringComparison.OrdinalIgnoreCase);
                    }

                    return false;
                }

                process.EnableRaisingEvents = true;
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        var trimmed = e.Data.Trim();
                        UpdateStartupPhase(trimmed);
                        WriteLog("OUT", trimmed);
                        if (ShouldMirrorLineToAudit("OUT", trimmed))
                        {
                            WriteAudit("VOICEHOST_PROCESS_STDOUT", "ok", new Dictionary<string, object>
                            {
                                ["pid"] = process.Id,
                                ["line"] = trimmed
                            });
                        }
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        var trimmed = e.Data.Trim();
                        UpdateStartupPhase(trimmed);
                        WriteLog("ERR", trimmed);
                        if (ShouldMirrorLineToAudit("ERR", trimmed))
                        {
                            WriteAudit("VOICEHOST_PROCESS_STDERR", "warn", new Dictionary<string, object>
                            {
                                ["pid"] = process.Id,
                                ["line"] = trimmed
                            });
                        }
                    }
                };
                process.Exited += (_, _) =>
                {
                    WriteLog("SYS", $"Process exited with code {process.ExitCode}");
                    WriteAudit("VOICEHOST_PROCESS_EXITED", "ok", new Dictionary<string, object>
                    {
                        ["pid"] = process.Id,
                        ["exitCode"] = process.ExitCode
                    });
                };

                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                catch
                {
                    // Not fatal; process can still run without stream readers.
                }

                lock (_processGate)
                {
                    _managedProcess = process;
                    _managedProcessPort = port;
                }

                WriteAudit("VOICEHOST_PROCESS_STARTED", "ok", new Dictionary<string, object>
                {
                    ["path"] = hostPath,
                    ["port"] = port,
                    ["pid"] = process.Id,
                    ["args"] = args,
                    ["configuredSttEngine"] = configuredSttEngine,
                    ["effectiveSttEngine"] = frontendSttEngine,
                    ["effectiveSttModelId"] = resolvedSttModelId
                });

                return (true, "");
            }
            catch (Exception ex)
            {
                WriteAudit("VOICEHOST_PROCESS_START_FAILED", "error", new Dictionary<string, object>
                {
                    ["path"] = hostPath,
                    ["port"] = port,
                    ["message"] = ex.Message
                });
                return (false, ex.Message);
            }
        }

    private void UpdateStartupPhase(string line)
        {
            // Map well-known stdout/stderr markers emitted by start-voice-backend.ps1
            // and server.py to short, user-friendly descriptions.
            if (line.Contains("[VENV_OK]", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Setting up Python environment...";
            else if (line.Contains("Installing dependencies", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("uv pip install", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Installing voice dependencies...";
            else if (line.Contains("Dependencies already installed", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Preparing voice engine...";
            else if (line.Contains("[ASSET_OK]", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Voice models verified.";
            else if (line.Contains("Preparing voice/ASR", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("[VOICE_PREFETCH]", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Preparing voice assets...";
            else if (line.Contains("[VOICE_TTS_READY]", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "TTS engine ready, starting server...";
            else if (line.Contains("Voice Backend starting", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Starting voice server...";
            else if (line.Contains("Application startup complete", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Voice server started, loading models...";
            else if (line.Contains("Lazy-loading faster-whisper", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Loading faster-whisper", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Loading speech recognition model...";
            else if (line.Contains("faster-whisper model", StringComparison.OrdinalIgnoreCase) &&
                     line.Contains("loaded", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Speech recognition ready.";
            else if (line.Contains("TTS Warmup", StringComparison.OrdinalIgnoreCase) &&
                     line.Contains("READY", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Voice engine ready.";
            else if (line.Contains("Uvicorn running", StringComparison.OrdinalIgnoreCase))
                _lastStartupPhase = "Voice backend online, waiting for readiness...";
        }

    private bool HasManagedProcessExited()
        {
            lock (_processGate)
            {
                return _managedProcess is not null && _managedProcess.HasExited;
            }
        }

    private bool HasManagedProcessAlive()
        {
            lock (_processGate)
            {
                return _managedProcess is not null && !_managedProcess.HasExited;
            }
        }

    private bool IsManagedProcessAliveOnPort(int port)
        {
            lock (_processGate)
            {
                return _managedProcess is not null &&
                       !_managedProcess.HasExited &&
                       _managedProcessPort == port;
            }
        }

    private int? TryGetManagedProcessId()
        {
            lock (_processGate)
            {
                if (_managedProcess is null)
                    return null;
                try
                {
                    return _managedProcess.Id;
                }
                catch
                {
                    return null;
                }
            }
        }

    private void StopManagedProcessIfAny()
        {
            _lastStartupPhase = "";
            _lastReadyAtUtc = null;
            lock (_processGate)
            {
                if (_managedProcess is null)
                {
                    _managedProcessPort = null;
                    return;
                }

                try
                {
                    if (!_managedProcess.HasExited)
                        _managedProcess.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best effort
                }
                finally
                {
                    _managedProcess.Dispose();
                    _managedProcess = null;
                    _managedProcessPort = null;
                }
            }
        }
}
