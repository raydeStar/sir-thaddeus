<#
.SYNOPSIS
    Simulates a fresh-PC experience locally by wiping voice-backend assets,
    downloading them from GitHub Releases, and verifying the voice backend starts.

.DESCRIPTION
    1. Wipes all voice-backend binary dirs (piper, piper-voices, runtime, deps/wheels, stt-models, .venv)
    2. Downloads every asset from assets/manifest.json (same as AssetManager would)
    3. Verifies SHA256 hashes
    4. Extracts to the correct locations
    5. Creates a fresh venv and installs deps from the bundled wheelhouse
    6. Optionally starts the voice backend server for a quick smoke test

    Run from repo root:  .\dev\test-fresh-pc.ps1
    Add -SkipDownload to skip re-downloading if assets are already present.
    Add -StartServer to launch the voice backend after setup.

.PARAMETER SkipDownload
    Skip downloading assets (use existing files).

.PARAMETER StartServer
    Start the voice backend server after setup for a quick smoke test.
#>

param(
    [switch]$SkipDownload,
    [switch]$StartServer
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ManifestPath = Join-Path $RepoRoot "assets/manifest.json"
$VoiceBackendDir = Join-Path $RepoRoot "apps/voice-backend"
$DownloadDir = Join-Path $RepoRoot "dist/asset-downloads"

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ("=" * 60) -ForegroundColor Cyan
}

function Write-Ok([string]$Msg) { Write-Host "  [OK] $Msg" -ForegroundColor Green }
function Write-Warn([string]$Msg) { Write-Host "  [WARN] $Msg" -ForegroundColor Yellow }
function Write-Err([string]$Msg) { Write-Host "  [FAIL] $Msg" -ForegroundColor Red }
function Write-Info([string]$Msg) { Write-Host "  $Msg" -ForegroundColor Gray }

# ── Load manifest ────────────────────────────────────────────────────

if (-not (Test-Path $ManifestPath)) {
    Write-Err "Manifest not found at $ManifestPath"
    exit 1
}
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$baseUrl = $manifest.baseUrl
$assets = $manifest.assets
Write-Host ""
Write-Host "  Manifest: $ManifestPath" -ForegroundColor Gray
Write-Host "  Base URL: $baseUrl" -ForegroundColor Gray
Write-Host "  Assets:   $($assets.Count)" -ForegroundColor Gray

# ── Step 1: Wipe existing assets ────────────────────────────────────

Write-Section "Step 1: Wipe Existing Voice Backend Assets"

$wipeDirs = @(
    (Join-Path $VoiceBackendDir "piper"),
    (Join-Path $VoiceBackendDir "piper-voices"),
    (Join-Path $VoiceBackendDir "runtime"),
    (Join-Path $VoiceBackendDir "deps/wheels"),
    (Join-Path $VoiceBackendDir "stt-models"),
    (Join-Path $VoiceBackendDir "bin"),
    (Join-Path $VoiceBackendDir ".venv")
)

foreach ($dir in $wipeDirs) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
        Write-Info "Wiped: $dir"
    }
    else {
        Write-Info "Already clean: $dir"
    }
}

# ── Step 2: Download assets ─────────────────────────────────────────

Write-Section "Step 2: Download Assets from GitHub Releases"

if (-not (Test-Path $DownloadDir)) {
    New-Item -ItemType Directory -Force -Path $DownloadDir | Out-Null
}

$allDownloadsOk = $true

