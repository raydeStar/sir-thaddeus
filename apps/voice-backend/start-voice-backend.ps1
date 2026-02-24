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
    STT engine: faster-whisper (default).

.PARAMETER SttModelId
    STT model id. If omitted with faster-whisper, defaults to base.

.PARAMETER SttLanguage
    STT language pin (default: en). Use "auto" to enable detection.

.PARAMETER TtsEngine
    TTS engine: piper (default), kokoro (optional).

.PARAMETER TtsModelId
    Optional TTS model id.

.PARAMETER TtsVoiceId
    TTS voice id (e.g. en_US-ryan-medium for piper).

.PARAMETER RequireTtsReady
    When true, startup fails if TTS cannot be initialized.
    Default false keeps Whisper ASR available even when TTS is unavailable.

# .EXAMPLE
#     ./dev/start-voice-backend.ps1
#     ./dev/start-voice-backend.ps1 -SttEngine faster-whisper -SttModelId small -Device cuda
#     ./dev/start-voice-backend.ps1 -TtsEngine piper -TtsVoiceId en_US-ryan-medium
#>

param(
    [int]$Port = 8001,
    [string]$Model = "base",
    [string]$Device = "cpu",
    [string]$SttEngine = "faster-whisper",
    [string]$SttModelId = "",
    [string]$SttLanguage = "en",
    [string]$TtsEngine = "piper",
    [string]$TtsModelId = "",
    [string]$TtsVoiceId = "",
    [bool]$PrefetchVoiceAssets = $true,
    [bool]$PrefetchAsrAssets = $false,
    [bool]$PrefetchYouTubeAsrAssets = $false,
    [bool]$RequireTtsReady = $false
)

$ErrorActionPreference = "Stop"
$VoiceBackendDir = $PSScriptRoot
$VenvDir = Join-Path $VoiceBackendDir ".venv"
$BundledPythonExe = Join-Path $VoiceBackendDir "runtime\python\python.exe"
$BundledWheelsDir = Join-Path $VoiceBackendDir "deps\wheels"
$hasBundledWheelsFile = Get-ChildItem -Path $BundledWheelsDir -Filter "*.whl" -File -ErrorAction SilentlyContinue | Select-Object -First 1
$HasBundledWheels = $null -ne $hasBundledWheelsFile

$offlineRaw = (("" + $env:ST_VOICE_OFFLINE).Trim().ToLowerInvariant())
$VoiceOffline = @("1", "true", "yes", "on") -contains $offlineRaw

# Force Python to use UTF-8 for all text I/O. Without this, Windows uses the
# system locale encoding (e.g. cp1252) which chokes on non-ASCII bytes inside
# third-party packages like kokoro_onnx.
$env:PYTHONUTF8 = "1"

# Force Python to flush stdout/stderr immediately. Without this, when VoiceHost
# redirects output to a pipe, Python buffers logs and they won't appear in
# voice-backend-debug.log until the buffer fills or the process exits.
$env:PYTHONUNBUFFERED = "1"

# Suppress non-fatal Hugging Face symlink warnings on Windows machines
# without Developer Mode; caching still works in copy mode.
$env:HF_HUB_DISABLE_SYMLINKS_WARNING = "1"

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
    
    $uvDownloadSuccess = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Write-Host "  Attempt $attempt/3..." -ForegroundColor DarkGray
            Invoke-WebRequest -Uri $uvZipUrl -OutFile $uvZipPath -TimeoutSec 60
            $uvDownloadSuccess = $true
            break
        }
        catch {
            Write-Host "  [UV_DOWNLOAD_RETRY] Attempt $attempt failed: $($_.Exception.Message)" -ForegroundColor Yellow
            if ($attempt -lt 3) { Start-Sleep -Seconds 2 }
        }
    }
    if (-not $uvDownloadSuccess) {
        Write-Host "[UV_DOWNLOAD_FAILED] Could not download uv after 3 attempts. Check internet connectivity." -ForegroundColor Red
        exit 1
    }
    Expand-Archive -Path $uvZipPath -DestinationPath $BinDir -Force
    Remove-Item -Path $uvZipPath -Force
}

