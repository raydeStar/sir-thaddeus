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
    TTS engine: kokoro (default).

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
    [string]$TtsEngine = "kokoro",
    [string]$TtsModelId = "",
    [string]$TtsVoiceId = "",
    [bool]$PrefetchVoiceAssets = $true,
    [bool]$PrefetchAsrAssets = $true,
    [bool]$PrefetchYouTubeAsrAssets = $false
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

    $prefetchCode = @'
from pathlib import Path
import os

voice_backend_dir = Path(os.environ.get('ST_PREFETCH_BACKEND_DIR') or '')
voices_root = voice_backend_dir / 'voices'
registry_path = voice_backend_dir / 'model_registry.json'
prefetch_voice_assets = (os.environ.get('ST_PREFETCH_VOICE_ASSETS') or '').strip().lower() == 'true'
prefetch_asr_assets = (os.environ.get('ST_PREFETCH_ASR_ASSETS') or '').strip().lower() == 'true'
prefetch_youtube_asr_assets = (os.environ.get('ST_PREFETCH_YT_ASR_ASSETS') or '').strip().lower() == 'true'
requested_voice_id = (os.environ.get('ST_PREFETCH_VOICE_ID') or '').strip() or 'bm_lewis'
requested_stt_model = ((os.environ.get('ST_PREFETCH_STT_MODEL_ID') or '').strip() or (os.environ.get('ST_PREFETCH_STT_MODEL_ALIAS') or '').strip() or 'base')
device = (os.environ.get('ST_PREFETCH_DEVICE') or 'cpu').strip().lower() or 'cpu'
variant = (os.environ.get('KOKORO_MODEL_VARIANT') or '').strip() or None

if prefetch_voice_assets:
    from model_downloader import ensure_kokoro_models
    try:
        print(f"[VOICE_PREFETCH] Ensuring Kokoro assets for '{requested_voice_id}'...")
        ensure_kokoro_models(voices_root, requested_voice_id, registry_path, variant=variant)
    except Exception as exc:
        print(f"[VOICE_PREFETCH_WARNING] Failed for '{requested_voice_id}': {exc}")

if prefetch_asr_assets:
    try:
        from faster_whisper import WhisperModel
        print(f"[ASR_PREFETCH] Ensuring faster-whisper model '{requested_stt_model}' on {device}...")
        whisper = WhisperModel(requested_stt_model, device=device, compute_type='int8')
        try:
            _ = whisper.transcribe(b"", language='en')
        except Exception:
            pass
    except Exception as exc:
        print(f"[ASR_PREFETCH_WARNING] faster-whisper preload failed: {exc}")

    if prefetch_youtube_asr_assets:
        print("[ASR_PREFETCH_INFO] whisper-only mode enabled; skipping optional YouTube qwen-asr prefetch.")
'@

    $prefetchFile = Join-Path $env:TEMP ("st-prefetch-" + [guid]::NewGuid().ToString("N") + ".py")
    Set-Content -Path $prefetchFile -Value $prefetchCode -Encoding UTF8
    try {
        & "$VenvDir\Scripts\python.exe" $prefetchFile
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
    }
}

function Repair-KokoroRuntimeIfNeeded {
    param(
        [string]$ProbeFailureOutput
    )

    if ([string]::IsNullOrWhiteSpace($ProbeFailureOutput)) {
        return $false
    }

    $needsRepair = $ProbeFailureOutput -match 'onnxruntime_pybind11_state' -or $ProbeFailureOutput -match 'DLL load failed'
    if (-not $needsRepair) {
        return $false
    }

    Write-Host "[VOICE_TTS_RUNTIME_REPAIR] Detected onnxruntime/DLL import issue. Attempting one-time runtime repair..." -ForegroundColor Yellow
    & $UvExe pip install --python "$VenvDir\Scripts\python.exe" -q --upgrade --force-reinstall onnxruntime "msvc-runtime; platform_system == 'Windows'"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[VOICE_TTS_RUNTIME_REPAIR_WARNING] Runtime repair install failed." -ForegroundColor Yellow
        return $false
    }

    Write-Host "[VOICE_TTS_RUNTIME_REPAIR] Runtime packages refreshed." -ForegroundColor DarkGray
    return $true
}

