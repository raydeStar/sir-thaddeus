#requires -Version 5.1

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [string]$Version = "",

    [switch]$SkipPreflight,

    [switch]$FullBundle
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
$versionLabel = Get-VersionLabel $Version
$archiveToken = if ([string]::IsNullOrWhiteSpace($versionLabel)) {
    Get-Date -Format "yyyyMMdd-HHmmss"
}
else {
    $versionLabel
}

$archiveStem = "sir-thaddeus-$Runtime-$archiveToken"
$archiveName = "$archiveStem.zip"
$archivePath = Join-Path $releaseDir $archiveName
$checksumPath = "$archivePath.sha256.txt"
$binaryChecksumsPath = Join-Path $releaseDir "$archiveStem-binaries.sha256.txt"

$firstRunReadmeSource = Join-Path $RepoRoot "README_FIRST_RUN.md"
$settingsTemplateSource = Join-Path $RepoRoot "SirThaddeus.Settings.template.json"

Write-Section "Package Settings"
Write-Host "  Configuration : $Configuration"
Write-Host "  Runtime       : $Runtime"
Write-Host "  SelfContained : $effectiveSelfContained"
Write-Host "  Bundle profile: $(if ($FullBundle) { 'full' } else { 'lite' })"
if ([string]::IsNullOrWhiteSpace($versionLabel)) {
    Write-Host "  Version       : <timestamp>"
}
else {
    Write-Host "  Version       : $versionLabel"
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

$includeOptionalBundledAssets = $FullBundle.IsPresent
$strictVoiceAssetGate = ($Configuration -eq "Release") -and $includeOptionalBundledAssets

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
    "apps/ui-avalonia/SirThaddeus.UI.Avalonia/SirThaddeus.UI.Avalonia.csproj"
)

$projectFrameworkOverrides = @{
    "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj" = @{
        default = "net10.0"
        windows = "net10.0-windows10.0.19041.0"
    }
    "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj" = @{
        default = "net10.0"
    }
    "apps/ui-avalonia/SirThaddeus.UI.Avalonia/SirThaddeus.UI.Avalonia.csproj" = @{
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
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $selfContainedValue,
        "-o", $projectPublishDir
    )

    if (-not [string]::IsNullOrWhiteSpace($targetFramework)) {
        $publishArgs += @("-f", $targetFramework)
    }

    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet publish failed for $project (exit code $LASTEXITCODE)." $LASTEXITCODE
    }
}

# -- Structured staging --------------------------------------------------------
#
#  ZIP root/
#   ├── SirThaddeus.UI.Avalonia.exe      ← user double-clicks this
#   ├── SirThaddeus.McpServer.exe
#   ├── SirThaddeus.VoiceHost.exe
#   ├── README_FIRST_RUN.md
#   └── bin/                             ← support files (DLLs, assets, voice/)
#

Write-Section "Stage Artifacts"

if (Test-Path $stageDir) {
    Remove-Item -Path $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

foreach ($project in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $projectPublishDir = Join-Path $RepoRoot "artifacts/publish/$projectName/$Runtime"

    Write-Host "  Staging $projectName files..."
    @(Get-ChildItem -Path $projectPublishDir -Recurse) | ForEach-Object {
        $relativePath = $_.FullName.Substring($projectPublishDir.Length).TrimStart('\')
        $dest = Join-Path $stageDir $relativePath

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

Write-Section "Archive + Checksums"

foreach ($p in @($archivePath, $checksumPath, $binaryChecksumsPath)) {
    if (Test-Path $p) { Remove-Item $p -Force }
}

# ── Package zip (lite by default; use -FullBundle for vendored assets) ─────
$sourcePath = $stageDir
if ($sourcePath -notmatch '\\$') { $sourcePath += '\' }
Compress-Archive -Path "$sourcePath*" -DestinationPath $archivePath -CompressionLevel Optimal -Force

$zipHash = Get-FileHash -Path $archivePath -Algorithm SHA256
"$($zipHash.Hash) *$archiveName" | Out-File -FilePath $checksumPath -Encoding ASCII -Force

$fullSizeMB = [math]::Round((Get-Item $archivePath).Length / 1MB, 1)
Write-Host "  Full archive: $archiveName (${fullSizeMB} MB)"

$binaries = Get-ChildItem -Path $stageDir -File
$binaryLines = foreach ($file in $binaries) {
    $hash = Get-FileHash -Path $file.FullName -Algorithm SHA256
    "$($hash.Hash) *$($file.Name)"
}
$binaryLines | Out-File -FilePath $binaryChecksumsPath -Encoding ASCII -Force

Write-Section "Done"
Write-Host "  Publish dir  : $publishDir"
Write-Host "  Stage dir    : $stageDir"
Write-Host "  Full archive : $archivePath  (${fullSizeMB} MB)"
Write-Host "  Checksums    : $checksumPath"
Write-Host "  Binary SHA   : $binaryChecksumsPath"

exit 0