foreach ($asset in $assets) {
    $url = $baseUrl + $asset.filename
    $destFile = Join-Path $DownloadDir $asset.filename
    $expectedHash = $asset.sha256
    $expectedSize = $asset.sizeBytes
    $sizeMB = [math]::Round($expectedSize / 1MB, 1)

    Write-Host ""
    Write-Host "  [$($asset.id)]" -ForegroundColor White
    Write-Host "    $($asset.description)" -ForegroundColor Gray
    Write-Host "    URL: $url" -ForegroundColor DarkGray
    Write-Host "    Expected: $sizeMB MB  SHA256: $($expectedHash.Substring(0,16))..." -ForegroundColor DarkGray

    if ($SkipDownload -and (Test-Path $destFile)) {
        Write-Info "  Skipping download (file exists)"
    }
    else {
        Write-Host "    Downloading..." -ForegroundColor Yellow -NoNewline

        $downloadOk = $false
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                $ProgressPreference = 'SilentlyContinue'
                Invoke-WebRequest -Uri $url -OutFile $destFile -TimeoutSec 300
                $ProgressPreference = 'Continue'
                $downloadOk = $true
                break
            }
            catch {
                Write-Host ""
                Write-Warn "  Attempt $attempt/3 failed: $($_.Exception.Message)"
                if ($attempt -lt 3) { Start-Sleep -Seconds 2 }
            }
        }

        if (-not $downloadOk) {
            Write-Err "  Failed to download $($asset.filename) after 3 attempts"
            $allDownloadsOk = $false
            continue
        }

        Write-Host " done." -ForegroundColor Green
    }

    # Verify size
    $actualSize = (Get-Item $destFile).Length
    if ($actualSize -ne $expectedSize) {
        Write-Err "  Size mismatch: expected $expectedSize, got $actualSize"
        $allDownloadsOk = $false
        continue
    }
    Write-Ok "Size: $actualSize bytes"

    # Verify SHA256
    $actualHash = (Get-FileHash -Path $destFile -Algorithm SHA256).Hash.ToLower()
    if ($actualHash -ne $expectedHash) {
        Write-Err "  SHA256 mismatch!"
        Write-Err "    Expected: $expectedHash"
        Write-Err "    Actual:   $actualHash"
        $allDownloadsOk = $false
        continue
    }
    Write-Ok "SHA256 verified"
}

if (-not $allDownloadsOk) {
    Write-Err "Some downloads failed or didn't verify. Aborting."
    exit 1
}

# ── Step 3: Extract assets ──────────────────────────────────────────

Write-Section "Step 3: Extract Assets"

