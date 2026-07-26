#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure we're at repo root (script lives in /dev)
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

Write-Host "`n══════════════════════════════════════════════════════════════"
Write-Host "  Sir Thaddeus Local Runner"
Write-Host "══════════════════════════════════════════════════════════════"

$GitBranch = (& git -C $RepoRoot branch --show-current 2>$null)
$GitCommit = (& git -C $RepoRoot rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -ne 0) {
    $GitBranch = "(git unavailable)"
    $GitCommit = "(unknown)"
}
Write-Host "  Source: $RepoRoot" -ForegroundColor Cyan
Write-Host "  Revision: $GitBranch @ $GitCommit" -ForegroundColor Cyan

$DebugMode = $args -contains "--debug"
$TerminalMode = $args -contains "--terminal"
$OfflineRequested = $args -contains "--offline"
$ForwardArgs = @($args | Where-Object { $_ -ne "--debug" -and $_ -ne "--terminal" -and $_ -ne "--offline" })
$ToolsRequested = $ForwardArgs -contains "--tools"

function Test-InternetAvailable {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "https://api.github.com/" -Method Head -TimeoutSec 3 -ErrorAction Stop
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500)
    }
    catch {
        return $false
    }
}

$IsOffline = $OfflineRequested -or -not (Test-InternetAvailable)
if ($IsOffline) {
    $reason = if ($OfflineRequested) { "--offline was specified" } else { "internet connectivity check failed" }
    Write-Host "      Offline mode enabled ($reason)." -ForegroundColor DarkYellow
}

function Test-ProjectAssetsPresent {
    param([string]$ProjectPath)

    $projectDir = Split-Path -Parent $ProjectPath
    $assetsPath = Join-Path $projectDir "obj\project.assets.json"
    return Test-Path $assetsPath
}

