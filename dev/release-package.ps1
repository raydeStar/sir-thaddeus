#requires -Version 5.1

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [string]$Version = "",

    [switch]$SkipPreflight
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

# ── Verify bundled Piper TTS assets ──────────────────────────────────
# Piper assets are committed to the repo via Git LFS, so no download
# is needed. Just verify the critical files are present.

Write-Section "Verify Piper TTS Assets"

$voiceBackendDir = Join-Path $RepoRoot "apps/voice-backend"
$piperExe = Join-Path $voiceBackendDir "piper/piper.exe"
$piperVoiceModel = Join-Path $voiceBackendDir "piper-voices/en_US-john-medium/en_US-john-medium.onnx"

if (Test-Path $piperExe) {
    Write-Host "  piper.exe present"
}
else {
    Write-Host "  WARN: piper.exe not found at $piperExe" -ForegroundColor Yellow
}

if (Test-Path $piperVoiceModel) {
    $voiceSize = [math]::Round((Get-Item $piperVoiceModel).Length / 1MB, 1)
    Write-Host "  Default voice model present (${voiceSize} MB)"
}
else {
    Write-Host "  WARN: Default voice model not found at $piperVoiceModel" -ForegroundColor Yellow
}

Write-Section "Publish Artifacts"

$projects = @(
    "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj",
    "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj",
    "apps/desktop-runtime/SirThaddeus.DesktopRuntime/SirThaddeus.DesktopRuntime.csproj"
)

