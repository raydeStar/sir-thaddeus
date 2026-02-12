<#
.SYNOPSIS
    Starts the local voice backend (ASR + TTS stub) on port 8001.

.DESCRIPTION
    Creates a Python virtual environment if needed, installs dependencies,
    and launches the FastAPI voice backend server. VoiceHost connects to
    this service for speech-to-text transcription.

.PARAMETER Port
    Listen port (default: 8001).

.PARAMETER Model
    Whisper model size: tiny, base, small, medium, large-v3 (default: base).
    Larger models are more accurate but slower and use more memory.

.PARAMETER Device
    Compute device: cpu or cuda (default: cpu).

.EXAMPLE
    ./dev/start-voice-backend.ps1
    ./dev/start-voice-backend.ps1 -Model small -Device cuda
#>

param(
    [int]$Port = 8001,
    [string]$Model = "base",
    [string]$Device = "cpu"
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
Write-Host "  Model: $Model   Device: $Device" -ForegroundColor Green
Write-Host "  Press Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

$env:WHISPER_MODEL = $Model
$env:WHISPER_DEVICE = $Device

$serverScript = Join-Path $VoiceBackendDir "server.py"
& python $serverScript --port $Port --model $Model --device $Device