function Invoke-ProjectBuild {
    param(
        [string]$ProjectPath,
        [string]$Label,
        [bool]$Offline = $false
    )

    $buildArgs = @("build", $ProjectPath, "-m:1", "-v", "q")
    $assetsPresent = Test-ProjectAssetsPresent -ProjectPath $ProjectPath
    if ($assetsPresent -or $Offline) {
        $buildArgs += "--no-restore"
    }
    if ($Offline -and -not $assetsPresent) {
        Write-Host "      Offline mode: $Label has no project.assets.json; build may need a prior restore." -ForegroundColor DarkYellow
    }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "      Retrying $Label build with verbose output..." -ForegroundColor DarkYellow

        $retryArgs = @("build", $ProjectPath, "-m:1", "-v", "m")
        if ($assetsPresent -or $Offline) {
            $retryArgs += "--no-restore"
        }

        & dotnet @retryArgs
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Host "`nERROR: $Label build failed." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

function Invoke-WebWorkspaceBuild {
    param(
        [string]$RepoRootPath,
        [bool]$Offline = $false
    )

    $webRoot = Join-Path $RepoRootPath "web"
    $packageJson = Join-Path $webRoot "package.json"
    $nodeModules = Join-Path $webRoot "node_modules"
    if (-not (Test-Path $packageJson)) {
        Write-Host "`nERROR: Web workspace was not found at $webRoot." -ForegroundColor Red
        exit 1
    }

    $npmCommand = if ($env:OS -eq "Windows_NT") { "npm.cmd" } else { "npm" }
    if (-not (Get-Command $npmCommand -ErrorAction SilentlyContinue)) {
        Write-Host "`nERROR: npm is required to build the desktop UI." -ForegroundColor Red
        exit 1
    }

    Push-Location $webRoot
    try {
        if (-not (Test-Path $nodeModules)) {
            if ($Offline) {
                Write-Host "`nERROR: web/node_modules is missing and offline mode is enabled." -ForegroundColor Red
                Write-Host "       Connect once and rerun localrunner so npm ci can restore the UI dependencies." -ForegroundColor Red
                exit 1
            }

            Write-Host "      Installing web dependencies..." -ForegroundColor DarkGray
            & $npmCommand ci
            if ($LASTEXITCODE -ne 0) {
                Write-Host "`nERROR: Web dependency restore failed." -ForegroundColor Red
                exit $LASTEXITCODE
            }
        }

        Write-Host "      Building current React workspace..." -ForegroundColor DarkGray
        & $npmCommand run build
        if ($LASTEXITCODE -ne 0) {
            Write-Host "`nERROR: React workspace build failed." -ForegroundColor Red
            exit $LASTEXITCODE
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-WebBundleSynced {
    param([string]$RepoRootPath)

    $distIndexPath = Join-Path $RepoRootPath "web\dist\index.html"
    $runtimeIndexPath = Join-Path $RepoRootPath "src\Thaddeus.Runtime\wwwroot\index.html"
    if (-not (Test-Path $distIndexPath) -or -not (Test-Path $runtimeIndexPath)) {
        Write-Host "`nERROR: The built React bundle was not synchronized into the runtime." -ForegroundColor Red
        exit 1
    }

    $assetPattern = 'assets/index-[^"''\s]+\.js'
    $distAsset = [regex]::Match((Get-Content -LiteralPath $distIndexPath -Raw), $assetPattern).Value
    $runtimeAsset = [regex]::Match((Get-Content -LiteralPath $runtimeIndexPath -Raw), $assetPattern).Value
    if ([string]::IsNullOrWhiteSpace($distAsset) -or $distAsset -ne $runtimeAsset) {
        Write-Host "`nERROR: Runtime UI bundle does not match the current React build." -ForegroundColor Red
        Write-Host "       web/dist: $distAsset" -ForegroundColor Red
        Write-Host "       runtime:  $runtimeAsset" -ForegroundColor Red
        exit 1
    }

    $runtimeWebRoot = Join-Path $RepoRootPath "src\Thaddeus.Runtime\wwwroot"
    $runtimeAssetPath = Join-Path $runtimeWebRoot ($runtimeAsset -replace '/', '\')
    if (-not (Test-Path $runtimeAssetPath)) {
        Write-Host "`nERROR: Runtime UI entry asset is missing: $runtimeAsset" -ForegroundColor Red
        exit 1
    }

    Write-Host "      Frontend bundle verified: $runtimeAsset" -ForegroundColor Green
}

function Stop-RepoOwnedPortListeners {
    param(
        [string]$RepoRootPath,
        [int[]]$Ports
    )

    foreach ($port in $Ports) {
        $listeners = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique)

            foreach ($listenerPid in $listeners) {
                $proc = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
            if (-not $proc) {
                continue
            }

                $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $listenerPid" -ErrorAction SilentlyContinue
            $details = @(
                $proc.ProcessName,
                $processInfo.ExecutablePath,
                $processInfo.CommandLine
            ) -join " "

            if ($details -like "*$RepoRootPath*" -or $details -like "*SirThaddeus*" -or $details -like "*voice-backend*") {
                    Write-Host "      Releasing port $port from PID $listenerPid ($($proc.ProcessName))..." -ForegroundColor DarkGray
                    Stop-Process -Id $listenerPid -Force -ErrorAction SilentlyContinue
            }
            else {
                    Write-Host "      Leaving unrelated listener on port ${port}: PID $listenerPid ($($proc.ProcessName))." -ForegroundColor DarkYellow
            }
        }
    }
}

function Stop-ExistingInstances {
    param([string]$RepoRootPath)

    Write-Host "`n[0/5] Stopping any existing instances of Sir Thaddeus..." -ForegroundColor Yellow
    $processesToKill = @("SirThaddeus.McpServer", "SirThaddeus.VoiceHost", "SirThaddeus.HeadlessRuntime", "Thaddeus.Runtime", "Thaddeus.Shell")
    foreach ($procName in $processesToKill) {
        $procs = Get-Process -Name $procName -ErrorAction SilentlyContinue
        if ($procs) {
            Write-Host "      Killing $procName..." -ForegroundColor DarkGray
            Stop-Process -Name $procName -Force -ErrorAction SilentlyContinue
        }
    }

    # Release orphaned listeners, but only when they belong to this repo/toolchain.
    # Include runtime API ports so stale external dotnet hosts are not reused.
    Stop-RepoOwnedPortListeners -RepoRootPath $RepoRootPath -Ports @(5378, 5391, 8001, 17845)
}

Stop-ExistingInstances -RepoRootPath $RepoRoot

function Ensure-LocalVoiceAssets {
    param(
        [string]$RepoRootPath,
        [bool]$Offline = $false
    )

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
            if ($Offline) {
                Write-Host "      Missing voice asset payload for $($entry.AssetId). Skipping fetch in offline mode." -ForegroundColor DarkYellow
                continue
            }

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
        $sessionPid = $json.pid
        if ($null -eq $sessionPid -or $sessionPid -le 0) {
            Remove-Item -Force $sessionPath -ErrorAction SilentlyContinue
            Write-Host "      Cleared stale voicehost-session.json (null pid)." -ForegroundColor DarkGray
            return
        }

        $proc = Get-Process -Id $sessionPid -ErrorAction SilentlyContinue
        if (-not $proc) {
            Remove-Item -Force $sessionPath -ErrorAction SilentlyContinue
            Write-Host "      Cleared stale voicehost-session.json (dead pid $sessionPid)." -ForegroundColor DarkGray
        }
    }
    catch {
        # Non-fatal: keep startup moving.
    }
}

function Get-LocalSearxngSidecarStatus {
    param([string]$RepoRootPath)

    $packageRoots = @(
        (Join-Path $RepoRootPath "apps/searxng/package"),
        (Join-Path $RepoRootPath "artifacts/searxng/win-x64/package")
    )

    foreach ($packageRoot in $packageRoots) {
        $startScript = Join-Path $packageRoot "start-searxng.ps1"
        $pythonExe = Join-Path $packageRoot "runtime/python/python.exe"
        $depsRoot = Join-Path $packageRoot "deps/site-packages"
        $sourceRoot = Join-Path $packageRoot "source/searxng-upstream/searx/webapp.py"

        if ((Test-Path $startScript) -and (Test-Path $pythonExe) -and (Test-Path $depsRoot) -and (Test-Path $sourceRoot)) {
            return [pscustomobject]@{
                Ready = $true
                PackageRoot = $packageRoot
                StartScript = $startScript
            }
        }
    }

    return [pscustomobject]@{
        Ready = $false
        PackageRoot = ""
        StartScript = ""
    }
}

function Ensure-LocalSearxngSidecar {
    param(
        [string]$RepoRootPath,
        [bool]$Offline = $false
    )

    $buildScript = Join-Path $RepoRootPath "dev/build-searxng-package.ps1"
    $status = Get-LocalSearxngSidecarStatus -RepoRootPath $RepoRootPath
    if ($status.Ready) {
        return $status
    }

    if (-not (Test-Path $buildScript)) {
        Write-Host "      WARN: missing $buildScript; cannot build bundled SearXNG sidecar." -ForegroundColor Yellow
        return $status
    }

    if ($Offline) {
        Write-Host "      Missing SearXNG sidecar payload. Skipping build in offline mode." -ForegroundColor DarkYellow
        return $status
    }

    Write-Host "      Missing SearXNG sidecar payload. Building..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript 2>&1 | ForEach-Object {
        Write-Host "      $_"
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "      WARN: failed to build bundled SearXNG sidecar (exit $LASTEXITCODE)." -ForegroundColor Yellow
    }

    return (Get-LocalSearxngSidecarStatus -RepoRootPath $RepoRootPath)
}

function Normalize-SearxngSidecarStatus {
    param($SidecarStatus)

    $candidate = $SidecarStatus
    if ($candidate -is [array]) {
        $candidate = $candidate |
            Where-Object { $null -ne $_ -and $null -ne $_.PSObject.Properties['Ready'] } |
            Select-Object -Last 1
    }

    if ($null -eq $candidate -or $null -eq $candidate.PSObject.Properties['Ready']) {
        return [pscustomobject]@{
            Ready = $false
            PackageRoot = ""
            StartScript = ""
        }
    }

    return $candidate
}

function Get-WebSearchRuntimeInfo {
    $settingsPath = Join-Path $env:LOCALAPPDATA "SirThaddeus\settings.json"
    $mode = "auto"
    $autoStart = $true
    $baseUrl = "http://localhost:8080"

    if (Test-Path $settingsPath) {
        try {
            $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
            $webSearchProperty = $settings.PSObject.Properties['webSearch']
            if ($null -ne $webSearchProperty -and $null -ne $webSearchProperty.Value) {
                $webSearch = $webSearchProperty.Value

                $modeProperty = $webSearch.PSObject.Properties['mode']
                if ($null -ne $modeProperty -and
                    -not [string]::IsNullOrWhiteSpace([string]$modeProperty.Value)) {
                    $mode = [string]$modeProperty.Value
                }

                $baseUrlProperty = $webSearch.PSObject.Properties['searxngBaseUrl']
                if ($null -ne $baseUrlProperty -and
                    -not [string]::IsNullOrWhiteSpace([string]$baseUrlProperty.Value)) {
                    $baseUrl = [string]$baseUrlProperty.Value
                }

                $autoStartProperty = $webSearch.PSObject.Properties['searxngAutoStart']
                if ($null -ne $autoStartProperty -and $null -ne $autoStartProperty.Value) {
                    $autoStart = [bool]$autoStartProperty.Value
                }
            }
        }
        catch {
            Write-Host "      WARN: failed to read web search settings from $settingsPath." -ForegroundColor Yellow
        }
    }

    return [pscustomobject]@{
        SettingsPath = $settingsPath
        Mode = $mode
        AutoStart = $autoStart
        BaseUrl = $baseUrl
    }
}

function Test-HttpUrlReachable {
    param([string]$Url)

    if ([string]::IsNullOrWhiteSpace($Url)) {
        return $false
    }

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 2 -ErrorAction Stop
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500)
    }
    catch {
        return $false
    }
}

