<#
.SYNOPSIS
    Diagnose a deployed release build. Run from the directory containing
    SirThaddeus.DesktopRuntime.exe (the extracted ZIP root).

.EXAMPLE
    cd C:\path\to\extracted\zip
    powershell -File diagnose-deploy.ps1
#>
param([string]$DeployDir = ".")

$ErrorActionPreference = "Continue"
$DeployDir = Resolve-Path $DeployDir

function Write-Section([string]$Title) {
    Write-Host "`n$('=' * 60)" -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host "$('=' * 60)" -ForegroundColor Cyan
}

function Write-Ok([string]$Msg) { Write-Host "  [OK] $Msg" -ForegroundColor Green }
function Write-Err([string]$Msg) { Write-Host "  [FAIL] $Msg" -ForegroundColor Red }
function Write-Warn([string]$Msg) { Write-Host "  [WARN] $Msg" -ForegroundColor Yellow }
function Write-Info([string]$Msg) { Write-Host "  $Msg" -ForegroundColor Gray }

# ── 1. Check directory structure ─────────────────────────────────────
Write-Section "1. Directory Structure"

$checks = @(
    @{ Path = "SirThaddeus.DesktopRuntime.exe"; Label = "DesktopRuntime exe" },
    @{ Path = "SirThaddeus.VoiceHost.exe";      Label = "VoiceHost exe" },
    @{ Path = "SirThaddeus.McpServer.exe";       Label = "McpServer exe" },
    @{ Path = "bin";                             Label = "bin/ directory" },
    @{ Path = "bin\voice";                       Label = "bin/voice/ directory" },
    @{ Path = "bin\voice\start-voice-backend.ps1"; Label = "Voice backend bootstrap script" },
    @{ Path = "bin\voice\server.py";             Label = "Voice backend server.py" },
    @{ Path = "bin\voice\requirements.txt";      Label = "Voice backend requirements.txt" },
    @{ Path = "bin\voice\piper\piper.exe";       Label = "Piper TTS binary" },
    @{ Path = "bin\voice\piper-voices\en_US-john-medium\en_US-john-medium.onnx"; Label = "Piper voice model" },
    @{ Path = "bin\voice\runtime\python\python.exe"; Label = "Bundled Python runtime" },
    @{ Path = "bin\voice\bin\uv.exe";            Label = "uv.exe (Python manager)" },
    @{ Path = "bin\voice\deps\wheels";           Label = "Bundled wheel directory" },
    @{ Path = "bin\voice\stt-models\base\model.bin"; Label = "Whisper STT model" },
    @{ Path = "bin\Fixtures";                    Label = "Fixtures directory" },
    @{ Path = "assets\manifest.json";            Label = "Asset manifest (self-heal)" },
    @{ Path = "README_FIRST_RUN.md";             Label = "README first run" }
)

