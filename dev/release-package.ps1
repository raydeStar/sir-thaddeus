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

$liteStem = "sir-thaddeus-$Runtime-$archiveToken-lite"
$liteName = "$liteStem.zip"
$litePath = Join-Path $releaseDir $liteName
$liteChecksumPath = "$litePath.sha256.txt"
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

# ── Prefetch default Kokoro TTS voice assets ──────────────────────────
# These are gitignored (~337 MB) but required in the release package so
# fresh target machines don't need a large first-run download.
# URLs are read from model_registry.json to stay in sync.

Write-Section "Prefetch Voice Assets"

$voiceBackendDir = Join-Path $RepoRoot "apps/voice-backend"
$voicesDir = Join-Path $voiceBackendDir "voices/bm_lewis"
$registryPath = Join-Path $voiceBackendDir "model_registry.json"
$modelPath = Join-Path $voicesDir "model.onnx"
$voicesBinPath = Join-Path $voicesDir "voices.bin"
$manifestPath = Join-Path $voicesDir "manifest.json"

if ((Test-Path $modelPath) -and (Test-Path $voicesBinPath)) {
    Write-Host "  Kokoro bm_lewis assets already present — skipping download."
}
elseif (-not (Test-Path $registryPath)) {
    Write-Host "  WARN: model_registry.json not found; skipping voice asset prefetch." -ForegroundColor Yellow
}
else {
    $registry = Get-Content $registryPath -Raw | ConvertFrom-Json
    $kokoroFiles = $registry.kokoro.'v1.0'.files

    if (-not (Test-Path $voicesDir)) {
        New-Item -ItemType Directory -Force -Path $voicesDir | Out-Null
    }

    foreach ($file in $kokoroFiles) {
        $destPath = Join-Path $voicesDir $file.localName
        if (Test-Path $destPath) {
            Write-Host "  Already exists: $($file.localName)"
            continue
        }

        Write-Host "  Downloading $($file.localName) ($([math]::Round($file.sizeBytes / 1MB, 1)) MB)..."
        $tmpPath = "$destPath.tmp"
        try {
            Invoke-WebRequest -Uri $file.url -OutFile $tmpPath -UseBasicParsing
            Move-Item -Path $tmpPath -Destination $destPath -Force
            Write-Host "  OK: $($file.localName)"
        }
        catch {
            if (Test-Path $tmpPath) { Remove-Item $tmpPath -Force -ErrorAction SilentlyContinue }
            Write-Host "  WARN: Failed to download $($file.localName): $_" -ForegroundColor Yellow
        }
    }

    # Write manifest so the startup script recognizes the bundle as valid.
    if ((Test-Path $modelPath) -and (Test-Path $voicesBinPath) -and -not (Test-Path $manifestPath)) {
        $manifest = @{
            voiceId = "bm_lewis"
            generatedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
            autoDownloaded = $true
            files = @(
                @{ path = "model.onnx"; sha256 = (Get-FileHash $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() },
                @{ path = "voices.bin"; sha256 = (Get-FileHash $voicesBinPath -Algorithm SHA256).Hash.ToLowerInvariant() }
            )
        }
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8
        Write-Host "  Created manifest.json for bm_lewis"
    }
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

foreach ($p in @($archivePath, $checksumPath, $binaryChecksumsPath, $litePath, $liteChecksumPath)) {
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

# ── Lite zip (strips heavy bundled assets; downloads them on first run) ──
Write-Section "Archive + Checksums (Lite)"

$voiceStageDir = Join-Path $binDir "voice"
$heavyAssetDirs = @(
    (Join-Path $voiceStageDir "runtime"),
    (Join-Path $voiceStageDir "deps"),
    (Join-Path $voiceStageDir "stt-models"),
    (Join-Path $voiceStageDir "bin")
)
$heavyAssetFiles = @(
    (Join-Path $voiceStageDir "voices/bm_lewis/model.onnx"),
    (Join-Path $voiceStageDir "voices/bm_lewis/voices.bin")
)

# Temporarily move heavy assets out of the stage tree
$tempHoldDir = Join-Path $RepoRoot "artifacts/stage-hold"
if (Test-Path $tempHoldDir) { Remove-Item $tempHoldDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tempHoldDir | Out-Null

$movedItems = @()
foreach ($dir in $heavyAssetDirs) {
    if (Test-Path $dir) {
        $holdDest = Join-Path $tempHoldDir (Split-Path $dir -Leaf)
        Move-Item -Path $dir -Destination $holdDest -Force
        $movedItems += @{ Source = $dir; Hold = $holdDest }
        Write-Host "  Lite: excluded $(Split-Path $dir -Leaf)/"
    }
}
foreach ($file in $heavyAssetFiles) {
    if (Test-Path $file) {
        $holdDest = Join-Path $tempHoldDir (Split-Path $file -Leaf)
        Move-Item -Path $file -Destination $holdDest -Force
        $movedItems += @{ Source = $file; Hold = $holdDest }
        Write-Host "  Lite: excluded $(Split-Path $file -Leaf)"
    }
}

Compress-Archive -Path "$sourcePath*" -DestinationPath $litePath -CompressionLevel Optimal -Force

$liteHash = Get-FileHash -Path $litePath -Algorithm SHA256
"$($liteHash.Hash) *$liteName" | Out-File -FilePath $liteChecksumPath -Encoding ASCII -Force

$liteSizeMB = [math]::Round((Get-Item $litePath).Length / 1MB, 1)
Write-Host "  Lite archive: $liteName (${liteSizeMB} MB)"

# Restore heavy assets back into stage tree (for local inspection)
foreach ($item in $movedItems) {
    $destParent = Split-Path $item.Source -Parent
    if (-not (Test-Path $destParent)) {
        New-Item -ItemType Directory -Force -Path $destParent | Out-Null
    }
    Move-Item -Path $item.Hold -Destination $item.Source -Force
}
Remove-Item $tempHoldDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Section "Done"
Write-Host "  Publish dir  : $publishDir"
Write-Host "  Stage dir    : $stageDir"
Write-Host "  Full archive : $archivePath  (${fullSizeMB} MB)"
Write-Host "  Lite archive : $litePath  (${liteSizeMB} MB)"
Write-Host "  Checksums    : $checksumPath"
Write-Host "  Binary SHA   : $binaryChecksumsPath"

exit 0