function Get-RuntimeLockInfo {
    param([string]$LockPath)

    if (-not (Test-Path $LockPath)) {
        return $null
    }

    try {
        $lock = Get-Content $LockPath -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }

    if ($null -eq $lock -or
        $null -eq $lock.pid -or [int]$lock.pid -le 0 -or
        $null -eq $lock.port -or [int]$lock.port -le 0 -or
        [string]::IsNullOrWhiteSpace([string]$lock.token)) {
        return $null
    }

    return $lock
}

function Test-FreshHybridRuntimeStarted {
    param(
        [string]$LockPath,
        [long]$PreviousTicks,
        [int]$TimeoutMs = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $LockPath) {
            $currentTicks = (Get-Item $LockPath).LastWriteTimeUtc.Ticks
            if ($currentTicks -gt $PreviousTicks) {
                $lock = Get-RuntimeLockInfo -LockPath $LockPath
                if ($null -ne $lock) {
                    $proc = Get-Process -Id ([int]$lock.pid) -ErrorAction SilentlyContinue
                    if ($null -ne $proc) {
                        try {
                            $runtimeInfo = Invoke-RestMethod \
                                -Uri ("http://127.0.0.1:{0}/api/runtime-info" -f [int]$lock.port) \
                                -Headers @{ Authorization = "Bearer $($lock.token)" } \
                                -TimeoutSec 2 \
                                -ErrorAction Stop

                            if ($null -ne $runtimeInfo) {
                                return [pscustomobject]@{
                                    Started = $true
                                    Lock = $lock
                                }
                            }
                        }
                        catch {
                            # Lock may be fresh before the API is fully ready; keep polling.
                        }
                    }
                }
            }
        }

        Start-Sleep -Milliseconds 250
    }

    return [pscustomobject]@{
        Started = $false
        Lock = $null
    }
}