foreach ($check in $checks) {
    $fullPath = Join-Path $DeployDir $check.Path
    if (Test-Path $fullPath) {
        $item = Get-Item $fullPath
        if ($item.PSIsContainer) {
            $count = (Get-ChildItem $fullPath -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
            Write-Ok "$($check.Label) ($count files)"
        } else {
            $sizeMB = [math]::Round($item.Length / 1MB, 1)
            Write-Ok "$($check.Label) ($sizeMB MB)"
        }
    } else {
        Write-Err "$($check.Label) NOT FOUND at $fullPath"
    }
}

# Count wheels
$wheelDir = Join-Path $DeployDir "bin\voice\deps\wheels"
if (Test-Path $wheelDir) {
    $wheelCount = (Get-ChildItem $wheelDir -Filter "*.whl" -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Info "  Wheel count: $wheelCount"
}

# ── 2. Settings file ────────────────────────────────────────────────
Write-Section "2. Settings"

$settingsDir = Join-Path $env:LOCALAPPDATA "SirThaddeus"
$settingsFile = Join-Path $settingsDir "SirThaddeus.Settings.json"
Write-Info "Settings directory: $settingsDir"
if (Test-Path $settingsFile) {
    Write-Ok "Settings file exists"
    try {
        $settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
        Write-Info "  MCP ServerPath: $($settings.mcp.serverPath)"
        Write-Info "  Voice.VoiceHostEnabled: $($settings.voice.voiceHostEnabled)"
        Write-Info "  Voice.VoiceHostBaseUrl: $($settings.voice.voiceHostBaseUrl)"
        Write-Info "  Voice.TtsEngine: $($settings.voice.ttsEngine)"
        Write-Info "  Voice.TtsVoiceId: $($settings.voice.ttsVoiceId)"
        Write-Info "  RuntimeSafety.SafeMode: $($settings.runtimeSafety.safeMode)"
        Write-Info "  RuntimeSafety.SafeModeReason: $($settings.runtimeSafety.safeModeReason)"
        Write-Info "  RuntimeSafety.StrictHandshake: $($settings.runtimeSafety.strictHandshake)"
    } catch {
        Write-Warn "Could not parse settings: $($_.Exception.Message)"
    }
} else {
    Write-Warn "Settings file not found (will use defaults)"
}

# ── 3. Log files ─────────────────────────────────────────────────────
Write-Section "3. Log Files"

$logFiles = @(
    @{ Path = (Join-Path $DeployDir "voicehost-debug.log"); Label = "VoiceHost debug log" },
    @{ Path = (Join-Path $settingsDir "audit.jsonl");       Label = "Audit log" },
    @{ Path = (Join-Path $settingsDir "voicehost-session.json"); Label = "VoiceHost session state" }
)

foreach ($log in $logFiles) {
    if (Test-Path $log.Path) {
        $size = (Get-Item $log.Path).Length
        Write-Ok "$($log.Label) ($size bytes)"
        if ($size -gt 0 -and $size -lt 50000) {
            Write-Host "--- Last 20 lines of $($log.Label) ---" -ForegroundColor DarkGray
            Get-Content $log.Path -Tail 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            Write-Host "---" -ForegroundColor DarkGray
        } elseif ($size -ge 50000) {
            Write-Host "--- Last 20 lines of $($log.Label) ---" -ForegroundColor DarkGray
            Get-Content $log.Path -Tail 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            Write-Host "---" -ForegroundColor DarkGray
        }
    } else {
        Write-Warn "$($log.Label) not found at $($log.Path)"
    }
}

# ── 4. Test MCP Server ──────────────────────────────────────────────
Write-Section "4. Test McpServer"

$mcpExe = Join-Path $DeployDir "SirThaddeus.McpServer.exe"
if (Test-Path $mcpExe) {
    Write-Info "Starting McpServer for 5 seconds..."
    try {
        $proc = Start-Process -FilePath $mcpExe -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\mcp-stdout.txt" -RedirectStandardError "$env:TEMP\mcp-stderr.txt"
        Start-Sleep -Seconds 5
        if ($proc.HasExited) {
            Write-Err "McpServer exited with code $($proc.ExitCode)"
        } else {
            Write-Ok "McpServer is running (PID: $($proc.Id))"
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Err "Failed to start McpServer: $($_.Exception.Message)"
    }

    foreach ($stream in @("stdout", "stderr")) {
        $logFile = "$env:TEMP\mcp-$stream.txt"
        if ((Test-Path $logFile) -and (Get-Item $logFile).Length -gt 0) {
            Write-Host "--- McpServer $stream ---" -ForegroundColor DarkGray
            Get-Content $logFile | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            Write-Host "---" -ForegroundColor DarkGray
        }
    }
} else {
    Write-Err "McpServer exe not found"
}

# ── 5. Test VoiceHost ───────────────────────────────────────────────
Write-Section "5. Test VoiceHost"

$voiceHostExe = Join-Path $DeployDir "SirThaddeus.VoiceHost.exe"
if (Test-Path $voiceHostExe) {
    Write-Info "Starting VoiceHost on port 18099 for 10 seconds..."
    try {
        $proc = Start-Process -FilePath $voiceHostExe -ArgumentList "--port 18099 --bind 127.0.0.1" -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\vh-stdout.txt" -RedirectStandardError "$env:TEMP\vh-stderr.txt"
        Start-Sleep -Seconds 10
        if ($proc.HasExited) {
            Write-Err "VoiceHost exited with code $($proc.ExitCode)"
        } else {
            Write-Ok "VoiceHost is running (PID: $($proc.Id))"
            
            # Try health check
            try {
                $health = Invoke-RestMethod -Uri "http://127.0.0.1:18099/health" -TimeoutSec 5
                Write-Ok "Health: ready=$($health.ready) asr=$($health.asrReady) tts=$($health.ttsReady)"
                if ($health.errorCode) { Write-Warn "errorCode: $($health.errorCode)" }
                if ($health.message) { Write-Warn "message: $($health.message)" }
            } catch {
                Write-Warn "Health check failed: $($_.Exception.Message)"
            }
            
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Err "Failed to start VoiceHost: $($_.Exception.Message)"
    }

    foreach ($stream in @("stdout", "stderr")) {
        $logFile = "$env:TEMP\vh-$stream.txt"
        if ((Test-Path $logFile) -and (Get-Item $logFile).Length -gt 0) {
            Write-Host "--- VoiceHost $stream ---" -ForegroundColor DarkGray
            Get-Content $logFile | Select-Object -First 30 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            Write-Host "---" -ForegroundColor DarkGray
        }
    }
} else {
    Write-Err "VoiceHost exe not found"
}

# ── 6. Test voice backend script directly ────────────────────────────
Write-Section "6. Test Voice Backend Script"

$backendScript = Join-Path $DeployDir "bin\voice\start-voice-backend.ps1"
if (Test-Path $backendScript) {
    Write-Info "Testing voice backend on port 18098 for 15 seconds..."
    Write-Info "Script path: $backendScript"
    try {
        $proc = Start-Process -FilePath "powershell" `
            -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$backendScript`" -Port 18098" `
            -WorkingDirectory (Join-Path $DeployDir "bin\voice") `
            -NoNewWindow -PassThru `
            -RedirectStandardOutput "$env:TEMP\vb-stdout.txt" `
            -RedirectStandardError "$env:TEMP\vb-stderr.txt"
        Start-Sleep -Seconds 15
        if ($proc.HasExited) {
            Write-Err "Voice backend exited with code $($proc.ExitCode)"
        } else {
            Write-Ok "Voice backend is running (PID: $($proc.Id))"
            try {
                $health = Invoke-RestMethod -Uri "http://127.0.0.1:18098/health" -TimeoutSec 5
                Write-Ok "Backend health: $($health | ConvertTo-Json -Compress)"
            } catch {
                Write-Warn "Backend health check failed: $($_.Exception.Message)"
            }
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Err "Failed to start voice backend: $($_.Exception.Message)"
    }

    foreach ($stream in @("stdout", "stderr")) {
        $logFile = "$env:TEMP\vb-$stream.txt"
        if ((Test-Path $logFile) -and (Get-Item $logFile).Length -gt 0) {
            $lineCount = (Get-Content $logFile | Measure-Object).Count
            Write-Host "--- Voice backend $stream ($lineCount lines) ---" -ForegroundColor DarkGray
            Get-Content $logFile | Select-Object -First 40 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            Write-Host "---" -ForegroundColor DarkGray
        }
    }
} else {
    Write-Err "Voice backend script not found"
}

Write-Section "Done"
Write-Host "  Copy the full output above and share it for debugging." -ForegroundColor Yellow