foreach ($project in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $projectPublishDir = Join-Path $RepoRoot "artifacts/publish/$projectName/$Runtime"
    
    if (Test-Path $projectPublishDir) {
        Remove-Item -Path $projectPublishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $projectPublishDir | Out-Null

    Write-Host "  Publishing $project to $projectPublishDir"
    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained $selfContainedValue `
        -o $projectPublishDir
    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet publish failed for $project (exit code $LASTEXITCODE)." $LASTEXITCODE
    }
}

# ── Structured staging ───────────────────────────────────────────────
#
#  ZIP root/
#   ├── SirThaddeus.DesktopRuntime.exe   ← user double-clicks this
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
$binDir = Join-Path $stageDir "bin"
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

foreach ($project in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $projectPublishDir = Join-Path $RepoRoot "artifacts/publish/$projectName/$Runtime"

    Write-Host "  Staging $projectName files..."
    Get-ChildItem -Path $projectPublishDir -Recurse | ForEach-Object {
        $relativePath = $_.FullName.Substring($projectPublishDir.Length).TrimStart('\')
        $isExe = ($_.Extension -eq ".exe" -and $relativePath -notmatch '\\')

        if ($isExe) {
            # Top-level EXEs land at the ZIP root
            $dest = Join-Path $stageDir $_.Name
        }
        elseif ($_.PSIsContainer) {
            # Recreate subdirectories under bin/
            $dest = Join-Path $binDir $relativePath
            New-Item -ItemType Directory -Path $dest -Force | Out-Null
            return
        }
        else {
            # Everything else (DLLs, assets, voice/) goes into bin/
            $dest = Join-Path $binDir $relativePath
            $destParent = Split-Path $dest -Parent
            if (-not (Test-Path $destParent)) {
                New-Item -ItemType Directory -Path $destParent -Force | Out-Null
            }
        }
        Copy-Item -Path $_.FullName -Destination $dest -Force -ErrorAction SilentlyContinue
    }
}

# ── Bundle vendored voice-backend assets (Git LFS) ──────────────────
# These are tracked via Git LFS and fetched during CI with lfs: true.
# They land under bin/voice/ alongside the Python scripts already
# staged by the VoiceHost dotnet publish output.

$voiceStageBinDir = Join-Path $binDir "voice"
if (-not (Test-Path $voiceStageBinDir)) {
    New-Item -ItemType Directory -Force -Path $voiceStageBinDir | Out-Null
}

# Piper TTS native binary (standalone exe + DLLs + espeak-ng-data)
$piperSource = Join-Path $voiceBackendDir "piper"
if (Test-Path $piperSource) {
    $piperDest = Join-Path $voiceStageBinDir "piper"
    Copy-Item -Path $piperSource -Destination $piperDest -Recurse -Force
    $piperCount = (Get-ChildItem -Path $piperDest -Recurse -File).Count
    Write-Host "  Staged: piper/ ($piperCount files)"
}
else {
    Write-Host "  WARN: piper/ directory not found; TTS will be unavailable" -ForegroundColor Yellow
}

# Piper voice models (default: en_US-john-medium)
$piperVoicesSource = Join-Path $voiceBackendDir "piper-voices"
if (Test-Path $piperVoicesSource) {
    $piperVoicesDest = Join-Path $voiceStageBinDir "piper-voices"
    Copy-Item -Path $piperVoicesSource -Destination $piperVoicesDest -Recurse -Force
    $voiceCount = (Get-ChildItem -Path $piperVoicesDest -Recurse -Filter "*.onnx").Count
    Write-Host "  Staged: piper-voices/ ($voiceCount voice model(s))"
}
else {
    Write-Host "  WARN: piper-voices/ directory not found; no bundled voices" -ForegroundColor Yellow
}

# uv.exe — Python environment manager
$uvSource = Join-Path $voiceBackendDir "bin/uv.exe"
if (Test-Path $uvSource) {
    $uvDest = Join-Path $voiceStageBinDir "bin"
    New-Item -ItemType Directory -Force -Path $uvDest | Out-Null
    Copy-Item -Path $uvSource -Destination (Join-Path $uvDest "uv.exe") -Force
    Write-Host "  Staged: bin/uv.exe"
}
else {
    Write-Host "  WARN: bundled uv.exe not found; offline venv creation may fail" -ForegroundColor Yellow
}

# Bundled Python 3.11 runtime
$runtimeSource = Join-Path $voiceBackendDir "runtime/python"
if (Test-Path $runtimeSource) {
    $runtimeDest = Join-Path $voiceStageBinDir "runtime/python"
    Copy-Item -Path $runtimeSource -Destination $runtimeDest -Recurse -Force
    $runtimeCount = (Get-ChildItem -Path $runtimeDest -Recurse -File).Count
    Write-Host "  Staged: runtime/python ($runtimeCount files)"
}
else {
    Write-Host "  WARN: bundled Python runtime not found; will download at first run" -ForegroundColor Yellow
}

# Python wheel dependencies (offline pip install)
$wheelsSource = Join-Path $voiceBackendDir "deps/wheels"
if ((Test-Path $wheelsSource) -and (Get-ChildItem -Path $wheelsSource -Filter "*.whl" | Measure-Object).Count -gt 0) {
    $wheelsDest = Join-Path $voiceStageBinDir "deps/wheels"
    New-Item -ItemType Directory -Force -Path $wheelsDest | Out-Null
    Copy-Item -Path (Join-Path $wheelsSource "*.whl") -Destination $wheelsDest -Force
    $wheelCount = (Get-ChildItem -Path $wheelsDest -Filter "*.whl").Count
    $wheelSizeMB = [math]::Round(((Get-ChildItem -Path $wheelsDest -Filter "*.whl" | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    Write-Host "  Staged: deps/wheels ($wheelCount wheels, ${wheelSizeMB} MB)"
}
else {
    Write-Host "  WARN: bundled Python wheels not found; will download at first run" -ForegroundColor Yellow
}

# Faster-Whisper base STT model
$sttSource = Join-Path $voiceBackendDir "stt-models/base"
if (Test-Path $sttSource) {
    $sttDest = Join-Path $voiceStageBinDir "stt-models/base"
    Copy-Item -Path $sttSource -Destination $sttDest -Recurse -Force
    Write-Host "  Staged: stt-models/base"
}
else {
    Write-Host "  WARN: bundled STT model not found; will download at first run" -ForegroundColor Yellow
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
$pdbFiles = Get-ChildItem -Path $stageDir -File -Recurse -Filter "*.pdb"
if ($pdbFiles.Count -gt 0) {
    foreach ($pdb in $pdbFiles) {
        Remove-Item -Path $pdb.FullName -Force
    }
    Write-Host "  Removed debug symbols: $($pdbFiles.Count)"
}

Write-Section "Archive + Checksums (Full Bundle)"

foreach ($p in @($archivePath, $checksumPath, $binaryChecksumsPath)) {
    if (Test-Path $p) { Remove-Item $p -Force }
}

# ── Full bundle zip (includes all vendored voice-backend assets) ─────
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