# ── Create venv via UV ───────────────────────────────────────────

if (-not (Test-Path "$VenvDir\Scripts\activate.ps1")) {
    if (Test-Path $BundledPythonExe) {
        Write-Host "Creating virtual environment (using bundled Python runtime)..." -ForegroundColor Yellow
        & $UvExe venv $VenvDir --python $BundledPythonExe
    }
    else {
        Write-Host "Creating virtual environment (downloading Python 3.11 if needed)..." -ForegroundColor Yellow
        & $UvExe venv $VenvDir --python 3.11
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to create venv." -ForegroundColor Red
        exit 1
    }
}

# ── Activate venv ────────────────────────────────────────────────

$activateScript = "$VenvDir\Scripts\Activate.ps1"
. $activateScript

# ── Validate venv Python works ───────────────────────────────────

$venvPython = "$VenvDir\Scripts\python.exe"
if (-not (Test-Path $venvPython)) {
    Write-Host "[VENV_BROKEN] python.exe not found in venv at '$venvPython'. Deleting venv for rebuild." -ForegroundColor Red
    Remove-Item -Recurse -Force $VenvDir -ErrorAction SilentlyContinue
    exit 1
}
try {
    $pyVersion = & $venvPython -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[VENV_BROKEN] python.exe in venv exits with error. Deleting venv for rebuild." -ForegroundColor Red
        Remove-Item -Recurse -Force $VenvDir -ErrorAction SilentlyContinue
        exit 1
    }
    Write-Host "[VENV_OK] Python $pyVersion" -ForegroundColor DarkGray
}
catch {
    Write-Host "[VENV_BROKEN] python.exe in venv cannot execute: $($_.Exception.Message)" -ForegroundColor Red
    Remove-Item -Recurse -Force $VenvDir -ErrorAction SilentlyContinue
    exit 1
}

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
    if ($VoiceOffline -and -not $HasBundledWheels) {
        Write-Host "[VOICE_DEPENDENCY_OFFLINE_MISSING] ST_VOICE_OFFLINE is enabled, but no bundled wheels were found at '$BundledWheelsDir'." -ForegroundColor Red
        exit 1
    }

    if ($HasBundledWheels) {
        Write-Host "Installing dependencies from bundled wheelhouse..." -ForegroundColor Yellow
        & $UvExe pip install --python "$VenvDir\Scripts\python.exe" -q --no-index --find-links "$BundledWheelsDir" -r $requirementsFile
    }
    else {
        Write-Host "Installing dependencies using uv..." -ForegroundColor Yellow
        & $UvExe pip install --python "$VenvDir\Scripts\python.exe" -q -r $requirementsFile
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: uv pip install failed." -ForegroundColor Red
        exit 1
    }

    Set-Content -Path $requirementsHashFile -Value $currentRequirementsHash -NoNewline
}
else {
    Write-Host "Dependencies already installed (requirements unchanged)." -ForegroundColor DarkGray
}

# ── Validate bundled assets (detect Git LFS pointer files) ───────────────────

function Test-LfsPointer {
    param([string]$FilePath)
    if (-not (Test-Path $FilePath)) { return $false }
    $size = (Get-Item $FilePath).Length
    # LFS pointer files are tiny text files (< 200 bytes)
    if ($size -gt 300) { return $false }
    try {
        $header = Get-Content -Path $FilePath -TotalCount 1 -ErrorAction SilentlyContinue
        return ($header -and $header.StartsWith("version https://git-lfs"))
    } catch { return $false }
}

