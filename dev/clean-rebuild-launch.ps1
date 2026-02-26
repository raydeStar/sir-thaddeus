#requires -Version 5.1

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$Runtime = "win-x64",

    [switch]$SkipPreflight,

    [switch]$NoLaunch,

    [switch]$CleanReleaseArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host "============================================================"
    Write-Host "  $Title"
    Write-Host "============================================================"
}

function Fail([string]$Message, [int]$Code = 1) {
    Write-Host "  FAIL: $Message" -ForegroundColor Red
    exit $Code
}

# Ensure we're at repo root
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$StageDir = Join-Path $RepoRoot "artifacts/stage/$Runtime"
$SolutionPath = Join-Path $RepoRoot "SirThaddeus.sln"

Write-Section "Sir Thaddeus Clean Rebuild Launch"
Write-Host "  Configuration      : $Configuration"
Write-Host "  Runtime            : $Runtime"
Write-Host "  SkipPreflight      : $($SkipPreflight.IsPresent)"
Write-Host "  NoLaunch           : $($NoLaunch.IsPresent)"
Write-Host "  CleanReleaseFolder : $($CleanReleaseArtifacts.IsPresent)"

Write-Section "1/4 Tear Down Running Processes"
Stop-Process -Name "SirThaddeus.DesktopRuntime" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "SirThaddeus.VoiceHost" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "SirThaddeus.McpServer" -Force -ErrorAction SilentlyContinue

foreach ($p in @(8001, 17845)) {
    $conn = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
    if ($conn) {
        Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue
    }
}

Write-Section "2/4 Clean Build + Packaging Outputs"
& dotnet clean $SolutionPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Fail "dotnet clean failed (exit code $LASTEXITCODE)." $LASTEXITCODE
}

$pathsToDelete = @(
    (Join-Path $RepoRoot "artifacts/publish"),
    (Join-Path $RepoRoot "artifacts/stage")
)

if ($CleanReleaseArtifacts) {
    $pathsToDelete += (Join-Path $RepoRoot "artifacts/release")
}

foreach ($path in $pathsToDelete) {
    if (Test-Path $path) {
        Write-Host "  Removing $path"
        Remove-Item -Path $path -Recurse -Force
    }
}

Write-Section "3/4 Rebuild + Package"
$releasePackageArgs = @{
    Configuration = $Configuration
    Runtime = $Runtime
}
if ($SkipPreflight) {
    $releasePackageArgs.SkipPreflight = $true
}

& "$PSScriptRoot\release-package.ps1" @releasePackageArgs
if ($LASTEXITCODE -ne 0) {
    Fail "Packaging failed." $LASTEXITCODE
}

if ($NoLaunch) {
    Write-Section "4/4 Launch"
    Write-Host "  Skipped (NoLaunch specified)."
    exit 0
}

Write-Section "4/4 Launch Packaged App"

# Launch Python backend in a dedicated window.
$BackendScript = Join-Path $RepoRoot "apps/voice-backend/start-voice-backend.ps1"
Start-Process powershell -ArgumentList "-NoExit", "-File", "`"$BackendScript`"" -WindowStyle Normal | Out-Null

# Launch packaged VoiceHost in a dedicated window.
$VoiceHostExe = Join-Path $StageDir "SirThaddeus.VoiceHost.exe"
Start-Process powershell -ArgumentList "-NoExit", "-Command", "& `"$VoiceHostExe`"" -WindowStyle Normal | Out-Null

Write-Host "  Waiting for VoiceHost health endpoint..."
$maxWait = 45
while ($maxWait -gt 0) {
    $health = $null
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:17845/health" -ErrorAction Stop
    }
    catch {
        # Service is still starting; retry until timeout.
    }

    if ($null -ne $health -and $health.status -eq "ok") {
        break
    }

    Start-Sleep -Seconds 1
    $maxWait--
}

$DesktopRuntimeExe = Join-Path $StageDir "SirThaddeus.DesktopRuntime.exe"
& "$DesktopRuntimeExe"

exit $LASTEXITCODE