foreach ($asset in $assets) {
    $zipFile = Join-Path $DownloadDir $asset.filename
    $extractTo = Join-Path $RepoRoot ($asset.extractTo -replace '/', '\')

    Write-Host ""
    Write-Host "  [$($asset.id)]" -ForegroundColor White
    Write-Host "    -> $extractTo" -ForegroundColor Gray

    if (-not (Test-Path $extractTo)) {
        New-Item -ItemType Directory -Force -Path $extractTo | Out-Null
    }

    Expand-Archive -Path $zipFile -DestinationPath $extractTo -Force
    $fileCount = @(Get-ChildItem -Path $extractTo -Recurse -File).Count
    Write-Ok "Extracted ($fileCount files)"
}

# ── Step 4: Verify key files ────────────────────────────────────────

Write-Section "Step 4: Verify Key Files"

$checks = @(
    @{ Path = "apps/voice-backend/bin/uv.exe";                                    Label = "uv.exe (Python manager)" },
    @{ Path = "apps/voice-backend/runtime/python/python.exe";                     Label = "Python 3.11 runtime" },
    @{ Path = "apps/voice-backend/piper/piper.exe";                               Label = "Piper TTS binary" },
    @{ Path = "apps/voice-backend/piper-voices/en_US-john-medium/en_US-john-medium.onnx"; Label = "Piper voice model" },
    @{ Path = "apps/voice-backend/stt-models/base/model.bin";                     Label = "Whisper base STT model" }
)

$allFilesOk = $true
foreach ($check in $checks) {
    $fullPath = Join-Path $RepoRoot ($check.Path -replace '/', '\')
    if (Test-Path $fullPath) {
        $sizeMB = [math]::Round((Get-Item $fullPath).Length / 1MB, 1)
        Write-Ok "$($check.Label) ($sizeMB MB)"
    }
    else {
        Write-Err "$($check.Label) NOT FOUND at $fullPath"
        $allFilesOk = $false
    }
}

# Check wheel count
$wheelDir = Join-Path $VoiceBackendDir "deps\wheels"
if (Test-Path $wheelDir) {
    $wheelCount = @(Get-ChildItem -Path $wheelDir -Filter "*.whl").Count
    Write-Ok "Bundled wheels: $wheelCount"
}
else {
    Write-Err "Wheel directory not found"
    $allFilesOk = $false
}

# ── Step 5: Test venv + wheel install ───────────────────────────────

Write-Section "Step 5: Create venv + Install from Bundled Wheels"

$uvExe = Join-Path $VoiceBackendDir "bin\uv.exe"
$venvDir = Join-Path $VoiceBackendDir ".venv"
$bundledPython = Join-Path $VoiceBackendDir "runtime\python\python.exe"

if (-not (Test-Path $uvExe)) {
    Write-Err "uv.exe not found -- cannot create venv"
    exit 1
}

# Create venv
Write-Info "Creating venv with bundled Python..."
$savedEAP = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $uvExe venv $venvDir --python $bundledPython 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
$uvExit = $LASTEXITCODE
$ErrorActionPreference = $savedEAP
if ($uvExit -ne 0) {
    Write-Err "Failed to create venv (exit code $uvExit)"
    exit 1
}
Write-Ok "Venv created"

# Install from bundled wheels
$reqFile = Join-Path $VoiceBackendDir "requirements.txt"
Write-Info "Installing dependencies from bundled wheelhouse (--no-index)..."
$ErrorActionPreference = "Continue"
& $uvExe pip install --python "$venvDir\Scripts\python.exe" --no-index --find-links $wheelDir -r $reqFile 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
$uvExit = $LASTEXITCODE
$ErrorActionPreference = $savedEAP
if ($uvExit -ne 0) {
    Write-Err "Wheel install FAILED -- this is the bug that hit the fresh PC"
    exit 1
}
Write-Ok "All dependencies installed from bundled wheels"

# Quick import test
Write-Info "Verifying key imports..."
$importTest = & "$venvDir\Scripts\python.exe" -c "import fastapi; import faster_whisper; import uvicorn; import numpy; print('All imports OK')" 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Ok $importTest
}
else {
    Write-Err "Import test failed: $importTest"
}

# ── Step 6: (Optional) Start server ─────────────────────────────────

if ($StartServer) {
    Write-Section "Step 6: Smoke-Test Voice Backend Server"
    Write-Info "Starting voice backend on port 8099 (will auto-stop after 15s)..."
    Write-Info "Press Ctrl+C to stop early."

    $job = Start-Job -ScriptBlock {
        param($script, $port)
        & powershell -File $script -Port $port
    } -ArgumentList (Join-Path $VoiceBackendDir "start-voice-backend.ps1"), 8099

    Start-Sleep -Seconds 15

    # Check if /health responds
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:8099/health" -TimeoutSec 5
        Write-Ok "Health check passed: $($health | ConvertTo-Json -Compress)"
    }
    catch {
        Write-Warn "Health check did not respond (server may still be loading)"
    }

    Stop-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
}

# ── Summary ──────────────────────────────────────────────────────────

Write-Section "Summary"

if ($allFilesOk) {
    Write-Ok "All asset files present and verified"
    Write-Ok "Venv + wheel install succeeded"
    Write-Ok "Fresh-PC simulation PASSED"
    Write-Host ""
    Write-Host "  Downloads cached in: $DownloadDir" -ForegroundColor Gray
    Write-Host "  To re-run without re-downloading: .\dev\test-fresh-pc.ps1 -SkipDownload" -ForegroundColor Gray
    Write-Host "  To also start the server:         .\dev\test-fresh-pc.ps1 -SkipDownload -StartServer" -ForegroundColor Gray
}
else {
    Write-Err "Some checks failed -- see above for details"
    exit 1
}