function Write-SearxngStartupExpectation {
    param(
        [bool]$IsTerminalMode,
        [bool]$IsToolsRequested,
        $RuntimeInfo,
        $SidecarStatus
    )

    $SidecarStatus = Normalize-SearxngSidecarStatus -SidecarStatus $SidecarStatus
    $healthUrl = ($RuntimeInfo.BaseUrl.TrimEnd('/') + "/search?q=thaddeus&format=json")
    $healthText = if (Test-HttpUrlReachable -Url $healthUrl) { "reachable" } else { "not reachable yet" }
    $sidecarText = if ($SidecarStatus.Ready) {
        "ready ($($SidecarStatus.StartScript))"
    }
    else {
        "missing"
    }

    Write-Host "      Web search mode: $($RuntimeInfo.Mode)" -ForegroundColor DarkGray
    Write-Host "      SearXNG auto-start: $(if ($RuntimeInfo.AutoStart) { 'enabled' } else { 'disabled' })" -ForegroundColor DarkGray
    Write-Host "      SearXNG base URL: $($RuntimeInfo.BaseUrl) ($healthText)" -ForegroundColor DarkGray
    Write-Host "      SearXNG sidecar: $sidecarText" -ForegroundColor DarkGray

    if ($IsTerminalMode) {
        Write-Host "      Tools flag: $(if ($IsToolsRequested) { 'enabled (--tools)' } else { 'disabled' })" -ForegroundColor DarkGray
        if (-not $IsToolsRequested) {
            Write-Host "      SearXNG startup: not expected in terminal mode without --tools." -ForegroundColor Yellow
            return
        }
    }
    else {
        Write-Host "      Runtime launch: UI-managed background runtime" -ForegroundColor DarkGray
    }

    $mode = [string]$RuntimeInfo.Mode
    if ([string]::IsNullOrWhiteSpace($mode)) {
        $mode = "auto"
    }
    $mode = $mode.ToLowerInvariant()
    if ($mode -notin @("auto", "searxng")) {
        Write-Host "      SearXNG startup: skipped because webSearch.mode='$($RuntimeInfo.Mode)' does not use SearXNG." -ForegroundColor Yellow
        return
    }

    if (-not $RuntimeInfo.AutoStart) {
        Write-Host "      SearXNG startup: skipped because webSearch.searxngAutoStart is disabled." -ForegroundColor Yellow
        return
    }

    if (-not $SidecarStatus.Ready) {
        Write-Host "      SearXNG startup: expected by runtime, but the sidecar payload is still missing." -ForegroundColor Yellow
        return
    }

    Write-Host "      SearXNG startup: expected in the background; it will not open a separate window." -ForegroundColor Cyan
}

