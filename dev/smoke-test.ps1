#requires -Version 5.1

# ===================================================================
#  smoke-test.ps1 -- Post-package validation gate
#
#  Validates a packaged release zip BEFORE it becomes a production
#  candidate. Designed to catch the "works on my machine" class of
#  failures:
#    1) File structure -- all required EXEs, DLLs, and assets present
#    2) Launch gate   -- UI shell starts in headless/smoke mode
#    3) Health check  -- VoiceHost /health endpoint responds
#    4) Checksum      -- zip SHA256 matches sidecar file
#
#  Usage:
#    .\dev\smoke-test.ps1                                # auto-find latest zip
#    .\dev\smoke-test.ps1 -ZipPath artifacts\release\sir-thaddeus-win-x64-dev-abc1234.zip
#    .\dev\smoke-test.ps1 -StageDir artifacts\stage\win-x64   # skip extract, test staged dir
#    .\dev\smoke-test.ps1 -SkipLaunch                    # CI runners without GUI
#
#  Exit codes:
#    0  All checks passed
#    1  File structure validation failed
#    2  Launch / health check failed
#    3  Checksum mismatch
# ===================================================================

param(
    [string]$ZipPath = "",
    [string]$StageDir = "",
    [switch]$SkipLaunch,
    [switch]$SkipChecksum,
    [switch]$AllowRuntimeAssetDownload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:failures = @()
$script:warnings = @()
$script:passed   = 0

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host "--------------------------------------------------------------"
    Write-Host "  $Title"
    Write-Host "--------------------------------------------------------------"
}

function Pass([string]$Check) {
    Write-Host "  PASS  $Check" -ForegroundColor Green
    $script:passed++
}

function Fail([string]$Check, [string]$Detail = "") {
    $msg = if ($Detail) { "$Check -- $Detail" } else { $Check }
    Write-Host "  FAIL  $msg" -ForegroundColor Red
    $script:failures += $msg
}

function Warn([string]$Check) {
    Write-Host "  WARN  $Check" -ForegroundColor Yellow
    $script:warnings += $Check
}

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

# -- Resolve test directory ------------------------------------------------

$testDir = ""