$sttModelBin = Join-Path $VoiceBackendDir "stt-models\base\model.bin"
if (Test-Path $sttModelBin) {
    if (Test-LfsPointer $sttModelBin) {
        Write-Host "[ASSET_ERROR] stt-models\base\model.bin is a Git LFS pointer, not an actual model file!" -ForegroundColor Red
        Write-Host "  Run 'git lfs pull' in the repo root to download the real model files." -ForegroundColor Yellow
        if ($RequireTtsReady) { exit 1 }
    } else {
        $modelSize = [math]::Round((Get-Item $sttModelBin).Length / 1MB, 1)
        Write-Host "[ASSET_OK] STT model base/model.bin (${modelSize} MB)" -ForegroundColor DarkGray
    }
} else {
    Write-Host "[ASSET_INFO] STT model base/model.bin not found -- will download on first use." -ForegroundColor DarkGray
}

if ($resolvedTtsEngine -eq "piper" -and -not $ttsStartupDegraded) {
    $voiceOnnx = Join-Path $VoiceBackendDir ("piper-voices\" + $resolvedTtsVoiceId + "\" + $resolvedTtsVoiceId + ".onnx")
    if ((Test-Path $voiceOnnx) -and (Test-LfsPointer $voiceOnnx)) {
        Write-Host "[ASSET_ERROR] piper-voices\$resolvedTtsVoiceId\$resolvedTtsVoiceId.onnx is a Git LFS pointer!" -ForegroundColor Red
        Write-Host "  Run 'git lfs pull' in the repo root to download the real voice files." -ForegroundColor Yellow
        $ttsStartupDegraded = $true
    }
}

# ── Prefetch voice + ASR assets for fresh machines ──────────────────────────

if ($PrefetchVoiceAssets -or $PrefetchAsrAssets) {
    Write-Host "Preparing voice/ASR model assets (first run can take several minutes)..." -ForegroundColor Yellow

    $env:ST_PREFETCH_BACKEND_DIR = $VoiceBackendDir
    $env:ST_PREFETCH_VOICE_ID = $TtsVoiceId
    $env:ST_PREFETCH_STT_MODEL_ID = $SttModelId
    $env:ST_PREFETCH_STT_MODEL_ALIAS = $Model
    $env:ST_PREFETCH_DEVICE = $Device
    $env:ST_PREFETCH_VOICE_ASSETS = [string]$PrefetchVoiceAssets
    $env:ST_PREFETCH_ASR_ASSETS = [string]$PrefetchAsrAssets
    $env:ST_PREFETCH_YT_ASR_ASSETS = [string]$PrefetchYouTubeAsrAssets
    $env:ST_PREFETCH_STT_MODEL_ROOT = (Join-Path $VoiceBackendDir "stt-models")
    $env:ST_PREFETCH_OFFLINE_MODE = [string]$VoiceOffline

    $prefetchCode = @'
from pathlib import Path
import os
import sys

voice_backend_dir = Path(os.environ.get('ST_PREFETCH_BACKEND_DIR') or '')
stt_models_root = Path(os.environ.get('ST_PREFETCH_STT_MODEL_ROOT') or str(voice_backend_dir / 'stt-models'))
prefetch_voice_assets = (os.environ.get('ST_PREFETCH_VOICE_ASSETS') or '').strip().lower() == 'true'
prefetch_asr_assets = (os.environ.get('ST_PREFETCH_ASR_ASSETS') or '').strip().lower() == 'true'
prefetch_youtube_asr_assets = (os.environ.get('ST_PREFETCH_YT_ASR_ASSETS') or '').strip().lower() == 'true'
offline_mode = (os.environ.get('ST_PREFETCH_OFFLINE_MODE') or '').strip().lower() == 'true'
requested_voice_id = (os.environ.get('ST_PREFETCH_VOICE_ID') or '').strip() or 'en_US-ryan-medium'
requested_stt_model = ((os.environ.get('ST_PREFETCH_STT_MODEL_ID') or '').strip() or (os.environ.get('ST_PREFETCH_STT_MODEL_ALIAS') or '').strip() or 'base')
device = (os.environ.get('ST_PREFETCH_DEVICE') or 'cpu').strip().lower() or 'cpu'

backend_dir_str = str(voice_backend_dir)
if backend_dir_str:
    sys.path.insert(0, backend_dir_str)

if prefetch_voice_assets:
    # Piper voices are pre-bundled; no Python-based download needed.
    print(f"[VOICE_PREFETCH] Piper voices are bundled -- skipping download prefetch.")

if prefetch_asr_assets:
    try:
        from faster_whisper import WhisperModel
        print(f"[ASR_PREFETCH] Ensuring faster-whisper model '{requested_stt_model}' on {device} (download_root={stt_models_root}, local_only={offline_mode})...")
        _ = WhisperModel(
            requested_stt_model,
            device=device,
            compute_type='int8',
            download_root=str(stt_models_root),
            local_files_only=offline_mode,
        )
        # Model construction is enough to ensure assets exist locally.
        # Avoid running a warmup transcribe here; on some fresh Windows
        # systems this can trigger a native crash in ctranslate2.
    except Exception as exc:
        print(f"[ASR_PREFETCH_WARNING] faster-whisper preload failed: {exc}")

    if prefetch_youtube_asr_assets:
        print("[ASR_PREFETCH_INFO] whisper-only mode enabled; skipping optional YouTube qwen-asr prefetch.")
'@

    $prefetchFile = Join-Path $env:TEMP ("st-prefetch-" + [guid]::NewGuid().ToString("N") + ".py")
    Set-Content -Path $prefetchFile -Value $prefetchCode -Encoding UTF8
    try {
        & "$VenvDir\Scripts\python.exe" $prefetchFile
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[PREFETCH_WARNING] Prefetch script exited with code $LASTEXITCODE. Server will attempt to start anyway." -ForegroundColor Yellow
        }
    }
    finally {
        Remove-Item -Path $prefetchFile -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_BACKEND_DIR -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_VOICE_ID -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_STT_MODEL_ID -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_STT_MODEL_ALIAS -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_DEVICE -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_VOICE_ASSETS -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_ASR_ASSETS -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_YT_ASR_ASSETS -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_STT_MODEL_ROOT -ErrorAction SilentlyContinue
        Remove-Item Env:ST_PREFETCH_OFFLINE_MODE -ErrorAction SilentlyContinue
    }
}

