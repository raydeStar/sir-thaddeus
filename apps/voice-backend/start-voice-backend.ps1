<#
.SYNOPSIS
    Starts the local voice backend with deterministic STT/TTS engine settings.

.DESCRIPTION
    Creates a Python virtual environment if needed, installs dependencies,
    and launches the FastAPI voice backend server. VoiceHost connects to
    this service for speech-to-text transcription.

.PARAMETER Port
    Listen port (default: 8001).

.PARAMETER Model
    Backward-compat alias for STT model id.

.PARAMETER Device
    Compute device: cpu or cuda (default: cpu).

.PARAMETER SttEngine
    STT engine: faster-whisper (default) or qwen3asr.

.PARAMETER SttModelId
    STT model id. If omitted with faster-whisper, defaults to base.

.PARAMETER SttLanguage
    STT language pin (default: en). Use "auto" to enable detection.

.PARAMETER TtsEngine
    TTS engine: windows (default) or kokoro.

.PARAMETER TtsModelId
    Optional TTS model id.

.PARAMETER TtsVoiceId
    TTS voice id. Required for kokoro engine.

# .EXAMPLE
#     ./dev/start-voice-backend.ps1
#     ./dev/start-voice-backend.ps1 -SttEngine faster-whisper -SttModelId small -Device cuda
#     ./dev/start-voice-backend.ps1 -TtsEngine kokoro -TtsVoiceId af_sky
#>

param(
    [int]$Port = 8001,
    [string]$Model = "base",
    [string]$Device = "cpu",
    [string]$SttEngine = "faster-whisper",
    [string]$SttModelId = "",
    [string]$SttLanguage = "en",
    [string]$TtsEngine = "windows",
    [string]$TtsModelId = "",
    [string]$TtsVoiceId = ""
)

$ErrorActionPreference = "Stop"
$VoiceBackendDir = $PSScriptRoot
$VenvDir = Join-Path $VoiceBackendDir ".venv"

$BinDir = Join-Path $VoiceBackendDir "bin"
$UvExe = Join-Path $BinDir "uv.exe"

# ── Bootstrap UV ─────────────────────────────────────────────────

if (-not (Test-Path $UvExe)) {
    Write-Host "Downloading 'uv' (fast Python manager)..." -ForegroundColor Yellow
    if (-not (Test-Path $BinDir)) {
        New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
    }
    
    $uvZipUrl = "https://github.com/astral-sh/uv/releases/download/0.5.21/uv-x86_64-pc-windows-msvc.zip"
    $uvZipPath = Join-Path $BinDir "uv.zip"
    
    Invoke-WebRequest -Uri $uvZipUrl -OutFile $uvZipPath
    Expand-Archive -Path $uvZipPath -DestinationPath $BinDir -Force
    Remove-Item -Path $uvZipPath -Force
}

# ── Create venv via UV ───────────────────────────────────────────

if (-not (Test-Path "$VenvDir\Scripts\activate.ps1")) {
    Write-Host "Creating virtual environment (downloading Python 3.11 if needed)..." -ForegroundColor Yellow
    & $UvExe venv $VenvDir --python 3.11
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to create venv." -ForegroundColor Red
        exit 1
    }
}

# ── Activate venv ────────────────────────────────────────────────

$activateScript = "$VenvDir\Scripts\Activate.ps1"
. $activateScript

# ── Install / update dependencies ────────────────────────────────

$requirementsFile = Join-Path $VoiceBackendDir "requirements.txt"
$requirementsHashFile = Join-Path $VenvDir ".requirements.sha256"
$currentRequirementsHash = (Get-FileHash -Algorithm SHA256 $requirementsFile).Hash
$storedRequirementsHash = ""
if (Test-Path $requirementsHashFile) {
    try {
        $storedRequirementsHash = (Get-Content -Path $requirementsHashFile -Raw).Trim()
    }
    catch {
        $storedRequirementsHash = ""
    }
}

if ($storedRequirementsHash -ne $currentRequirementsHash) {
    Write-Host "Installing dependencies using uv..." -ForegroundColor Yellow
    & $UvExe pip install --python "$VenvDir\Scripts\python.exe" -q -r $requirementsFile
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: uv pip install failed." -ForegroundColor Red
        exit 1
    }

    Set-Content -Path $requirementsHashFile -Value $currentRequirementsHash -NoNewline
}
else {
    Write-Host "Dependencies already installed (requirements unchanged)." -ForegroundColor DarkGray
}

# ── Start server ─────────────────────────────────────────────────

$resolvedSttModel = if ([string]::IsNullOrWhiteSpace($SttModelId)) { $Model } else { $SttModelId.Trim() }
$resolvedTtsEngine = if ([string]::IsNullOrWhiteSpace($TtsEngine)) { "windows" } else { $TtsEngine.Trim().ToLowerInvariant() }
$resolvedTtsModelId = if ([string]::IsNullOrWhiteSpace($TtsModelId)) { "" } else { $TtsModelId.Trim() }
$resolvedTtsVoiceId = if ([string]::IsNullOrWhiteSpace($TtsVoiceId)) { "" } else { $TtsVoiceId.Trim() }

