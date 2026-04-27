#requires -Version 5.1

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [string]$Version = "",

    [switch]$SkipPreflight,

    [switch]$LiteBundle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host "══════════════════════════════════════════════════════════════"
    Write-Host "  $Title"
    Write-Host "══════════════════════════════════════════════════════════════"
}

function Fail([string]$Message, [int]$Code = 1) {
    Write-Host "  FAIL: $Message" -ForegroundColor Red
    exit $Code
}

function Assert-OrWarn([bool]$Condition, [string]$ErrorMessage, [string]$WarnMessage, [bool]$Required) {
    if ($Condition) {
        return
    }

    if ($Required) {
        Fail $ErrorMessage
    }

    Write-Host "  WARN: $WarnMessage" -ForegroundColor Yellow
}

function Get-VersionLabel([string]$RawVersion) {
    if ([string]::IsNullOrWhiteSpace($RawVersion)) {
        return ""
    }

    $value = $RawVersion.Trim()
    if ($value.StartsWith("refs/tags/", [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring("refs/tags/".Length)
    }

    # Keep the version token file-name safe.
    return ($value -replace '[^0-9A-Za-z\.\-_]', '-')
}

function Get-AssemblyVersion([string]$VersionLabel) {
    # MSBuild's -p:Version accepts NuGet/SemVer versions, not branch labels.
    # Keep non-release labels for archive names, but let MSBuild use the
    # repo default version for those builds.
    if ([string]::IsNullOrWhiteSpace($VersionLabel)) {
        return ""
    }

    $value = $VersionLabel.Trim()
    if ($value -match '^[vV](?=\d)') {
        $value = $value.Substring(1)
    }

    if ($value -notmatch '^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z][0-9A-Za-z\.-]*)?(\+[0-9A-Za-z][0-9A-Za-z\.-]*)?$') {
        return ""
    }

    return $value
}

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$publishDir = Join-Path $RepoRoot "artifacts/publish/$Runtime"
$stageDir = Join-Path $RepoRoot "artifacts/stage/$Runtime"
$releaseDir = Join-Path $RepoRoot "artifacts/release"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$effectiveSelfContained = if ($PSBoundParameters.ContainsKey("SelfContained")) {
    $SelfContained.IsPresent
}
elseif ($Configuration -eq "Release") {
    # MVP packaging should be self-contained by default in release mode.
    $true
}
else {
    $false
}

$selfContainedValue = if ($effectiveSelfContained) { "true" } else { "false" }
$bundleProfile = if ($LiteBundle.IsPresent) { "lite" } else { "full" }
$versionLabel = Get-VersionLabel $Version
$assemblyVersion = Get-AssemblyVersion $versionLabel
$archiveToken = if ([string]::IsNullOrWhiteSpace($versionLabel)) {
    Get-Date -Format "yyyyMMdd-HHmmss"
}
else {
    $versionLabel
}

$archiveStem = "sir-thaddeus-$Runtime-$archiveToken-$bundleProfile"
$archiveName = "$archiveStem.zip"
$archivePath = Join-Path $releaseDir $archiveName
$checksumPath = "$archivePath.sha256.txt"
$contentsChecksumsPath = Join-Path $releaseDir "$archiveStem-contents.sha256.txt"

$firstRunReadmeSource = Join-Path $RepoRoot "README_FIRST_RUN.md"
$settingsTemplateSource = Join-Path $RepoRoot "SirThaddeus.Settings.template.json"

Write-Section "Package Settings"
Write-Host "  Configuration : $Configuration"
Write-Host "  Runtime       : $Runtime"
Write-Host "  SelfContained : $effectiveSelfContained"
Write-Host "  Bundle profile: $(if ($LiteBundle) { 'lite' } else { 'full' })"
if ([string]::IsNullOrWhiteSpace($versionLabel)) {
    Write-Host "  Version       : <timestamp>"
}
else {
    Write-Host "  Version       : $versionLabel"
}
if (-not [string]::IsNullOrWhiteSpace($versionLabel) -and [string]::IsNullOrWhiteSpace($assemblyVersion)) {
    Write-Host "  MSBuildVersion: <default; label is not NuGet-compatible>"
}
elseif (-not [string]::IsNullOrWhiteSpace($assemblyVersion)) {
    Write-Host "  MSBuildVersion: $assemblyVersion"
}
Write-Host "  Publish dir   : $publishDir"
Write-Host "  Stage dir     : $stageDir"
Write-Host "  Release dir   : $releaseDir"

if (-not $SkipPreflight) {
    Write-Section "Preflight Gate"
    & "$PSScriptRoot\preflight.ps1"
    if ($LASTEXITCODE -ne 0) {
        Fail "preflight gate failed (exit code $LASTEXITCODE)." $LASTEXITCODE
    }
}

$includeOptionalBundledAssets = -not $LiteBundle.IsPresent
$strictVoiceAssetGate = $Configuration -eq "Release"

# -- Verify Piper TTS assets ---------------------------------------------------
# Assets are fetched from GitHub Releases via dev/fetch-assets.ps1 (CI)
# or downloaded at runtime by AssetManager. Verify they are present.

$voiceBackendDir = Join-Path $RepoRoot "apps/voice-backend"
$piperExe = Join-Path $voiceBackendDir "piper/piper.exe"
$piperVoiceModel = Join-Path $voiceBackendDir "piper-voices/en_US-john-medium/en_US-john-medium.onnx"

if ($includeOptionalBundledAssets) {
    Write-Section "Verify Piper TTS Assets"

    if (Test-Path $piperExe) {
        Write-Host "  piper.exe present"
    }
    else {
        Assert-OrWarn `
            -Condition $false `
            -ErrorMessage "piper.exe not found at $piperExe" `
            -WarnMessage "piper.exe not found at $piperExe" `
            -Required $strictVoiceAssetGate
    }

    if (Test-Path $piperVoiceModel) {
        $voiceSize = [math]::Round((Get-Item $piperVoiceModel).Length / 1MB, 1)
        Write-Host "  Default voice model present (${voiceSize} MB)"
    }
    else {
        Assert-OrWarn `
            -Condition $false `
            -ErrorMessage "Default voice model not found at $piperVoiceModel" `
            -WarnMessage "Default voice model not found at $piperVoiceModel" `
            -Required $strictVoiceAssetGate
    }
}
else {
    Write-Section "Lite Profile"
    Write-Host "  Skipping bundled voice + Playwright payloads to minimize package size."
}

Write-Section "Publish Artifacts"

$projects = @(
    "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj",
    "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj",
    "src/Thaddeus.Runtime/Thaddeus.Runtime.csproj"
)

$projectStageSubdirs = @{
    "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj" = ""
    "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj" = ""
    "src/Thaddeus.Runtime/Thaddeus.Runtime.csproj" = ""
}

$optionalSearxngProject = "apps/searxng/SirThaddeus.Searxng/SirThaddeus.Searxng.csproj"

$projectFrameworkOverrides = @{
    "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj" = @{
        default = "net10.0"
        windows = "net10.0-windows10.0.19041.0"
    }
    "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj" = @{
        default = "net10.0"
    }
    "src/Thaddeus.Runtime/Thaddeus.Runtime.csproj" = @{
        default = "net10.0"
    }
}

if (Test-Path (Join-Path $RepoRoot $optionalSearxngProject)) {
    $projects += $optionalSearxngProject
    $projectStageSubdirs[$optionalSearxngProject] = "search"
    $projectFrameworkOverrides[$optionalSearxngProject] = @{
        default = "net10.0"
    }
}

function Resolve-PublishFramework([string]$ProjectPath, [string]$TargetRuntime) {
    if (-not $projectFrameworkOverrides.ContainsKey($ProjectPath)) {
        return $null
    }

    $frameworkSet = $projectFrameworkOverrides[$ProjectPath]
    $isWindowsRuntime = $TargetRuntime -like "win-*"

    if ($isWindowsRuntime -and $frameworkSet.ContainsKey("windows")) {
        return $frameworkSet["windows"]
    }

    if ($frameworkSet.ContainsKey("default")) {
        return $frameworkSet["default"]
    }

    return $null
}

function Copy-DirectoryContents([string]$SourceRoot, [string]$DestinationRoot) {
    if (-not (Test-Path $SourceRoot)) {
        throw "Source directory not found: $SourceRoot"
    }

    New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null

    @(Get-ChildItem -Path $SourceRoot -Recurse -Force) | ForEach-Object {
        $relativePath = $_.FullName.Substring($SourceRoot.Length).TrimStart('\')
        $destinationPath = Join-Path $DestinationRoot $relativePath

        if ($_.PSIsContainer) {
            New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
            return
        }

        $destinationParent = Split-Path $destinationPath -Parent
        if (-not (Test-Path $destinationParent)) {
            New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
        }

        Copy-Item -Path $_.FullName -Destination $destinationPath -Force -ErrorAction Stop
    }
}

function Test-SearxngPayloadRoot([string]$CandidateRoot, [ref]$MissingPath) {
    $requiredPaths = @(
        "start-searxng.ps1",
        "runtime\python\python.exe",
        "source\searxng-upstream\searx\webapp.py",
        "deps\site-packages\flask\__init__.py"
    )

    foreach ($relativePath in $requiredPaths) {
        $candidatePath = Join-Path $CandidateRoot $relativePath
        if (-not (Test-Path $candidatePath)) {
            $MissingPath.Value = $candidatePath
            return $false
        }
    }

    $MissingPath.Value = $null
    return $true
}

foreach ($project in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $projectPublishDir = Join-Path $RepoRoot "artifacts/publish/$projectName/$Runtime"
    $targetFramework = Resolve-PublishFramework -ProjectPath $project -TargetRuntime $Runtime
    
    if (Test-Path $projectPublishDir) {
        Remove-Item -Path $projectPublishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $projectPublishDir | Out-Null

    if ([string]::IsNullOrWhiteSpace($targetFramework)) {
        Write-Host "  Publishing $project to $projectPublishDir"
    }
    else {
        Write-Host "  Publishing $project to $projectPublishDir (framework: $targetFramework)"
    }

    $publishArgs = @(
        "publish", $project,
        "-m:1",
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $selfContainedValue,
        "-o", $projectPublishDir
    )

    if (-not [string]::IsNullOrWhiteSpace($targetFramework)) {
        $publishArgs += @("-f", $targetFramework)
    }

    if (-not [string]::IsNullOrWhiteSpace($assemblyVersion)) {
        $publishArgs += @("-p:Version=$assemblyVersion")
    }

    $nativePreference = $null
    $hasNativePreference = $false
    try {
        $nativeVariable = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
        if ($null -ne $nativeVariable) {
            $hasNativePreference = $true
            $nativePreference = $nativeVariable.Value
            $script:PSNativeCommandUseErrorActionPreference = $false
        }

        & dotnet @publishArgs
    }
    finally {
        if ($hasNativePreference) {
            $script:PSNativeCommandUseErrorActionPreference = $nativePreference
        }
    }

    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet publish failed for $project (exit code $LASTEXITCODE)." $LASTEXITCODE
    }
}

# -- Structured staging --------------------------------------------------------
#
#  ZIP root/
#   ├── Thaddeus.Runtime.exe             ← user launches this
#   ├── SirThaddeus.McpServer.exe
#   ├── SirThaddeus.VoiceHost.exe
#   ├── README_FIRST_RUN.md
#   └── support files (DLLs, assets, voice/, search/)
#

Write-Section "Stage Artifacts"

if (Test-Path $stageDir) {
    Remove-Item -Path $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

foreach ($project in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $projectPublishDir = Join-Path $RepoRoot "artifacts/publish/$projectName/$Runtime"
    $stageSubdir = if ($projectStageSubdirs.ContainsKey($project)) { $projectStageSubdirs[$project] } else { "" }
    
    if ([string]::IsNullOrWhiteSpace($stageSubdir)) {
        Write-Host "  Staging $projectName files..."
    }
    else {
        Write-Host "  Staging $projectName files -> $stageSubdir/"
    }

    @(Get-ChildItem -Path $projectPublishDir -Recurse) | ForEach-Object {
        $relativePath = $_.FullName.Substring($projectPublishDir.Length).TrimStart('\')
        $destRoot = if ([string]::IsNullOrWhiteSpace($stageSubdir)) {
            $stageDir
        }
        else {
            Join-Path $stageDir $stageSubdir
        }
        $dest = Join-Path $destRoot $relativePath

        if ($_.PSIsContainer) {
            New-Item -ItemType Directory -Path $dest -Force | Out-Null
            return
        }

        $destParent = Split-Path $dest -Parent
        if (-not (Test-Path $destParent)) {
            New-Item -ItemType Directory -Path $destParent -Force | Out-Null
        }

        Copy-Item -Path $_.FullName -Destination $dest -Force -ErrorAction Stop
    }
}

$searchStageDir = Join-Path $stageDir "search"
$searchPayloadRequired = $Configuration -eq "Release"
$searchSidecarStaged = $false
$searchStageFailures = @()
$rawSearxngPayloadCandidates = @(
    (Join-Path $RepoRoot "apps/searxng/package"),
    (Join-Path $RepoRoot "artifacts/searxng/$Runtime/package"),
    (Join-Path $RepoRoot "apps/searxng/dist")
)

foreach ($candidate in $rawSearxngPayloadCandidates) {
    if (-not (Test-Path $candidate)) {
        continue
    }

    $missingSearchPath = $null
    if (-not (Test-SearxngPayloadRoot -CandidateRoot $candidate -MissingPath ([ref]$missingSearchPath))) {
        Write-Host "  WARN: skipping invalid search payload candidate $candidate (missing $missingSearchPath)" -ForegroundColor Yellow
        continue
    }

    try {
        if (Test-Path $searchStageDir) {
            Remove-Item -Path $searchStageDir -Recurse -Force
        }

        Copy-DirectoryContents -SourceRoot $candidate -DestinationRoot $searchStageDir
        Write-Host "  Staged: search/ payload from $candidate"
        $searchSidecarStaged = $true
        break
    }
    catch {
        $message = $_.Exception.Message
        $searchStageFailures += "${candidate}: $message"
        Write-Host "  WARN: failed to stage search payload from $candidate ($message)" -ForegroundColor Yellow
    }
}

if (-not $searchSidecarStaged) {
    $detail = if ($searchStageFailures.Count -gt 0) {
        " Tried candidates: " + ($searchStageFailures -join "; ")
    }
    else {
        ""
    }

    Assert-OrWarn `
        -Condition $false `
        -ErrorMessage "Bundled SearXNG sidecar payload not found or could not be staged.$detail" `
        -WarnMessage "Bundled SearXNG sidecar payload not found or could not be staged.$detail" `
        -Required $searchPayloadRequired
}

if ($includeOptionalBundledAssets) {
    # -- Bundle voice-backend assets (GitHub Releases) ---------------------------
    # These are fetched from GitHub Releases via dev/fetch-assets.ps1 in CI.
    # They land under voice/ alongside the Python scripts staged by VoiceHost publish.

    $voiceStageDir = Join-Path $stageDir "voice"
    if (-not (Test-Path $voiceStageDir)) {
        New-Item -ItemType Directory -Force -Path $voiceStageDir | Out-Null
    }

    # Piper TTS native binary (standalone exe + DLLs + espeak-ng-data)
    $piperSource = Join-Path $voiceBackendDir "piper"
    if (Test-Path $piperSource) {
        $piperDest = Join-Path $voiceStageDir "piper"
        New-Item -ItemType Directory -Force -Path $piperDest | Out-Null
        Copy-Item -Path (Join-Path $piperSource "*") -Destination $piperDest -Recurse -Force
        $piperCount = @(Get-ChildItem -Path $piperDest -Recurse -File).Count
        Write-Host "  Staged: piper/ ($piperCount files)"
    }
    else {
        Assert-OrWarn `
            -Condition $false `
            -ErrorMessage "piper/ directory not found; TTS will be unavailable" `
            -WarnMessage "piper/ directory not found; TTS will be unavailable" `
            -Required $strictVoiceAssetGate
    }

    # Piper voice models (default: en_US-john-medium)
    $piperVoicesSource = Join-Path $voiceBackendDir "piper-voices"
    if (Test-Path $piperVoicesSource) {
        $piperVoicesDest = Join-Path $voiceStageDir "piper-voices"
        New-Item -ItemType Directory -Force -Path $piperVoicesDest | Out-Null
        Copy-Item -Path (Join-Path $piperVoicesSource "*") -Destination $piperVoicesDest -Recurse -Force
        $voiceCount = @(Get-ChildItem -Path $piperVoicesDest -Recurse -Filter "*.onnx").Count
        Write-Host "  Staged: piper-voices/ ($voiceCount voice model(s))"
    }
    else {
        Assert-OrWarn `
            -Condition $false `
            -ErrorMessage "piper-voices/ directory not found; no bundled voices" `
            -WarnMessage "piper-voices/ directory not found; no bundled voices" `
            -Required $strictVoiceAssetGate
    }

    # uv.exe — Python environment manager
    $uvSource = Join-Path $voiceBackendDir "bin/uv.exe"
    if (Test-Path $uvSource) {
        $uvDest = Join-Path $voiceStageDir "bin"
        New-Item -ItemType Directory -Force -Path $uvDest | Out-Null
        Copy-Item -Path $uvSource -Destination (Join-Path $uvDest "uv.exe") -Force
        Write-Host "  Staged: bin/uv.exe"
    }
    else {
        Assert-OrWarn `
            -Condition $false `
            -ErrorMessage "bundled uv.exe not found; offline venv creation may fail" `
            -WarnMessage "bundled uv.exe not found; offline venv creation may fail" `
            -Required $strictVoiceAssetGate
    }

    # Bundled Python 3.11 runtime
    $runtimeSource = Join-Path $voiceBackendDir "runtime/python"
    if (Test-Path $runtimeSource) {
        $runtimeDest = Join-Path $voiceStageDir "runtime/python"
        New-Item -ItemType Directory -Force -Path $runtimeDest | Out-Null
        Copy-Item -Path (Join-Path $runtimeSource "*") -Destination $runtimeDest -Recurse -Force
        $runtimeCount = @(Get-ChildItem -Path $runtimeDest -Recurse -File).Count
        Write-Host "  Staged: runtime/python ($runtimeCount files)"
    }
    else {
        Assert-OrWarn `
            -Condition $false `
            -ErrorMessage "bundled Python runtime not found; will download at first run" `
            -WarnMessage "bundled Python runtime not found; will download at first run" `
            -Required $strictVoiceAssetGate
    }

    # Python wheel dependencies (offline pip install)
    $wheelsSource = Join-Path $voiceBackendDir "deps/wheels"
    if ((Test-Path $wheelsSource) -and (Get-ChildItem -Path $wheelsSource -Filter "*.whl" | Measure-Object).Count -gt 0) {
        $wheelsDest = Join-Path $voiceStageDir "deps/wheels"
        New-Item -ItemType Directory -Force -Path $wheelsDest | Out-Null
        Copy-Item -Path (Join-Path $wheelsSource "*.whl") -Destination $wheelsDest -Force
        $wheelCount = @(Get-ChildItem -Path $wheelsDest -Filter "*.whl").Count
        $wheelSizeMB = [math]::Round(((Get-ChildItem -Path $wheelsDest -Filter "*.whl" | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
        Write-Host "  Staged: deps/wheels ($wheelCount wheels, ${wheelSizeMB} MB)"
    }
    else {
        Assert-OrWarn `
            -Condition $false `
            -ErrorMessage "bundled Python wheels not found; will download at first run" `
            -WarnMessage "bundled Python wheels not found; will download at first run" `
            -Required $strictVoiceAssetGate
    }

    # Faster-Whisper base STT model
    $sttSource = Join-Path $voiceBackendDir "stt-models/base"
    if (Test-Path $sttSource) {
        $sttDest = Join-Path $voiceStageDir "stt-models/base"
        New-Item -ItemType Directory -Force -Path $sttDest | Out-Null
        Copy-Item -Path (Join-Path $sttSource "*") -Destination $sttDest -Recurse -Force
        Write-Host "  Staged: stt-models/base"

        $sttModelFile = Join-Path $sttDest "model.bin"
        Assert-OrWarn `
            -Condition (Test-Path $sttModelFile) `
            -ErrorMessage "bundled STT model.bin missing at $sttModelFile" `
            -WarnMessage "bundled STT model.bin missing at $sttModelFile" `
            -Required $strictVoiceAssetGate
    }
    else {
        Assert-OrWarn `
            -Condition $false `
            -ErrorMessage "bundled STT model not found; will download at first run" `
            -WarnMessage "bundled STT model not found; will download at first run" `
            -Required $strictVoiceAssetGate
    }
}
else {
    # Remove optional heavyweight payloads for a smaller runtime package.
    $litePruneTargets = @(
        ".playwright",
        "voice/piper",
        "voice/piper-voices",
        "voice/runtime",
        "voice/deps",
        "voice/stt-models"
    )

    foreach ($relative in $litePruneTargets) {
        $target = Join-Path $stageDir $relative
        if (Test-Path $target) {
            Remove-Item -Path $target -Recurse -Force
            Write-Host "  Pruned: $relative"
        }
    }

    $uvTarget = Join-Path $stageDir "voice/bin/uv.exe"
    if (Test-Path $uvTarget) {
        Remove-Item -Path $uvTarget -Force
        Write-Host "  Pruned: voice/bin/uv.exe"
    }
}

# Stage asset manifest so the app can self-heal (download missing assets at runtime)
$manifestSource = Join-Path $RepoRoot "assets/manifest.json"
if (Test-Path $manifestSource) {
    $manifestDest = Join-Path $stageDir "assets"
    New-Item -ItemType Directory -Force -Path $manifestDest | Out-Null
    Copy-Item -Path $manifestSource -Destination (Join-Path $manifestDest "manifest.json") -Force
    Write-Host "  Staged: assets/manifest.json"
}
else {
    Assert-OrWarn `
        -Condition $false `
        -ErrorMessage "assets/manifest.json not found; runtime asset download will be unavailable" `
        -WarnMessage "assets/manifest.json not found; runtime asset download will be unavailable" `
        -Required $strictVoiceAssetGate
}

if (-not (Test-Path $firstRunReadmeSource)) {
    Fail "required file is missing: $firstRunReadmeSource"
}
Copy-Item -Path $firstRunReadmeSource -Destination (Join-Path $stageDir "README_FIRST_RUN.md") -Force

$disclaimerSource = Join-Path $RepoRoot "DISCLAIMER.md"
if (Test-Path $disclaimerSource) {
    Copy-Item -Path $disclaimerSource -Destination (Join-Path $stageDir "DISCLAIMER.md") -Force
}

if (Test-Path $settingsTemplateSource) {
    Copy-Item -Path $settingsTemplateSource -Destination (Join-Path $stageDir "SirThaddeus.Settings.template.json") -Force
}
else {
    Write-Host "  WARN: optional template missing: $settingsTemplateSource" -ForegroundColor Yellow
}

# Public MVP ZIP should not ship debug symbols.
$pdbFiles = @(Get-ChildItem -Path $stageDir -File -Recurse -Filter "*.pdb")
if ($pdbFiles.Count -gt 0) {
    foreach ($pdb in $pdbFiles) {
        Remove-Item -Path $pdb.FullName -Force
    }
    Write-Host "  Removed debug symbols: $($pdbFiles.Count)"
}

# Trim cross-platform Playwright node binaries. The Microsoft.Playwright
# NuGet package ships node launchers for darwin/linux/win in .playwright/node/,
# but a self-contained build only ever runs on its target RID. Keeping the
# others bloats the public download by ~450 MB on Windows (where the publish
# output didn't even include a win32 node, since playwright.ps1 + system node
# is the Windows path).
$playwrightNodeDir = Join-Path $stageDir ".playwright/node"
if (Test-Path $playwrightNodeDir) {
    $keepByRuntime = @{
        "win-x64"     = @("win32_x64")
        "linux-x64"   = @("linux-x64")
        "linux-arm64" = @("linux-arm64")
        "osx-x64"     = @("darwin-x64")
        "osx-arm64"   = @("darwin-arm64")
    }
    $keep = if ($keepByRuntime.ContainsKey($Runtime)) { $keepByRuntime[$Runtime] } else { @() }
    $sizeBefore = ((Get-ChildItem -Path $playwrightNodeDir -File -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum)
    $prunedDirs = 0
    Get-ChildItem -Path $playwrightNodeDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Name -notin $keep) {
            Remove-Item -Path $_.FullName -Recurse -Force
            $prunedDirs++
        }
    }
    if ($prunedDirs -gt 0) {
        $sizeAfter = if (Test-Path $playwrightNodeDir) {
            ((Get-ChildItem -Path $playwrightNodeDir -File -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum)
        } else { 0 }
        $freedMB = [math]::Round((($sizeBefore - $sizeAfter) / 1MB), 1)
        Write-Host "  Pruned cross-RID Playwright node dirs: $prunedDirs (${freedMB} MB freed)"
    }
}

# Strip XmlDoc files at the stage root. They're IDE/IntelliSense aids, not
# runtime artifacts — not needed in a user-facing zip.
$xmlDocFiles = @(Get-ChildItem -Path $stageDir -File -Filter "*.xml")
if ($xmlDocFiles.Count -gt 0) {
    foreach ($xml in $xmlDocFiles) { Remove-Item -Path $xml.FullName -Force }
    Write-Host "  Removed XmlDoc files: $($xmlDocFiles.Count)"
}

Write-Section "Archive + Checksums"

foreach ($p in @($archivePath, $checksumPath, $contentsChecksumsPath)) {
    if (Test-Path $p) { Remove-Item $p -Force }
}

# Wrap zip contents in a single top-level folder so users get one tidy folder
# on extract instead of a 200-file DLL spew in their target directory. Mirrors
# the tar.gz flow in dev/package-cross.ps1 which already wraps.
$archiveRootName = $archiveStem
$parentStageDir = Split-Path $stageDir -Parent
$renamedStageDir = Join-Path $parentStageDir $archiveRootName
$stageDirLeaf = Split-Path $stageDir -Leaf
$stageWasRenamed = $false
if ($stageDirLeaf -ne $archiveRootName) {
    if (Test-Path $renamedStageDir) {
        Remove-Item -Path $renamedStageDir -Recurse -Force
    }
    Rename-Item -Path $stageDir -NewName $archiveRootName
    $stageWasRenamed = $true
}

try {
    Compress-Archive -Path $renamedStageDir -DestinationPath $archivePath -CompressionLevel Optimal -Force
}
finally {
    if ($stageWasRenamed) {
        # Restore so smoke-test, debug-package, and clean-rebuild-launch still
        # find the stage dir at the legacy "artifacts/stage/<rid>" path.
        Rename-Item -Path $renamedStageDir -NewName $stageDirLeaf
    }
}

$zipHash = Get-FileHash -Path $archivePath -Algorithm SHA256
"$($zipHash.Hash) *$archiveName" | Out-File -FilePath $checksumPath -Encoding ASCII -Force

$archiveSizeMB = [math]::Round((Get-Item $archivePath).Length / 1MB, 1)
Write-Host "  Bundle archive: $archiveName (${archiveSizeMB} MB)"

$stageRootPrefix = $stageDir
if ($stageRootPrefix -notmatch '\\$') { $stageRootPrefix += '\' }
$stagedFiles = Get-ChildItem -Path $stageDir -File -Recurse | Sort-Object FullName
$contentLines = foreach ($file in $stagedFiles) {
    $hash = Get-FileHash -Path $file.FullName -Algorithm SHA256
    $relativePath = $file.FullName.Substring($stageRootPrefix.Length).Replace('\', '/')
    # Prefix with wrapper folder so contents checksums match the post-extract layout.
    "$($hash.Hash) *$archiveRootName/$relativePath"
}
$contentLines | Out-File -FilePath $contentsChecksumsPath -Encoding ASCII -Force

Write-Section "Done"
Write-Host "  Publish dir  : $publishDir"
Write-Host "  Stage dir    : $stageDir"
Write-Host "  Bundle       : $bundleProfile"
Write-Host "  Archive      : $archivePath  (${archiveSizeMB} MB)"
Write-Host "  Checksums    : $checksumPath"
Write-Host "  Contents SHA : $contentsChecksumsPath"

exit 0