# ── Start server ─────────────────────────────────────────────────

$resolvedSttModel = if ([string]::IsNullOrWhiteSpace($SttModelId)) { $Model } else { $SttModelId.Trim() }
$resolvedTtsEngine = if ([string]::IsNullOrWhiteSpace($TtsEngine)) { "piper" } else { $TtsEngine.Trim().ToLowerInvariant() }
$resolvedTtsModelId = if ([string]::IsNullOrWhiteSpace($TtsModelId)) { "" } else { $TtsModelId.Trim() }
$resolvedTtsVoiceId = if ([string]::IsNullOrWhiteSpace($TtsVoiceId)) { "" } else { $TtsVoiceId.Trim() }
$ttsStartupDegraded = $false

# ── Piper TTS file check ─────────────────────────────────────────
# Piper uses a standalone native exe -- no Python dependency, no DLL hell.
# Just verify the exe and voice model files are present.
if ($resolvedTtsEngine -eq "piper") {
    if ([string]::IsNullOrWhiteSpace($resolvedTtsVoiceId)) {
        $resolvedTtsVoiceId = "en_US-ryan-medium"
        Write-Host "[VOICE_TTS_DEFAULT_APPLIED] No Piper voice specified; defaulting to '$resolvedTtsVoiceId'." -ForegroundColor DarkGray
    }

    $piperExe = Join-Path $VoiceBackendDir "piper\piper.exe"
    $piperModel = Join-Path $VoiceBackendDir ("piper-voices\" + $resolvedTtsVoiceId + "\" + $resolvedTtsVoiceId + ".onnx")
    $piperConfig = Join-Path $VoiceBackendDir ("piper-voices\" + $resolvedTtsVoiceId + "\" + $resolvedTtsVoiceId + ".onnx.json")

    if (-not (Test-Path $piperExe)) {
        Write-Host "[VOICE_TTS_MISSING] piper.exe not found at '$piperExe'." -ForegroundColor Yellow
        if ($RequireTtsReady) {
            Write-Host "[VOICE_TTS_REQUIRED_UNAVAILABLE] Piper exe is missing. Aborting startup." -ForegroundColor Red
            exit 1
        }
        $ttsStartupDegraded = $true
    }
    elseif (-not ((Test-Path $piperModel) -and (Test-Path $piperConfig))) {
        Write-Host "[VOICE_TTS_MISSING] Piper voice model not found for '$resolvedTtsVoiceId'." -ForegroundColor Yellow
        if ($RequireTtsReady) {
            Write-Host "[VOICE_TTS_REQUIRED_UNAVAILABLE] Piper voice files missing. Aborting startup." -ForegroundColor Red
            exit 1
        }
        $ttsStartupDegraded = $true
    }
    else {
        Write-Host "[VOICE_TTS_READY] Piper exe and voice '$resolvedTtsVoiceId' are present." -ForegroundColor DarkGray
    }
}
elseif ($resolvedTtsEngine -eq "kokoro") {
    # Kokoro is still supported but no longer default or bundled.
    # The server.py KokoroProvider handles all runtime detection.
    Write-Host "[VOICE_TTS_ENGINE] Kokoro TTS requested (user-installed). Runtime validation deferred to server." -ForegroundColor DarkGray
}

if ($ttsStartupDegraded) {
    Write-Host "[VOICE_TTS_DEGRADED_MODE] TTS is unavailable; backend will still start for Whisper ASR. /tts requests may fail." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  Voice Backend starting on http://127.0.0.1:$Port" -ForegroundColor Green
Write-Host "  STT: $SttEngine  model: $resolvedSttModel  lang: $SttLanguage  device: $Device" -ForegroundColor Green
Write-Host "  TTS: $resolvedTtsEngine  model: $(if ($resolvedTtsModelId) { $resolvedTtsModelId } else { '<none>' })  voice: $(if ($resolvedTtsVoiceId) { $resolvedTtsVoiceId } else { '<none>' })" -ForegroundColor Green
Write-Host "  Press Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

# ── Kill stale processes holding our port ─────────────────────────
# When the supervisor restarts this script, a previous python process may
# still be holding the port.  Kill it before we try to bind.
try {
    $staleListeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    foreach ($conn in $staleListeners) {
        $stalePid = $conn.OwningProcess
        if ($stalePid -and $stalePid -ne $PID -and $stalePid -ne 0) {
            Write-Host "[PORT_CLEANUP] Killing stale process $stalePid on port $Port" -ForegroundColor Yellow
            try { Stop-Process -Id $stalePid -Force -ErrorAction SilentlyContinue } catch {}
            Start-Sleep -Milliseconds 500
        }
    }
}
catch {
    # Get-NetTCPConnection may not be available on all systems; proceed anyway.
}

$env:WHISPER_MODEL = $resolvedSttModel
$env:WHISPER_DEVICE = $Device
$env:ST_VOICE_STT_ENGINE = $SttEngine
$env:ST_VOICE_STT_MODEL_ID = $resolvedSttModel
$env:ST_VOICE_STT_MODEL_ROOT = (Join-Path $VoiceBackendDir "stt-models")
$env:ST_VOICE_STT_LANGUAGE = $SttLanguage
$env:ST_VOICE_TTS_ENGINE = $resolvedTtsEngine
$env:ST_VOICE_TTS_MODEL_ID = $resolvedTtsModelId
$env:ST_VOICE_TTS_VOICE_ID = $resolvedTtsVoiceId
$env:ST_VOICE_OFFLINE = [string]$VoiceOffline

if ($VoiceOffline) {
    $env:HF_HUB_OFFLINE = "1"
    $env:TRANSFORMERS_OFFLINE = "1"
}

$env:HF_HUB_DISABLE_SYMLINKS_WARNING = "1"

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
