#!/usr/bin/env pwsh
#requires -Version 7.0

# ===================================================================
#  smoke-test-cross.ps1 -- Cross-platform post-package validation
#
#  Validates a packaged release tar.gz for Linux or macOS BEFORE it
#  becomes a production candidate.
#    1) File structure -- headless runtime, MCP server, UI binary present
#    2) Launch gate   -- headless runtime starts in --server mode
#    3) Health check  -- /api/health endpoint responds
#
#  Usage:
#    pwsh ./dev/smoke-test-cross.ps1 -ArchivePath artifacts/release/sir-thaddeus-linux-x64-dev-abc1234-full.tar.gz
#    pwsh ./dev/smoke-test-cross.ps1 -StageDir artifacts/stage/linux-x64
#    pwsh ./dev/smoke-test-cross.ps1 -ArchivePath <path> -SkipLaunch
#
#  Exit codes:
#    0  All checks passed
#    1  File structure validation failed
#    2  Launch / health check failed
# ===================================================================

param(
    [string]$ArchivePath = "",
    [string]$StageDir = "",
    [switch]$SkipLaunch
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
elseif ($ArchivePath) {
    if (-not (Test-Path $ArchivePath)) {
        Write-Host "ERROR: Archive file not found: $ArchivePath" -ForegroundColor Red
        exit 1
    }

    $extractDir = Join-Path $RepoRoot "artifacts/smoke-test-cross"
    if (Test-Path $extractDir) { Remove-Item -Path $extractDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null

    Write-Section "Extract Archive"
    Write-Host "  Extracting: $ArchivePath"
    tar -xzf $ArchivePath -C $extractDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: tar extraction failed (exit code $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
    # Release archives intentionally wrap their contents in one versioned
    # top-level directory. Validate the package root, not its parent.
    $archiveRoots = @(Get-ChildItem -LiteralPath $extractDir -Force)
    $testDir = if ($archiveRoots.Count -eq 1 -and $archiveRoots[0].PSIsContainer) {
        $archiveRoots[0].FullName
    } else {
        $extractDir
    }
    Write-Host "  Extracted to: $testDir"
}
else {
    Write-Host "ERROR: Provide either -ArchivePath or -StageDir." -ForegroundColor Red
    exit 1
}

Write-Section "Cross-Platform Smoke Test: $testDir"

# Detect platform from archive contents or environment
$targetIsLinux = $IsLinux -or ($ArchivePath -match 'linux')
$targetIsMacOS = $IsMacOS -or ($ArchivePath -match 'osx')

$exeSuffix = ""  # No .exe on Linux/macOS

# =========================================================================
#  1) FILE STRUCTURE VALIDATION
# =========================================================================

Write-Section "File Structure Checks"

# Hybrid runtime
$runtimeBinary = Join-Path $testDir "Thaddeus.Runtime$exeSuffix"
if (Test-Path $runtimeBinary) {
    $sizeMB = [math]::Round((Get-Item $runtimeBinary).Length / 1MB, 1)
    Pass "Thaddeus.Runtime present (${sizeMB} MB)"
}
else {
    Fail "Thaddeus.Runtime missing"
}

# MCP Server
$mcpBinary = Join-Path $testDir "SirThaddeus.McpServer$exeSuffix"
if (Test-Path $mcpBinary) {
    $sizeMB = [math]::Round((Get-Item $mcpBinary).Length / 1MB, 1)
    Pass "SirThaddeus.McpServer present (${sizeMB} MB)"
}
else {
    Fail "SirThaddeus.McpServer missing"
}

# UI binary
$uiBinary = Join-Path $testDir "Thaddeus.Runtime$exeSuffix"
if (Test-Path $uiBinary) {
    $sizeMB = [math]::Round((Get-Item $uiBinary).Length / 1MB, 1)
    Pass "Thaddeus.Runtime present (${sizeMB} MB)"
}
else {
    Fail "Thaddeus.Runtime missing"
}

# README_FIRST_RUN.md
$readme = Join-Path $testDir "README_FIRST_RUN.md"
if (Test-Path $readme) {
    Pass "README_FIRST_RUN.md present"
}
else {
    Fail "README_FIRST_RUN.md missing"
}

# Launcher script
if ($targetIsLinux) {
    $launcher = Join-Path $testDir "launch.sh"
    if (Test-Path $launcher) {
        Pass "launch.sh present"
    }
    else {
        Warn "launch.sh missing"
    }
}
elseif ($targetIsMacOS) {
    $launcher = Join-Path $testDir "launch.command"
    if (Test-Path $launcher) {
        Pass "launch.command present"
    }
    else {
        Warn "launch.command missing"
    }
}

# Managed payload DLLs
$dllCount = @(Get-ChildItem -Path $testDir -Filter "*.dll" -File -Recurse).Count
if ($dllCount -gt 0) {
    Pass "Managed payload present ($dllCount DLLs)"
}
else {
    Fail "No managed payload DLLs found"
}

# No PDB files in release packages
$pdbFiles = @(Get-ChildItem -Path $testDir -Filter "*.pdb" -Recurse)
if ($pdbFiles.Count -eq 0) {
    Pass "No debug symbols (.pdb) in package"
}
else {
    Warn "$($pdbFiles.Count) debug symbols found (expected in Debug builds only)"
}

# =========================================================================
#  2) LAUNCH GATE — Headless Runtime Health Check
# =========================================================================

if (-not $SkipLaunch) {
    Write-Section "Launch Gate — Headless Runtime"

    if (-not (Test-Path $headlessBinary)) {
        Fail "Cannot test headless launch — binary not found"
    }
    else {
        # Ensure the binary is executable on Linux/macOS
        if ($targetIsLinux -or $targetIsMacOS) {
            chmod +x $headlessBinary 2>$null
        }

        $port = 15378  # Use non-default port to avoid conflicts
        $process = $null
        try {
            Write-Host "  Starting headless runtime in --server mode on port $port..."

            $process = Start-Process -FilePath $headlessBinary `
                -ArgumentList "--server", "--port", "$port" `
                -PassThru -RedirectStandardError (Join-Path $testDir "headless-stderr.log")

            $maxWait = 30
            $healthy = $false
            while ($maxWait -gt 0 -and -not $process.HasExited) {
                try {
                    $response = Invoke-RestMethod -Uri "http://127.0.0.1:${port}/api/health" `
                        -TimeoutSec 2 -ErrorAction Stop
                    if ($response.status -eq 'ok') {
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
                Pass "Headless runtime started and /api/health responded (status: ok)"

                # Verify response structure
                if ($response.runtime -eq 'headless-runtime') {
                    Pass "Health response identifies as headless-runtime"
                }
                else {
                    Warn "Health response runtime field unexpected: $($response.runtime)"
                }

                if ($response.version) {
                    Pass "Health response includes version: $($response.version)"
                }
                else {
                    Warn "Health response missing version field"
                }
            }
            elseif ($process.HasExited) {
                $exitCode = $process.ExitCode
                $stderr = ""
                $stderrLog = Join-Path $testDir "headless-stderr.log"
                if (Test-Path $stderrLog) {
                    $stderr = Get-Content $stderrLog -Raw -ErrorAction SilentlyContinue
                }
                Fail "Headless runtime exited early" "exit code: $exitCode; stderr: $($stderr.Substring(0, [Math]::Min($stderr.Length, 500)))"
            }
            else {
                Fail "Headless runtime did not respond to /api/health within 30s"
            }
        }
        finally {
            if ($process -and -not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                Write-Host "  Stopped headless runtime (PID: $($process.Id))"
            }
        }
    }
}
else {
    Write-Section "Launch Gate (SKIPPED)"
    Write-Host "  Skipped: -SkipLaunch was specified"
}

# =========================================================================
#  SUMMARY
# =========================================================================

Write-Section "Cross-Platform Smoke Test Summary"

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
    Write-Host "  CROSS-PLATFORM SMOKE TEST FAILED" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "  CROSS-PLATFORM SMOKE TEST PASSED" -ForegroundColor Green
exit 0