# Fresh machines can have Kokoro selected in settings but no local voice bundle yet.
# If assets for the requested Kokoro voice are missing, degrade to Windows TTS so
# the app still becomes ready instead of remaining stuck in perpetual warmup.
if ($resolvedTtsEngine -eq "kokoro") {
    if ([string]::IsNullOrWhiteSpace($resolvedTtsVoiceId)) {
        $resolvedTtsVoiceId = "bm_lewis"
        Write-Host "[VOICE_TTS_DEFAULT_APPLIED] No Kokoro voice specified; defaulting to '$resolvedTtsVoiceId'." -ForegroundColor DarkGray
    }

    $voiceManifest = Join-Path $VoiceBackendDir ("voices\\" + $resolvedTtsVoiceId + "\\manifest.json")
    if (-not (Test-Path $voiceManifest)) {
        Write-Host "[VOICE_TTS_ASSET_DOWNLOAD_ATTEMPT] Kokoro assets are missing for voice '$resolvedTtsVoiceId'; starting background auto-download." -ForegroundColor Yellow

        $pythonCode = @"
from pathlib import Path
import os
from model_downloader import ensure_kokoro_models

voice_backend_dir = Path(r'$VoiceBackendDir')
voices_root = voice_backend_dir / 'voices'
registry_path = voice_backend_dir / 'model_registry.json'
voice_id = r'$resolvedTtsVoiceId'
variant = (os.environ.get('KOKORO_MODEL_VARIANT') or '').strip() or None

ensure_kokoro_models(voices_root, voice_id, registry_path, variant=variant)
"@

        try {
            Start-Process -FilePath "$VenvDir\Scripts\python.exe" -ArgumentList @("-c", $pythonCode) -WorkingDirectory $VoiceBackendDir -WindowStyle Hidden | Out-Null
            Write-Host "[VOICE_TTS_ASSET_DOWNLOAD_BACKGROUND_STARTED] Kokoro asset download launched for '$resolvedTtsVoiceId'." -ForegroundColor DarkGray
        }
        catch {
            Write-Host "[VOICE_TTS_ASSET_DOWNLOAD_FAILED] Failed to launch Kokoro asset download for '$resolvedTtsVoiceId': $($_.Exception.Message)" -ForegroundColor Yellow
        }

        Write-Host "[VOICE_TTS_FALLBACK_APPLIED] Kokoro assets are not installed yet for '$resolvedTtsVoiceId'; falling back to Windows TTS for readiness." -ForegroundColor Yellow
        $resolvedTtsEngine = "windows"
        $resolvedTtsVoiceId = ""
        $resolvedTtsModelId = ""
    }
}

Write-Host ""
Write-Host "  Voice Backend starting on http://127.0.0.1:$Port" -ForegroundColor Green
Write-Host "  STT: $SttEngine  model: $resolvedSttModel  lang: $SttLanguage  device: $Device" -ForegroundColor Green
Write-Host "  TTS: $resolvedTtsEngine  model: $(if ($resolvedTtsModelId) { $resolvedTtsModelId } else { '<none>' })  voice: $(if ($resolvedTtsVoiceId) { $resolvedTtsVoiceId } else { '<none>' })" -ForegroundColor Green
Write-Host "  Press Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

$env:WHISPER_MODEL = $resolvedSttModel
$env:WHISPER_DEVICE = $Device
$env:ST_VOICE_STT_ENGINE = $SttEngine
$env:ST_VOICE_STT_MODEL_ID = $resolvedSttModel
$env:ST_VOICE_STT_LANGUAGE = $SttLanguage
$env:ST_VOICE_TTS_ENGINE = $resolvedTtsEngine
$env:ST_VOICE_TTS_MODEL_ID = $resolvedTtsModelId
$env:ST_VOICE_TTS_VOICE_ID = $resolvedTtsVoiceId

$serverScript = Join-Path $VoiceBackendDir "server.py"
$pythonArgs = @(
    $serverScript,
    "--port", "$Port",
    "--stt-engine", "$SttEngine",
    "--stt-model-id", "$resolvedSttModel",
    "--stt-language", "$SttLanguage",
    "--device", "$Device",
    "--tts-engine", "$resolvedTtsEngine"
)
if (-not [string]::IsNullOrWhiteSpace($resolvedTtsModelId)) {
    $pythonArgs += @("--tts-model-id", $resolvedTtsModelId)
}
if (-not [string]::IsNullOrWhiteSpace($resolvedTtsVoiceId)) {
    $pythonArgs += @("--tts-voice-id", $resolvedTtsVoiceId)
}

& "$VenvDir\Scripts\python.exe" @pythonArgs
