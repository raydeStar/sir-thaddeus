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
$VoiceBackendDir = Join-Path $PSScriptRoot ".." "apps" "voice-backend"
$VenvDir = Join-Path $VoiceBackendDir ".venv"

# ── Check Python ─────────────────────────────────────────────────

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    Write-Host "ERROR: Python not found. Install Python 3.10+ and try again." -ForegroundColor Red
    exit 1
}

$pyVersion = & python --version 2>&1
Write-Host "Using $pyVersion" -ForegroundColor Cyan

# ── Create venv if missing ───────────────────────────────────────

if (-not (Test-Path (Join-Path $VenvDir "Scripts" "activate.ps1"))) {
    Write-Host "Creating virtual environment..." -ForegroundColor Yellow
    & python -m venv $VenvDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to create venv." -ForegroundColor Red
        exit 1
    }
}

# ── Activate venv ────────────────────────────────────────────────

$activateScript = Join-Path $VenvDir "Scripts" "Activate.ps1"
. $activateScript

# ── Install / update dependencies ────────────────────────────────

$requirementsFile = Join-Path $VoiceBackendDir "requirements.txt"
Write-Host "Installing dependencies..." -ForegroundColor Yellow
& pip install -q -r $requirementsFile
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: pip install failed." -ForegroundColor Red
    exit 1
}

# ── Start server ─────────────────────────────────────────────────

Write-Host ""
Write-Host "  Voice Backend starting on http://127.0.0.1:$Port" -ForegroundColor Green
Write-Host "  STT: $SttEngine  model: $(if ($SttModelId) { $SttModelId } else { $Model })  lang: $SttLanguage  device: $Device" -ForegroundColor Green
Write-Host "  TTS: $TtsEngine  model: $(if ($TtsModelId) { $TtsModelId } else { '<none>' })  voice: $(if ($TtsVoiceId) { $TtsVoiceId } else { '<none>' })" -ForegroundColor Green
Write-Host "  Press Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

$resolvedSttModel = if ([string]::IsNullOrWhiteSpace($SttModelId)) { $Model } else { $SttModelId.Trim() }

$env:WHISPER_MODEL = $resolvedSttModel
$env:WHISPER_DEVICE = $Device
$env:ST_VOICE_STT_ENGINE = $SttEngine
$env:ST_VOICE_STT_MODEL_ID = $resolvedSttModel
$env:ST_VOICE_STT_LANGUAGE = $SttLanguage
$env:ST_VOICE_TTS_ENGINE = $TtsEngine
$env:ST_VOICE_TTS_MODEL_ID = $TtsModelId
$env:ST_VOICE_TTS_VOICE_ID = $TtsVoiceId

$serverScript = Join-Path $VoiceBackendDir "server.py"
$pythonArgs = @(
    $serverScript,
    "--port", "$Port",
    "--stt-engine", "$SttEngine",
    "--stt-model-id", "$resolvedSttModel",
    "--stt-language", "$SttLanguage",
    "--device", "$Device",
    "--tts-engine", "$TtsEngine"
)
if (-not [string]::IsNullOrWhiteSpace($TtsModelId)) {
    $pythonArgs += @("--tts-model-id", $TtsModelId.Trim())
}
if (-not [string]::IsNullOrWhiteSpace($TtsVoiceId)) {
    $pythonArgs += @("--tts-voice-id", $TtsVoiceId.Trim())
}

& python @pythonArgs
