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

if (-not (Test-Path $StageDir)) {
    Write-Host "`n[1/3] Packaging application (Release) since it's missing..." -ForegroundColor Yellow
    & "$PSScriptRoot\release-package.ps1"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "`nERROR: Packaging failed." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}
else {
    Write-Host "`n[1/3] Found existing packaged app in $StageDir." -ForegroundColor Green
    Write-Host "      (Run .\dev\release-package.ps1 manually to rebuild it with new code changes)" -ForegroundColor DarkGray
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
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:17845/health" -ErrorAction SilentlyContinue
    if ($null -ne $health -and $health.status -eq 'ok') { break }
    Start-Sleep -Seconds 1
    $maxWait--
}

Write-Host "      Backend logs are now visible in dedicated windows." -ForegroundColor DarkGray

Write-Host "`n[3/3] Starting Packaged UI (Desktop Runtime)..." -ForegroundColor Yellow
$DesktopRuntimeExe = Join-Path $StageDir "SirThaddeus.DesktopRuntime.exe"
& "$DesktopRuntimeExe"

exit $LASTEXITCODE