if ($StageDir) {
    if (-not (Test-Path $StageDir)) {
        Write-Host "ERROR: Stage directory not found: $StageDir" -ForegroundColor Red
        exit 1
    }
    $testDir = Resolve-Path $StageDir
}
elseif ($ZipPath) {
    if (-not (Test-Path $ZipPath)) {
        Write-Host "ERROR: Zip file not found: $ZipPath" -ForegroundColor Red
        exit 1
    }

    $extractDir = Join-Path $RepoRoot "artifacts/smoke-test"
    if (Test-Path $extractDir) { Remove-Item -Path $extractDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null

    Write-Section "Extract Archive"
    Write-Host "  Extracting: $ZipPath"
    Expand-Archive -Path $ZipPath -DestinationPath $extractDir -Force
    $testDir = $extractDir

    # Descend into wrapper folder if the archive uses the wrapped layout.
    if (-not (Test-Path (Join-Path $testDir 'Thaddeus.Runtime.exe'))) {
        $wrapper = @(Get-ChildItem -Path $testDir -Directory -Filter 'sir-thaddeus-*')
        if ($wrapper.Count -eq 1) {
            $testDir = $wrapper[0].FullName
            Write-Host "  Wrapper folder: $($wrapper[0].Name)"
        }
    }

    Write-Host "  Extracted to: $testDir"
}
else {
    # Auto-find: prefer staged dir, then latest zip
    $defaultStage = Join-Path $RepoRoot "artifacts/stage/win-x64"
    if (Test-Path $defaultStage) {
        $testDir = Resolve-Path $defaultStage
        Write-Host "  Auto-detected stage dir: $testDir"
    }
    else {
        $releaseDir = Join-Path $RepoRoot "artifacts/release"
        if (Test-Path $releaseDir) {
            $latestZip = Get-ChildItem -Path $releaseDir -Filter "sir-thaddeus-*.zip" |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1

            if ($latestZip) {
                $extractDir = Join-Path $RepoRoot "artifacts/smoke-test"
                if (Test-Path $extractDir) { Remove-Item -Path $extractDir -Recurse -Force }
                New-Item -ItemType Directory -Force -Path $extractDir | Out-Null

                Write-Host "  Auto-detected zip: $($latestZip.FullName)"
                Expand-Archive -Path $latestZip.FullName -DestinationPath $extractDir -Force
                $testDir = $extractDir

                # Descend into wrapper folder if the archive uses the wrapped layout.
                if (-not (Test-Path (Join-Path $testDir 'Thaddeus.Runtime.exe'))) {
                    $wrapper = @(Get-ChildItem -Path $testDir -Directory -Filter 'sir-thaddeus-*')
                    if ($wrapper.Count -eq 1) {
                        $testDir = $wrapper[0].FullName
                        Write-Host "  Wrapper folder: $($wrapper[0].Name)"
                    }
                }
            }
        }
    }

    if (-not $testDir -or -not (Test-Path $testDir)) {
        Write-Host "ERROR: No stage dir or release zip found. Run release-package.ps1 first." -ForegroundColor Red
        exit 1
    }
}

Write-Section "Smoke Test: $testDir"

# =========================================================================
#  1) FILE STRUCTURE VALIDATION
# =========================================================================

Write-Section "File Structure Checks"

# Required top-level executables
$requiredExes = @(
    "SirThaddeus.McpServer.exe",
    "SirThaddeus.VoiceHost.exe"
)

foreach ($exe in $requiredExes) {
    $path = Join-Path $testDir $exe
    if (Test-Path $path) {
        $sizeMB = [math]::Round((Get-Item $path).Length / 1MB, 1)
        Pass "$exe present (${sizeMB} MB)"
    }
    else {
        Fail "$exe missing from package root"
    }
}

$uiExecutable = Join-Path $testDir "Thaddeus.Runtime.exe"
if (Test-Path $uiExecutable) {
    $sizeMB = [math]::Round((Get-Item $uiExecutable).Length / 1MB, 1)
    Pass "Thaddeus.Runtime.exe present (${sizeMB} MB)"
}
else {
    Fail "Runtime executable missing from package root (expected Thaddeus.Runtime.exe)"
}

# Required support files
$requiredFiles = @(
    "README_FIRST_RUN.md"
)

foreach ($file in $requiredFiles) {
    $path = Join-Path $testDir $file
    if (Test-Path $path) {
        Pass "$file present"
    }
    else {
        Fail "$file missing"
    }
}

# Package must contain managed payload DLLs.
$rootDllCount = @(Get-ChildItem -Path $testDir -Filter "*.dll" -File -Recurse).Count
$binDir = Join-Path $testDir "bin"
$binDllCount = if (Test-Path $binDir) {
    @(Get-ChildItem -Path $binDir -Filter "*.dll" -File -Recurse).Count
}
else {
    0
}

if ($rootDllCount -gt 0 -or $binDllCount -gt 0) {
    Pass "Managed payload present (root DLLs: $rootDllCount, bin DLLs: $binDllCount)"
}
else {
    Fail "No managed payload DLLs found in package"
}

# Voice backend assets.
# By default, this gate requires bundled assets to avoid shipping packages
# that only work after runtime downloads.
$voiceAssetsRequired = -not $AllowRuntimeAssetDownload
$voiceAssets = @(
    @{ Path = "voice/piper/piper.exe"; Label = "Piper TTS binary"; Required = $voiceAssetsRequired },
    @{ Path = "voice/piper-voices/en_US-john-medium/en_US-john-medium.onnx"; Label = "Default Piper voice model"; Required = $voiceAssetsRequired },
    @{ Path = "voice/stt-models/base/model.bin"; Label = "Whisper STT model"; Required = $voiceAssetsRequired }
)

foreach ($asset in $voiceAssets) {
    $path = Join-Path $testDir $asset.Path
    if (Test-Path $path) {
        Pass "$($asset.Label) present"
        continue
    }

    if ($voiceAssetsRequired) {
        Fail "$($asset.Label) not bundled"
    }
    else {
        Warn "$($asset.Label) not bundled (will download at runtime)"
    }
}

$packagedUvExe = Join-Path $testDir "voice/bin/uv.exe"
if (Test-Path $packagedUvExe) {
    Pass "Bundled uv.exe present"
}
else {
    if ($voiceAssetsRequired) {
        Fail "Bundled uv.exe not found"
    }
    else {
        Warn "Bundled uv.exe not found (will download at runtime)"
    }
}

$packagedPythonExe = Join-Path $testDir "voice/runtime/python/python.exe"
if (Test-Path $packagedPythonExe) {
    Pass "Bundled Python runtime present"
}
else {
    if ($voiceAssetsRequired) {
        Fail "Bundled Python runtime not found"
    }
    else {
        Warn "Bundled Python runtime not found (will download at runtime)"
    }
}

$packagedRequirementsFile = Join-Path $testDir "voice/requirements.txt"
if (Test-Path $packagedRequirementsFile) {
    Pass "Voice requirements.txt present"
}
else {
    if ($voiceAssetsRequired) {
        Fail "Voice requirements.txt not found"
    }
    else {
        Warn "Voice requirements.txt not found"
    }
}

$packagedWheelDir = Join-Path $testDir "voice/deps/wheels"
$packagedWheelCount = if (Test-Path $packagedWheelDir) {
    @(Get-ChildItem -Path $packagedWheelDir -Filter "*.whl" -File -ErrorAction SilentlyContinue).Count
}
else {
    0
}

if ($packagedWheelCount -gt 0) {
    Pass "Bundled wheelhouse present ($packagedWheelCount wheels)"
}
else {
    if ($voiceAssetsRequired) {
        Fail "Bundled wheelhouse missing or empty"
    }
    else {
        Warn "Bundled wheelhouse missing or empty (runtime download required)"
    }
}

# Settings template
$settingsTemplate = Join-Path $testDir "SirThaddeus.Settings.template.json"
if (Test-Path $settingsTemplate) {
    Pass "Settings template present"
}
else {
    Warn "Settings template not found (optional)"
}

# Assets manifest (needed for runtime self-heal)
$assetsManifest = Join-Path $testDir "assets/manifest.json"
if (Test-Path $assetsManifest) {
    Pass "Asset manifest present"
}
else {
    Warn "Asset manifest missing (runtime asset download unavailable)"
}

$searchPayloadChecks = @(
    @{ Path = "search/start-searxng.ps1"; Label = "SearXNG bootstrap script" },
    @{ Path = "search/runtime/python/python.exe"; Label = "SearXNG Python runtime" },
    @{ Path = "search/source/searxng-upstream/searx/webapp.py"; Label = "SearXNG source payload" },
    @{ Path = "search/deps/site-packages/flask/__init__.py"; Label = "SearXNG Python dependencies" }
)

$detectedSearchPayload = 0
foreach ($asset in $searchPayloadChecks) {
    if (Test-Path (Join-Path $testDir $asset.Path)) {
        $detectedSearchPayload++
    }
}

if ($detectedSearchPayload -eq 0) {
    if ($AllowRuntimeAssetDownload) {
        Warn "Bundled SearXNG payload not found (will use configured/live search providers when available)"
    }
    else {
        Fail "Bundled SearXNG payload not found"
    }
}
else {
    foreach ($asset in $searchPayloadChecks) {
        $path = Join-Path $testDir $asset.Path
        if (Test-Path $path) {
            Pass "$($asset.Label) present"
        }
        else {
            Fail "$($asset.Label) missing"
        }
    }
}

# No PDB files should be in release packages
$pdbFiles = @(Get-ChildItem -Path $testDir -Filter "*.pdb" -Recurse)
if ($pdbFiles.Count -eq 0) {
    Pass "No debug symbols (.pdb) in package"
}
else {
    Warn "$($pdbFiles.Count) debug symbols found (expected in Debug builds only)"
}

# =========================================================================
#  1.5) OFFLINE VOICE DEPENDENCY VALIDATION
# =========================================================================

Write-Section "Offline Voice Dependency Gate"

if ($voiceAssetsRequired) {
    if (-not $packagedUvExe -or -not $packagedPythonExe -or -not $packagedRequirementsFile -or $packagedWheelCount -le 0) {
        Fail "Offline dependency install gate prerequisites missing"
    }
    else {
        $voiceVenvTempDir = Join-Path $env:TEMP ("st-voice-smoke-venv-" + [Guid]::NewGuid().ToString("N"))
        try {
            Write-Host "  Creating temporary voice venv for offline install test..."
            & $packagedUvExe venv $voiceVenvTempDir --python $packagedPythonExe
            if ($LASTEXITCODE -ne 0) {
                Fail "Offline dependency install gate" "uv venv creation failed (exit code $LASTEXITCODE)"
            }
            else {
                $venvPython = Join-Path $voiceVenvTempDir "Scripts/python.exe"
                if (-not (Test-Path $venvPython)) {
                    Fail "Offline dependency install gate" "venv python not found at $venvPython"
                }
                else {
                    Write-Host "  Installing voice dependencies from bundled wheelhouse (--no-index)..."
                    & $packagedUvExe pip install --python $venvPython --no-index --find-links $packagedWheelDir -r $packagedRequirementsFile
                    if ($LASTEXITCODE -ne 0) {
                        Fail "Offline dependency install gate" "uv pip install failed (exit code $LASTEXITCODE)"
                    }
                    else {
                        & $venvPython -c "import fastapi; import faster_whisper; import uvicorn; import numpy; print('voice dependency imports OK')"
                        if ($LASTEXITCODE -ne 0) {
                            Fail "Offline dependency import gate" "Python import verification failed (exit code $LASTEXITCODE)"
                        }
                        else {
                            Pass "Offline voice dependency install + import validation"
                        }
                    }
                }
            }
        }
        finally {
            if (Test-Path $voiceVenvTempDir) {
                Remove-Item -Path $voiceVenvTempDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
else {
    Warn "Offline dependency install gate skipped (--AllowRuntimeAssetDownload)"
}

# =========================================================================
#  2) LAUNCH GATE (optional)
# =========================================================================

if (-not $SkipLaunch) {
    Write-Section "Launch Gate"

    # -- VoiceHost health check --
    $voiceHostExe = Join-Path $testDir "SirThaddeus.VoiceHost.exe"
    if (Test-Path $voiceHostExe) {
        Write-Host "  Starting VoiceHost..."
        $voiceHostProcess = $null
        try {
            $voiceHostProcess = Start-Process -FilePath $voiceHostExe `
                -PassThru -WindowStyle Hidden

            $maxWait = 30
            $healthy = $false
            while ($maxWait -gt 0 -and -not $voiceHostProcess.HasExited) {
                try {
                    $response = Invoke-RestMethod -Uri "http://127.0.0.1:17845/health" -TimeoutSec 2 -ErrorAction Stop
                    if ($response.status -eq 'ok' -or $response.status -eq 'loading') {
                        $healthy = $true
                        break
                    }
                }
                catch {
                    # Not up yet
                }
                Start-Sleep -Seconds 1
                $maxWait--
            }

            if ($healthy) {
                Pass "VoiceHost started and /health responded"
            }
            elseif ($voiceHostProcess.HasExited) {
                Fail "VoiceHost exited early" "exit code: $($voiceHostProcess.ExitCode)"
            }
            else {
                Fail "VoiceHost did not respond to /health within 30s"
            }
        }
        finally {
            if ($voiceHostProcess -and -not $voiceHostProcess.HasExited) {
                Stop-Process -Id $voiceHostProcess.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    else {
        Fail "Cannot test VoiceHost launch -- exe not found"
    }

    # -- UI shell launch --
    if ($uiExecutable) {
        Write-Host "  Starting UI shell in smoke mode..."
        $uiProcess = $null
        try {
            $uiProcess = Start-Process -FilePath $uiExecutable `
                -ArgumentList "--headless", "--smoke-test" `
                -PassThru -WindowStyle Hidden

            # Give it a few seconds to start and not crash
            Start-Sleep -Seconds 5

            if (-not $uiProcess.HasExited) {
                Pass "UI shell started in smoke mode (still running after 5s)"
            }
            else {
                if ($uiProcess.ExitCode -eq 0) {
                    Pass "UI shell exited cleanly in smoke-test mode"
                }
                else {
                    Fail "UI shell crashed on launch" "exit code: $($uiProcess.ExitCode)"
                }
            }
        }
        finally {
            if ($uiProcess -and -not $uiProcess.HasExited) {
                Stop-Process -Id $uiProcess.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    else {
        Fail "Cannot test UI launch -- executable not found"
    }
}
else {
    Write-Section "Launch Gate (SKIPPED)"
    Write-Host "  Skipped: -SkipLaunch was specified"
}

# =========================================================================
#  3) CHECKSUM VALIDATION (optional)
# =========================================================================

if (-not $SkipChecksum -and $ZipPath) {
    Write-Section "Checksum Validation"

    $checksumFile = "$ZipPath.sha256.txt"
    if (Test-Path $checksumFile) {
        $expectedLine = (Get-Content $checksumFile -Raw).Trim()
        $expectedHash = ($expectedLine -split '\s+')[0].ToUpperInvariant()

        $actualHash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToUpperInvariant()

        if ($actualHash -eq $expectedHash) {
            Pass "SHA256 checksum matches"
        }
        else {
            Fail "SHA256 checksum mismatch" "expected=$expectedHash actual=$actualHash"
        }
    }
    else {
        Warn "No checksum sidecar file found at $checksumFile"
    }
}

# =========================================================================
#  SUMMARY
# =========================================================================

Write-Section "Smoke Test Summary"

$totalChecks = $script:passed + $script:failures.Count
Write-Host "  Passed   : $($script:passed) / $totalChecks"

if ($script:warnings.Count -gt 0) {
    Write-Host "  Warnings : $($script:warnings.Count)" -ForegroundColor Yellow
    foreach ($w in $script:warnings) {
        Write-Host "    - $w" -ForegroundColor Yellow
    }
}

if ($script:failures.Count -gt 0) {
    Write-Host "  Failures : $($script:failures.Count)" -ForegroundColor Red
    foreach ($f in $script:failures) {
        Write-Host "    - $f" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "  SMOKE TEST FAILED" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "  SMOKE TEST PASSED" -ForegroundColor Green
exit 0
