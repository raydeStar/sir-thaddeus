#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure we're at repo root
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

Write-Host "`n══════════════════════════════════════════════════════════════"
Write-Host "  Sir Thaddeus Packaged-App Debug Runner"
Write-Host "══════════════════════════════════════════════════════════════"

$StageDir = Join-Path $RepoRoot "artifacts/stage/win-x64"

Write-Host "`n[1/3] Packaging application (Debug)..." -ForegroundColor Yellow
& "$PSScriptRoot\release-package.ps1" -Configuration Debug -SkipPreflight
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nERROR: Packaging failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n[2/3] Cleaning up existing background processes..." -ForegroundColor Cyan
Stop-Process -Name "SirThaddeus.VoiceHost" -Force -ErrorAction SilentlyContinue

foreach ($p in @(8001, 17845)) {
    $conn = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
    if ($conn) { Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue }
}

Write-Host "      Launching backend services in separate windows..." -ForegroundColor Cyan

# Launch Python Backend
$BackendScript = Join-Path $RepoRoot "apps/voice-backend/start-voice-backend.ps1"
Start-Process powershell -ArgumentList "-NoExit", "-File", "`"$BackendScript`"" -WindowStyle Normal

# Launch Packaged VoiceHost
$VoiceHostExe = Join-Path $StageDir "SirThaddeus.VoiceHost.exe"
Start-Process powershell -ArgumentList "-NoExit", "-Command", "& `"$VoiceHostExe`"" -WindowStyle Normal

Write-Host "      Waiting for VoiceHost to initialize..." -ForegroundColor DarkGray
$maxWait = 45
while ($maxWait -gt 0) {
    $health = $null
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:17845/health" -ErrorAction Stop
    } catch {
        # Server not up yet, ignore and retry
    }
    if ($null -ne $health -and $health.status -eq 'ok') { break }
    Start-Sleep -Seconds 1
    $maxWait--
}

Write-Host "      Backend logs are now visible in dedicated windows." -ForegroundColor DarkGray

Write-Host "`n[3/3] Starting Packaged UI..." -ForegroundColor Yellow
$UiExe = Join-Path $StageDir "SirThaddeus.UI.Avalonia.exe"
if (-not (Test-Path $UiExe)) {
    Write-Host "ERROR: No UI executable found in stage directory." -ForegroundColor Red
    exit 1
}
& "$UiExe"

exit $LASTEXITCODE
