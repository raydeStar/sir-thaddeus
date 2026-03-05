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
$TerminalMode = $args -contains "--terminal"
$ForwardArgs = @($args | Where-Object { $_ -ne "--debug" -and $_ -ne "--terminal" })

function Ensure-LocalVoiceAssets {
    param([string]$RepoRootPath)

    $voiceBackendDir = Join-Path $RepoRootPath "apps/voice-backend"
    $fetchScript = Join-Path $RepoRootPath "dev/fetch-assets.ps1"

    if (-not (Test-Path $fetchScript)) {
        Write-Host "      WARN: missing $fetchScript; cannot self-heal voice assets." -ForegroundColor Yellow
        return
    }

    $assetChecks = @(
        @{ AssetId = "voice-runtime-win-x64"; MarkerDir = $voiceBackendDir; Marker = ".installed.marker"; Required = @((Join-Path $voiceBackendDir "runtime\python\python.exe"), (Join-Path $voiceBackendDir "bin\uv.exe")) },
        @{ AssetId = "voice-deps-win-x64"; MarkerDir = Join-Path $voiceBackendDir "deps\wheels"; Marker = ".installed.marker"; Required = @(Join-Path $voiceBackendDir "deps\wheels\faster_whisper-1.2.1-py3-none-any.whl") },
        @{ AssetId = "piper-win-x64"; MarkerDir = Join-Path $voiceBackendDir "piper"; Marker = ".installed.marker"; Required = @(Join-Path $voiceBackendDir "piper\piper.exe") },
        @{ AssetId = "piper-voice-en_US-john-medium"; MarkerDir = Join-Path $voiceBackendDir "piper-voices\en_US-john-medium"; Marker = ".installed.marker"; Required = @(Join-Path $voiceBackendDir "piper-voices\en_US-john-medium\en_US-john-medium.onnx") },
        @{ AssetId = "stt-model-whisper-base"; MarkerDir = Join-Path $voiceBackendDir "stt-models\base"; Marker = ".installed.marker"; Required = @(Join-Path $voiceBackendDir "stt-models\base\model.bin") }
    )

    foreach ($entry in $assetChecks) {
        $markerPath = Join-Path $entry.MarkerDir $entry.Marker
        $hasMissingPayload = $false

        foreach ($requiredPath in $entry.Required) {
            if (-not (Test-Path $requiredPath)) {
                $hasMissingPayload = $true
                break
            }
        }

        if ((Test-Path $markerPath) -and $hasMissingPayload) {
            Write-Host "      Detected stale marker for $($entry.AssetId); clearing marker..." -ForegroundColor DarkGray
            try {
                attrib -r $markerPath 2>$null
                Remove-Item -Force $markerPath -ErrorAction SilentlyContinue
            }
            catch {
                # best effort
            }
        }

        if ($hasMissingPayload) {
            Write-Host "      Missing voice asset payload for $($entry.AssetId). Fetching..." -ForegroundColor Cyan
            & powershell -NoProfile -ExecutionPolicy Bypass -File $fetchScript -AssetId $entry.AssetId
            if ($LASTEXITCODE -ne 0) {
                Write-Host "      WARN: failed to fetch $($entry.AssetId) (exit $LASTEXITCODE)." -ForegroundColor Yellow
            }
        }
    }
}

function Repair-StaleVoiceSessionState {
    $sessionPath = Join-Path $env:LOCALAPPDATA "SirThaddeus\voicehost-session.json"
    if (-not (Test-Path $sessionPath)) { return }

    try {
        $json = Get-Content $sessionPath -Raw | ConvertFrom-Json
        $pid = $json.pid
        if ($null -eq $pid -or $pid -le 0) {
            Remove-Item -Force $sessionPath -ErrorAction SilentlyContinue
            Write-Host "      Cleared stale voicehost-session.json (null pid)." -ForegroundColor DarkGray
            return
        }

        $proc = Get-Process -Id $pid -ErrorAction SilentlyContinue
        if (-not $proc) {
            Remove-Item -Force $sessionPath -ErrorAction SilentlyContinue
            Write-Host "      Cleared stale voicehost-session.json (dead pid $pid)." -ForegroundColor DarkGray
        }
    }
    catch {
        # Non-fatal: keep startup moving.
    }
}

# 1. Bootstrap (Restores dependencies, validates SDK)
Write-Host "`n[1/5] Bootstrapping environment..." -ForegroundColor Yellow
& "$PSScriptRoot\bootstrap.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nERROR: Bootstrap failed. Cannot start application." -ForegroundColor Red
    exit $LASTEXITCODE
}

# 2. Local voice asset/session repair
Write-Host "`n[2/5] Checking local voice assets/session state..." -ForegroundColor Yellow
Ensure-LocalVoiceAssets -RepoRootPath $RepoRoot
Repair-StaleVoiceSessionState

# 3. Build VoiceHost & MCP Server (UI/terminal hosts don't directly reference them)
Write-Host "`n[3/5] Building VoiceHost..." -ForegroundColor Yellow
$VoiceHostPath = Join-Path $RepoRoot "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj"
& dotnet build $VoiceHostPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nERROR: VoiceHost build failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n[4/5] Building MCP Server..." -ForegroundColor Yellow
$McpServerPath = Join-Path $RepoRoot "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj"
& dotnet build $McpServerPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nERROR: MCP Server build failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

# 5. Preparation & Execution
if ($DebugMode) {
    Write-Host "`n[5/5] DEBUG MODE: Cleaning up existing background processes..." -ForegroundColor Cyan
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
    Write-Host "      Starting runtime host..." -ForegroundColor Yellow
}
else {
    Write-Host "`n[5/5] Starting Sir Thaddeus..." -ForegroundColor Yellow
    Write-Host "      VoiceHost and Backend services will auto-start as needed." -ForegroundColor DarkGray
    Write-Host "      (Use --debug to see background service logs in separate windows)" -ForegroundColor DarkGray
}

$ProjectPath = if ($TerminalMode) {
    Join-Path $RepoRoot "apps/headless-runtime/SirThaddeus.HeadlessRuntime/SirThaddeus.HeadlessRuntime.csproj"
}
else {
    Join-Path $RepoRoot "apps/ui-avalonia/SirThaddeus.UI.Avalonia/SirThaddeus.UI.Avalonia.csproj"
}

if ($TerminalMode) {
    Write-Host "      Mode: terminal (headless runtime)" -ForegroundColor Cyan
}
else {
    Write-Host "      Mode: Avalonia UI" -ForegroundColor Cyan
}

# Keep startup snappy: rely on normal incremental build.
& dotnet build $ProjectPath -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nERROR: startup project build failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

try {
    & dotnet run --project $ProjectPath --no-build -- $ForwardArgs
}
finally {
    if ($DebugMode) {
        Write-Host "`n[DEBUG] Cleaning up background service windows..." -ForegroundColor DarkGray
        if ($null -ne $voiceHostProcess) { Stop-Process -Id $voiceHostProcess.Id -Force -ErrorAction SilentlyContinue }
        if ($null -ne $backendProcess) { Stop-Process -Id $backendProcess.Id -Force -ErrorAction SilentlyContinue }
    }
}

exit $LASTEXITCODE

