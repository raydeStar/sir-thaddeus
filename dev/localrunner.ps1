#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure we're at repo root (script lives in /dev)
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

Write-Host "`n══════════════════════════════════════════════════════════════"
Write-Host "  Sir Thaddeus Local Runner"
Write-Host "══════════════════════════════════════════════════════════════"

$DebugMode = $args -contains "--debug"

# 1. Bootstrap (Restores dependencies, validates SDK)
Write-Host "`n[1/3] Bootstrapping environment..." -ForegroundColor Yellow
& "$PSScriptRoot\bootstrap.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nERROR: Bootstrap failed. Cannot start application." -ForegroundColor Red
    exit $LASTEXITCODE
}

# 2. Build VoiceHost (DesktopRuntime doesn't reference it directly)
Write-Host "`n[2/3] Building VoiceHost..." -ForegroundColor Yellow
$VoiceHostPath = Join-Path $RepoRoot "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj"
& dotnet build $VoiceHostPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nERROR: VoiceHost build failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

# 3. Preparation & Execution
if ($DebugMode) {
    Write-Host "`n[3/3] DEBUG MODE: Cleaning up existing background processes..." -ForegroundColor Cyan
    Stop-Process -Name "SirThaddeus.VoiceHost" -Force -ErrorAction SilentlyContinue
    
    foreach ($p in @(8001, 17845)) {
        $conn = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
        if ($conn) { Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue }
    }

    Write-Host "      Launching backend services in separate windows..." -ForegroundColor Cyan
    
    # Launch Python Backend
    $BackendScript = Join-Path $RepoRoot "dev/start-voice-backend.ps1"
    Start-Process powershell -ArgumentList "-NoExit", "-File", "`"$BackendScript`"" -WindowStyle Normal

    # Launch VoiceHost
    $VoiceHostCsproj = Join-Path $RepoRoot "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj"
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "dotnet run --project `"$VoiceHostCsproj`"" -WindowStyle Normal

    Write-Host "      Waiting for VoiceHost to initialize..." -ForegroundColor DarkGray
    $maxWait = 45
    while ($maxWait -gt 0) {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:17845/health" -ErrorAction SilentlyContinue
        if ($null -ne $health -and $health.status -eq 'ok') { break }
        Start-Sleep -Seconds 1
        $maxWait--
    }

    Write-Host "      Backend logs are now visible in dedicated windows." -ForegroundColor DarkGray
    Write-Host "      Starting Desktop Runtime..." -ForegroundColor Yellow
}
else {
    Write-Host "`n[3/3] Starting Sir Thaddeus (Desktop Runtime)..." -ForegroundColor Yellow
    Write-Host "      VoiceHost and Backend services will auto-start as needed." -ForegroundColor DarkGray
    Write-Host "      (Use --debug to see background service logs in separate windows)" -ForegroundColor DarkGray
}

$ProjectPath = Join-Path $RepoRoot "apps/desktop-runtime/SirThaddeus.DesktopRuntime/SirThaddeus.DesktopRuntime.csproj"
# Remove --debug from args before passing to dotnet run if needed, 
# though DesktopRuntime usually ignores unknown args.
& dotnet run --project $ProjectPath -- $args

exit $LASTEXITCODE