# ── Start server ─────────────────────────────────────────────────

$resolvedSttModel = if ([string]::IsNullOrWhiteSpace($SttModelId)) { $Model } else { $SttModelId.Trim() }
$resolvedTtsEngine = if ([string]::IsNullOrWhiteSpace($TtsEngine)) { "kokoro" } else { $TtsEngine.Trim().ToLowerInvariant() }
$resolvedTtsModelId = if ([string]::IsNullOrWhiteSpace($TtsModelId)) { "" } else { $TtsModelId.Trim() }
$resolvedTtsVoiceId = if ([string]::IsNullOrWhiteSpace($TtsVoiceId)) { "" } else { $TtsVoiceId.Trim() }

if ($resolvedTtsEngine -ne "kokoro") {
    Write-Host "[VOICE_TTS_ENGINE_FORCED] Non-kokoro TTS engine '$resolvedTtsEngine' requested; forcing kokoro." -ForegroundColor Yellow
    $resolvedTtsEngine = "kokoro"
}

# Fresh machines can have Kokoro selected in settings but no local voice bundle yet.
# We require Kokoro for startup readiness and fail fast if no usable Kokoro
# assets can be resolved.
if ($resolvedTtsEngine -eq "kokoro") {
    if ([string]::IsNullOrWhiteSpace($resolvedTtsVoiceId)) {
        $resolvedTtsVoiceId = "bm_lewis"
        Write-Host "[VOICE_TTS_DEFAULT_APPLIED] No Kokoro voice specified; defaulting to '$resolvedTtsVoiceId'." -ForegroundColor DarkGray
    }

    $voiceModel = Join-Path $VoiceBackendDir ("voices\\" + $resolvedTtsVoiceId + "\\model.onnx")
    $voiceBundle = Join-Path $VoiceBackendDir ("voices\\" + $resolvedTtsVoiceId + "\\voices.bin")
    $hasRequestedVoiceAssets = (Test-Path $voiceModel) -and (Test-Path $voiceBundle)
    $fallbackAssetDir = Get-ChildItem -Path (Join-Path $VoiceBackendDir "voices") -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            (Test-Path (Join-Path $_.FullName "model.onnx")) -and
            (Test-Path (Join-Path $_.FullName "voices.bin"))
        } |
        Select-Object -First 1
    $hasAnyKokoroAssets = $null -ne $fallbackAssetDir

    if (-not $hasRequestedVoiceAssets) {
        if ($hasAnyKokoroAssets) {
            Write-Host "[VOICE_TTS_SHARED_ASSETS] No dedicated pack for '$resolvedTtsVoiceId'; using shared Kokoro assets from '$($fallbackAssetDir.Name)'." -ForegroundColor DarkGray
            $resolvedTtsVoiceId = $fallbackAssetDir.Name
        }
        else {
            Write-Host "[VOICE_TTS_ASSET_DOWNLOAD_ATTEMPT] Kokoro assets are missing for voice '$resolvedTtsVoiceId'; downloading now (startup blocks until complete)." -ForegroundColor Yellow

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

            & "$VenvDir\Scripts\python.exe" -c $pythonCode
            if ($LASTEXITCODE -ne 0) {
                Write-Host "[VOICE_TTS_REQUIRED_UNAVAILABLE] Failed to download Kokoro assets for '$resolvedTtsVoiceId'. Aborting startup." -ForegroundColor Red
                exit 1
            }

            $hasRequestedVoiceAssets = (Test-Path $voiceModel) -and (Test-Path $voiceBundle)
            if (-not $hasRequestedVoiceAssets) {
                Write-Host "[VOICE_TTS_REQUIRED_UNAVAILABLE] Kokoro assets are still missing after download attempt for '$resolvedTtsVoiceId'. Aborting startup." -ForegroundColor Red
                exit 1
            }

            Write-Host "[VOICE_TTS_ASSET_READY] Kokoro assets are ready for '$resolvedTtsVoiceId'." -ForegroundColor DarkGray
        }
    }

    $resolvedVoiceModel = Join-Path $VoiceBackendDir ("voices\\" + $resolvedTtsVoiceId + "\\model.onnx")
    $resolvedVoiceBundle = Join-Path $VoiceBackendDir ("voices\\" + $resolvedTtsVoiceId + "\\voices.bin")
    if (-not ((Test-Path $resolvedVoiceModel) -and (Test-Path $resolvedVoiceBundle))) {
        Write-Host "[VOICE_TTS_REQUIRED_UNAVAILABLE] Kokoro startup requires usable assets, but none were found for resolved voice '$resolvedTtsVoiceId'. Aborting startup." -ForegroundColor Red
        exit 1
    }

    $env:ST_KOKORO_PROBE_BACKEND_DIR = $VoiceBackendDir
    $env:ST_KOKORO_PROBE_VOICE_ID = $resolvedTtsVoiceId

    $kokoroProbeCode = @'
