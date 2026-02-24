<#
.SYNOPSIS
    Simulates and validates voice-backend startup as if on a fresh Windows machine.

.DESCRIPTION
    Creates an isolated temporary directory with only the "shipped" assets,
    then runs each startup stage in isolation to identify failures that would
    occur on a clean Windows install. Reports pass/fail for each stage.

    Run with -KeepTemp to inspect the temp directory after the test.
    Run with -SkipModels to simulate a fresh machine without pre-downloaded STT models.
    Run with -SkipPiperVoices to simulate missing bundled Piper voice files.
    Run with -Offline to simulate no internet connectivity.

.PARAMETER KeepTemp
    Do not delete the temporary test directory after the test completes.

.PARAMETER SkipModels
    Do not copy stt-models into the test directory (simulates first-run download).

.PARAMETER SkipPiperVoices
    Do not copy piper-voices into the test directory (simulates missing bundled voices).

.PARAMETER Offline
    Set ST_VOICE_OFFLINE=1 to simulate no internet connectivity.

.PARAMETER Port
    Port for the test server (default: 18999, chosen to avoid conflicts).

.PARAMETER TimeoutSeconds
    How long to wait for the server to become healthy (default: 120).
#>

param(
    [switch]$KeepTemp,
    [switch]$SkipModels,
    [switch]$SkipPiperVoices,
    [switch]$Offline,
    [int]$Port = 18999,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$VoiceBackendDir = Join-Path $RepoRoot "apps\voice-backend"
$TestDir = Join-Path ([System.IO.Path]::GetTempPath()) ("st-fresh-machine-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))

$stages = [ordered]@{}
$serverProcess = $null

function Report-Stage {
    param([string]$Name, [bool]$Pass, [string]$Detail = "")
    $icon = if ($Pass) { "[PASS]" } else { "[FAIL]" }
    $color = if ($Pass) { "Green" } else { "Red" }
    Write-Host "  $icon $Name" -ForegroundColor $color -NoNewline
    if ($Detail) { Write-Host " - $Detail" -ForegroundColor DarkGray } else { Write-Host "" }
    $stages[$Name] = @{ Pass = $Pass; Detail = $Detail }
}

try {
    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host "  Fresh Machine Voice Backend Simulation Test" -ForegroundColor Cyan
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Test dir:     $TestDir"
    Write-Host "  Port:         $Port"
    Write-Host "  SkipModels:   $SkipModels"
    Write-Host "  SkipVoices:   $SkipPiperVoices"
    Write-Host "  Offline:      $Offline"
    Write-Host ""

    # ── Stage 0: Validate source directory ─────────────────────────
    $sourceValid = (Test-Path (Join-Path $VoiceBackendDir "server.py")) -and
                   (Test-Path (Join-Path $VoiceBackendDir "start-voice-backend.ps1")) -and
                   (Test-Path (Join-Path $VoiceBackendDir "requirements.txt"))
    Report-Stage "Source directory valid" $sourceValid $VoiceBackendDir
    if (-not $sourceValid) {
        Write-Host "  Cannot proceed without valid source directory." -ForegroundColor Red
        exit 1
    }

    # ── Stage 1: Create isolated test directory ───────────────────
    Write-Host ""
    Write-Host "  Copying shipped assets to isolated test directory..." -ForegroundColor Yellow

    New-Item -ItemType Directory -Force -Path $TestDir | Out-Null

    # Copy source files (what would be in a packaged release)
    Copy-Item (Join-Path $VoiceBackendDir "server.py") $TestDir
    Copy-Item (Join-Path $VoiceBackendDir "start-voice-backend.ps1") $TestDir
    Copy-Item (Join-Path $VoiceBackendDir "requirements.txt") $TestDir
    Copy-Item (Join-Path $VoiceBackendDir "model_registry.json") $TestDir -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $VoiceBackendDir "model_downloader.py") $TestDir -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $VoiceBackendDir "youtube_pipeline.py") $TestDir -ErrorAction SilentlyContinue

    # Copy bundled runtime (Python)
    $bundledRuntime = Join-Path $VoiceBackendDir "runtime"
    if (Test-Path $bundledRuntime) {
        Copy-Item $bundledRuntime (Join-Path $TestDir "runtime") -Recurse
        Report-Stage "Bundled Python runtime" $true "Copied"
    } else {
        Report-Stage "Bundled Python runtime" $false "Not found at $bundledRuntime — UV will need to download Python"
    }

    # Copy bundled wheels
    $bundledWheels = Join-Path $VoiceBackendDir "deps"
    if (Test-Path $bundledWheels) {
        Copy-Item $bundledWheels (Join-Path $TestDir "deps") -Recurse
        $wheelCount = (Get-ChildItem (Join-Path $TestDir "deps\wheels") -Filter "*.whl" -ErrorAction SilentlyContinue).Count
        Report-Stage "Bundled wheels" ($wheelCount -gt 0) "$wheelCount .whl files"
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $TestDir "deps\wheels") | Out-Null
        Report-Stage "Bundled wheels" $false "No bundled wheels — will need internet"
    }

    # Copy UV binary
    $uvBin = Join-Path $VoiceBackendDir "bin"
    if (Test-Path (Join-Path $uvBin "uv.exe")) {
        Copy-Item $uvBin (Join-Path $TestDir "bin") -Recurse
        Report-Stage "UV binary" $true "Copied"
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $TestDir "bin") | Out-Null
        Report-Stage "UV binary" $false "Not found — will need to download from GitHub"
    }

    # Copy piper
    $piperDir = Join-Path $VoiceBackendDir "piper"
    if (Test-Path $piperDir) {
        Copy-Item $piperDir (Join-Path $TestDir "piper") -Recurse
        $hasPiperExe = Test-Path (Join-Path $TestDir "piper\piper.exe")
        Report-Stage "Piper TTS binary" $hasPiperExe $(if ($hasPiperExe) { "piper.exe present" } else { "piper.exe missing!" })
    } else {
        Report-Stage "Piper TTS binary" $false "piper/ directory not found"
    }

    # Copy piper-voices (unless skipped)
    if (-not $SkipPiperVoices) {
        $piperVoices = Join-Path $VoiceBackendDir "piper-voices"
        if (Test-Path $piperVoices) {
            Copy-Item $piperVoices (Join-Path $TestDir "piper-voices") -Recurse
            $voiceCount = (Get-ChildItem (Join-Path $TestDir "piper-voices") -Directory).Count
            Report-Stage "Piper voice assets" ($voiceCount -gt 0) "$voiceCount voices"
        } else {
            Report-Stage "Piper voice assets" $false "piper-voices/ not found"
        }
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $TestDir "piper-voices") | Out-Null
        Report-Stage "Piper voice assets" $false "SKIPPED by -SkipPiperVoices flag"
    }

    # Copy stt-models (unless skipped)
    if (-not $SkipModels) {
        $sttModels = Join-Path $VoiceBackendDir "stt-models"
        if (Test-Path $sttModels) {
            Copy-Item $sttModels (Join-Path $TestDir "stt-models") -Recurse
            $hasBaseModel = Test-Path (Join-Path $TestDir "stt-models\base\model.bin")
            Report-Stage "STT model (faster-whisper base)" $hasBaseModel $(if ($hasBaseModel) { "model.bin present" } else { "model.bin missing!" })
        } else {
            Report-Stage "STT model (faster-whisper base)" $false "stt-models/ not found"
        }
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $TestDir "stt-models") | Out-Null
        Report-Stage "STT model (faster-whisper base)" $false "SKIPPED by -SkipModels flag"
    }

    # ── Stage 2: Kill anything on the test port ──────────────────
    Write-Host ""
    try {
        $staleListeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        foreach ($conn in $staleListeners) {
            $stalePid = $conn.OwningProcess
            if ($stalePid -and $stalePid -ne $PID -and $stalePid -ne 0) {
                Write-Host "  Killing stale process $stalePid on port $Port" -ForegroundColor Yellow
                Stop-Process -Id $stalePid -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 500
            }
        }
    } catch {}

    # ── Stage 3: Run start-voice-backend.ps1 ─────────────────────
    Write-Host "  Launching voice backend from isolated directory..." -ForegroundColor Yellow
    Write-Host ""

    $logFile = Join-Path $TestDir "test-output.log"
    $envVars = @{
        "PYTHONUTF8" = "1"
        "PYTHONUNBUFFERED" = "1"
    }
    if ($Offline) {
        $envVars["ST_VOICE_OFFLINE"] = "1"
        $envVars["HF_HUB_OFFLINE"] = "1"
        $envVars["TRANSFORMERS_OFFLINE"] = "1"
    }

    $startScript = Join-Path $TestDir "start-voice-backend.ps1"
    $psArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$startScript`" -Port $Port -TtsEngine piper -TtsVoiceId en_US-ryan-medium -SttEngine faster-whisper -SttModelId base"

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "powershell"
    $psi.Arguments = $psArgs
    $psi.WorkingDirectory = $TestDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    foreach ($kv in $envVars.GetEnumerator()) {
        $psi.EnvironmentVariables[$kv.Key] = $kv.Value
    }

    $serverProcess = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $serverProcess) {
        Report-Stage "Process launch" $false "Process.Start returned null"
        exit 1
    }

    Report-Stage "Process launch" $true "PID $($serverProcess.Id)"

    # Async read stdout/stderr to avoid deadlocks
    $stdoutTask = $serverProcess.StandardOutput.ReadToEndAsync()
    $stderrTask = $serverProcess.StandardError.ReadToEndAsync()

    # ── Stage 4: Wait for health endpoint ────────────────────────
    Write-Host ""
    Write-Host "  Waiting for health endpoint (up to ${TimeoutSeconds}s)..." -ForegroundColor Yellow

    $healthUrl = "http://127.0.0.1:$Port/health"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $healthy = $false
    $lastStatus = ""
    $httpClient = New-Object System.Net.Http.HttpClient
    $httpClient.Timeout = [TimeSpan]::FromSeconds(3)
    $spinChars = @('|', '/', '-', '\')
    $spinIdx = 0

    while ([DateTime]::UtcNow -lt $deadline) {
        if ($serverProcess.HasExited) {
            $lastStatus = "Process exited with code $($serverProcess.ExitCode)"
            break
        }

        try {
            $response = $httpClient.GetAsync($healthUrl).GetAwaiter().GetResult()
            if ($response.IsSuccessStatusCode) {
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $json = $body | ConvertFrom-Json -ErrorAction SilentlyContinue
                if ($json -and $json.ready -eq $true) {
                    $healthy = $true
                    $lastStatus = "ready=true, asrReady=$($json.asrReady), ttsReady=$($json.ttsReady)"
                    break
                }
                $lastStatus = "status=$($json.status), ready=$($json.ready), message=$($json.message)"
            } else {
                $lastStatus = "HTTP $($response.StatusCode)"
            }
        }
        catch {
            $lastStatus = "unreachable"
        }

        $spin = $spinChars[$spinIdx % $spinChars.Count]
        Write-Host "`r  $spin Waiting... ($lastStatus)    " -NoNewline
        $spinIdx++
        Start-Sleep -Milliseconds 1000
    }

    Write-Host "`r                                                              `r" -NoNewline
    $httpClient.Dispose()

    Report-Stage "Health endpoint ready" $healthy $lastStatus

    # ── Stage 5: Check ASR readiness ──────────────────────────────
    if ($healthy) {
        try {
            $hc = New-Object System.Net.Http.HttpClient
            $hc.Timeout = [TimeSpan]::FromSeconds(5)
            $resp = $hc.GetAsync($healthUrl).GetAwaiter().GetResult()
            $hBody = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $hJson = $hBody | ConvertFrom-Json
            Report-Stage "ASR ready" ($hJson.asrReady -eq $true) "asrReady=$($hJson.asrReady)"
            Report-Stage "TTS ready" ($hJson.ttsReady -eq $true) "ttsReady=$($hJson.ttsReady)"
            $hc.Dispose()
        }
        catch {
            Report-Stage "ASR ready" $false $_.Exception.Message
            Report-Stage "TTS ready" $false "skipped"
        }
    } else {
        Report-Stage "ASR ready" $false "Server not healthy"
        Report-Stage "TTS ready" $false "Server not healthy"
    }

    # ── Summary ───────────────────────────────────────────────────
    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host "  Results Summary" -ForegroundColor Cyan
    Write-Host "================================================================" -ForegroundColor Cyan

    $passCount = ($stages.Values | Where-Object { $_.Pass }).Count
    $failCount = ($stages.Values | Where-Object { -not $_.Pass }).Count
    $totalCount = $stages.Count

    foreach ($kv in $stages.GetEnumerator()) {
        $icon = if ($kv.Value.Pass) { "[PASS]" } else { "[FAIL]" }
        $color = if ($kv.Value.Pass) { "Green" } else { "Red" }
        Write-Host "  $icon $($kv.Key)" -ForegroundColor $color -NoNewline
        if ($kv.Value.Detail) { Write-Host " - $($kv.Value.Detail)" -ForegroundColor DarkGray } else { Write-Host "" }
    }

    Write-Host ""
    if ($failCount -eq 0) {
        Write-Host "  ALL $totalCount STAGES PASSED" -ForegroundColor Green
    } else {
        Write-Host "  $passCount/$totalCount passed, $failCount FAILED" -ForegroundColor Red
    }

    # Capture process output
    if (-not $serverProcess.HasExited) {
        Write-Host ""
        Write-Host "  Stopping test server..." -ForegroundColor DarkGray
        try { $serverProcess.Kill($true) } catch {}
        $serverProcess.WaitForExit(3000)
    }

    # Write captured output to log
    try {
        $stdout = if ($stdoutTask.IsCompleted) { $stdoutTask.Result } else { $stdoutTask.GetAwaiter().GetResult() }
        $stderr = if ($stderrTask.IsCompleted) { $stderrTask.Result } else { $stderrTask.GetAwaiter().GetResult() }
        $logContent = "=== STDOUT ===`r`n$stdout`r`n`r`n=== STDERR ===`r`n$stderr"
        Set-Content -Path $logFile -Value $logContent -Encoding UTF8
        Write-Host "  Process output saved to: $logFile" -ForegroundColor DarkGray
    } catch {}

    if ($failCount -gt 0 -or $KeepTemp) {
        Write-Host "  Test directory preserved at: $TestDir" -ForegroundColor Yellow
        Write-Host "  Inspect $logFile for process output." -ForegroundColor DarkGray
    }

    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "  FATAL: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  $($_.ScriptStackTrace)" -ForegroundColor DarkGray
}
finally {
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        try { $serverProcess.Kill($true) } catch {}
    }

    if (-not $KeepTemp -and ($stages.Values | Where-Object { -not $_.Pass }).Count -eq 0) {
        if (Test-Path $TestDir) {
            try { Remove-Item $TestDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
            Write-Host "  Test directory cleaned up." -ForegroundColor DarkGray
        }
    }
}
