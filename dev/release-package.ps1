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

Write-Section "Archive + Checksums"

if (Test-Path $archivePath) {
    Remove-Item $archivePath -Force
}
if (Test-Path $checksumPath) {
    Remove-Item $checksumPath -Force
}
if (Test-Path $binaryChecksumsPath) {
    Remove-Item $binaryChecksumsPath -Force
}

# Using a more robust zip method for PS 5.1
$sourcePath = $stageDir
if ($sourcePath -notmatch '\\$') { $sourcePath += '\' }
Compress-Archive -Path "$sourcePath*" -DestinationPath $archivePath -CompressionLevel Optimal -Force

$zipHash = Get-FileHash -Path $archivePath -Algorithm SHA256
"$($zipHash.Hash) *$archiveName" | Out-File -FilePath $checksumPath -Encoding ASCII -Force

$binaries = Get-ChildItem -Path $stageDir -File
$binaryLines = foreach ($file in $binaries) {
    $hash = Get-FileHash -Path $file.FullName -Algorithm SHA256
    "$($hash.Hash) *$($file.Name)"
}
$binaryLines | Out-File -FilePath $binaryChecksumsPath -Encoding ASCII -Force

Write-Section "Done"
Write-Host "  Publish dir : $publishDir"
Write-Host "  Stage dir   : $stageDir"
Write-Host "  Archive     : $archivePath"
Write-Host "  Checksums   : $checksumPath"
Write-Host "  Binary SHA  : $binaryChecksumsPath"

exit 0