# 1. Bootstrap (Restores dependencies, validates SDK)
Write-Host "`n[1/5] Bootstrapping environment..." -ForegroundColor Yellow
& "$PSScriptRoot\bootstrap.ps1" -SkipRestore
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nERROR: Bootstrap failed. Cannot start application." -ForegroundColor Red
    exit $LASTEXITCODE
}

# 2. Local voice asset/session repair
Write-Host "`n[2/5] Checking local voice assets/session state..." -ForegroundColor Yellow
Ensure-LocalVoiceAssets -RepoRootPath $RepoRoot -Offline:$IsOffline
Repair-StaleVoiceSessionState
$SearxngSidecarStatus = Ensure-LocalSearxngSidecar -RepoRootPath $RepoRoot -Offline:$IsOffline
$SearxngRuntimeInfo = Get-WebSearchRuntimeInfo

# 3. Build VoiceHost & MCP Server (UI/terminal hosts don't directly reference them)
Write-Host "`n[3/5] Building VoiceHost..." -ForegroundColor Yellow
$VoiceHostPath = Join-Path $RepoRoot "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj"
Invoke-ProjectBuild -ProjectPath $VoiceHostPath -Label "VoiceHost" -Offline:$IsOffline

Write-Host "`n[4/5] Building MCP Server..." -ForegroundColor Yellow
$McpServerPath = Join-Path $RepoRoot "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj"
Invoke-ProjectBuild -ProjectPath $McpServerPath -Label "MCP Server" -Offline:$IsOffline

# 5. Preparation & Execution
if ($DebugMode) {
    Write-Host "`n[5/5] DEBUG MODE: Cleaning up existing background processes..." -ForegroundColor Cyan
    Stop-Process -Name "SirThaddeus.VoiceHost" -Force -ErrorAction SilentlyContinue
    Stop-RepoOwnedPortListeners -RepoRootPath $RepoRoot -Ports @(8001, 17845)

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

$RuntimeProjectPath = Join-Path $RepoRoot "src/Thaddeus.Runtime/Thaddeus.Runtime.csproj"
$ShellProjectPath = Join-Path $RepoRoot "src/Thaddeus.Shell/Thaddeus.Shell.csproj"
$HeadlessProjectPath = Join-Path $RepoRoot "apps/headless-runtime/SirThaddeus.HeadlessRuntime/SirThaddeus.HeadlessRuntime.csproj"

if ($TerminalMode) {
    Write-Host "      Mode: terminal (headless runtime)" -ForegroundColor Cyan
}
else {
    Write-Host "      Mode: desktop shell (Photino window + tray)" -ForegroundColor Cyan
}
Write-SearxngStartupExpectation -IsTerminalMode:$TerminalMode -IsToolsRequested:$ToolsRequested -RuntimeInfo $SearxngRuntimeInfo -SidecarStatus $SearxngSidecarStatus

# Keep startup snappy: rely on normal incremental build.
if ($TerminalMode) {
    Invoke-ProjectBuild -ProjectPath $HeadlessProjectPath -Label "headless runtime" -Offline:$IsOffline
    $ProjectPath = $HeadlessProjectPath
}
else {
    # Shell spawns Runtime as a child via dotnet run --no-build, so both must be
    # built ahead of time in the same (Debug) configuration.
    Invoke-WebWorkspaceBuild -RepoRootPath $RepoRoot -Offline:$IsOffline
    Invoke-ProjectBuild -ProjectPath $RuntimeProjectPath -Label "runtime" -Offline:$IsOffline
    Assert-WebBundleSynced -RepoRootPath $RepoRoot
    Invoke-ProjectBuild -ProjectPath $ShellProjectPath -Label "shell" -Offline:$IsOffline
    $ProjectPath = $ShellProjectPath
}

try {
    & dotnet run --project $ProjectPath --no-build -- $ForwardArgs
    $startupExitCode = $LASTEXITCODE
}
finally {
    if ($DebugMode) {
        Write-Host "`n[DEBUG] Cleaning up background service windows..." -ForegroundColor DarkGray
        if ($null -ne $voiceHostProcess) { Stop-Process -Id $voiceHostProcess.Id -Force -ErrorAction SilentlyContinue }
        if ($null -ne $backendProcess) { Stop-Process -Id $backendProcess.Id -Force -ErrorAction SilentlyContinue }
    }
}

exit $startupExitCode

