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
    $BackendScript = Join-Path $RepoRoot "apps/voice-backend/start-voice-backend.ps1"
    $backendProcess = Start-Process powershell -ArgumentList "-NoExit", "-File", "`"$BackendScript`"" -WindowStyle Normal -PassThru

    # Launch VoiceHost
    $VoiceHostCsproj = Join-Path $RepoRoot "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj"
    $voiceHostProcess = Start-Process powershell -ArgumentList "-NoExit", "-Command", "dotnet run --project `"$VoiceHostCsproj`"" -WindowStyle Normal -PassThru

    Write-Host "      Waiting for VoiceHost to initialize..." -ForegroundColor DarkGray
    $maxWait = 45
    while ($maxWait -gt 0) {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:17845/health" -ErrorAction Stop
            if ($null -ne $health -and $health.status -eq 'ok') { break }
        }
        catch {
            # Ignore connection refused or timeout errors while waiting for the service to bind
        }
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
# Force a non-incremental build so XAML/code-behind edits are always picked up.
& dotnet build $ProjectPath --no-incremental -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nERROR: DesktopRuntime build failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

try {
    & dotnet run --project $ProjectPath --no-build -- $args
}
finally {
    if ($DebugMode) {
        Write-Host "`n[DEBUG] Cleaning up background service windows..." -ForegroundColor DarkGray
        if ($null -ne $voiceHostProcess) { Stop-Process -Id $voiceHostProcess.Id -Force -ErrorAction SilentlyContinue }
        if ($null -ne $backendProcess) { Stop-Process -Id $backendProcess.Id -Force -ErrorAction SilentlyContinue }
    }
}

exit $LASTEXITCODE