from pathlib import Path
import os
import sys

voice_backend_dir = Path(os.environ.get('ST_KOKORO_PROBE_BACKEND_DIR') or '')
voice_id = (os.environ.get('ST_KOKORO_PROBE_VOICE_ID') or '').strip() or 'bm_lewis'
voice_dir = voice_backend_dir / 'voices' / voice_id
model_path = voice_dir / 'model.onnx'
voices_path = voice_dir / 'voices.bin'

try:
    from kokoro_onnx import Kokoro

    runtime = Kokoro(str(model_path), str(voices_path))
    try:
        result = runtime.create('Voice engine initialization check.', voice=voice_id, speed=1.0, lang='en-us')
    except TypeError:
        result = runtime.create('Voice engine initialization check.', voice=voice_id)

    if result is None:
        raise RuntimeError('kokoro_probe_empty_result')
except Exception as exc:
    print('[VOICE_TTS_REQUIRED_UNAVAILABLE] Kokoro probe failed for %r: %s' % (voice_id, exc))
    sys.exit(1)
'@

    $kokoroProbeFile = Join-Path $env:TEMP ("st-kokoro-probe-" + [guid]::NewGuid().ToString("N") + ".py")
    Set-Content -Path $kokoroProbeFile -Value $kokoroProbeCode -Encoding UTF8

    $probeExitCode = 0
    $probeOutput = ''
    $probeAttemptedRepair = $false
    try {
        $probeOutput = (& "$VenvDir\Scripts\python.exe" $kokoroProbeFile 2>&1 | Out-String)
        if (-not [string]::IsNullOrWhiteSpace($probeOutput)) {
            Write-Host $probeOutput.TrimEnd()
        }
        $probeExitCode = $LASTEXITCODE

        if ($probeExitCode -ne 0) {
            $probeAttemptedRepair = Repair-KokoroRuntimeIfNeeded -ProbeFailureOutput $probeOutput
            if ($probeAttemptedRepair) {
                $probeOutput = (& "$VenvDir\Scripts\python.exe" $kokoroProbeFile 2>&1 | Out-String)
                if (-not [string]::IsNullOrWhiteSpace($probeOutput)) {
                    Write-Host $probeOutput.TrimEnd()
                }
                $probeExitCode = $LASTEXITCODE
            }
        }
    }
    finally {
        Remove-Item -Path $kokoroProbeFile -ErrorAction SilentlyContinue
        Remove-Item Env:ST_KOKORO_PROBE_BACKEND_DIR -ErrorAction SilentlyContinue
        Remove-Item Env:ST_KOKORO_PROBE_VOICE_ID -ErrorAction SilentlyContinue
    }

    if ($probeExitCode -ne 0) {
        if ($probeAttemptedRepair) {
            Write-Host "[VOICE_TTS_REQUIRED_UNAVAILABLE] Kokoro runtime probe still failed after repair attempt. Aborting startup." -ForegroundColor Red
        }
        else {
            Write-Host "[VOICE_TTS_REQUIRED_UNAVAILABLE] Kokoro runtime probe failed. Aborting startup." -ForegroundColor Red
        }
        exit 1
    }

    Write-Host "[VOICE_TTS_PROBE_READY] Kokoro runtime probe passed for '$resolvedTtsVoiceId'." -ForegroundColor DarkGray
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
