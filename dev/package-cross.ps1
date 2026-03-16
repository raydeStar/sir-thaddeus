#!/usr/bin/env pwsh
#requires -Version 7.0

<#
.SYNOPSIS
    Builds a self-contained release package for Linux or macOS.

.DESCRIPTION
    Publishes the headless runtime, Avalonia UI, and MCP server for the
    target RID, stages them, adds launcher scripts, and produces a
    .tar.gz archive + SHA-256 checksum.

    Run this script natively on the target OS (ubuntu runner for
    linux-x64, macos runner for osx-arm64) via 'pwsh'.

.PARAMETER Runtime
    Target RID: linux-x64, osx-x64, or osx-arm64.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER SelfContained
    Publish self-contained (bundled .NET runtime, no prereqs on target).
    Default: true in Release mode.

.PARAMETER Version
    Version label for archive naming. Tags ('refs/tags/v1.2.0') are
    unwrapped automatically. Falls back to a UTC timestamp.

.EXAMPLE
    pwsh ./dev/package-cross.ps1 -Runtime linux-x64 -Version v1.2.0
    pwsh ./dev/package-cross.ps1 -Runtime osx-arm64 -Version v1.2.0
#>

param(
    [ValidateSet("linux-x64", "osx-x64", "osx-arm64")]
    [string]$Runtime = "linux-x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SelfContained,

    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$IsWindows_Host = $PSVersionTable.Platform -eq 'Win32NT' -or ($null -eq $PSVersionTable.Platform)

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
    if ([string]::IsNullOrWhiteSpace($RawVersion)) { return "" }
    $v = $RawVersion.Trim()
    if ($v.StartsWith("refs/tags/", [System.StringComparison]::OrdinalIgnoreCase)) {
        $v = $v.Substring("refs/tags/".Length)
    }
    return ($v -replace '[^0-9A-Za-z\.\-_]', '-')
}

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$effectiveSelfContained = if ($PSBoundParameters.ContainsKey("SelfContained")) {
    $SelfContained.IsPresent
} elseif ($Configuration -eq "Release") {
    $true
} else {
    $false
}
$selfContainedValue = if ($effectiveSelfContained) { "true" } else { "false" }

$versionLabel = Get-VersionLabel $Version
$archiveToken = if ([string]::IsNullOrWhiteSpace($versionLabel)) {
    (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
} else {
    $versionLabel
}

$archiveStem     = "sir-thaddeus-$Runtime-$archiveToken"
$archiveName     = "$archiveStem.tar.gz"
$checksumName    = "$archiveName.sha256.txt"
$releaseDir      = Join-Path $RepoRoot "artifacts/release"
$stageDir        = Join-Path $RepoRoot "artifacts/stage/$Runtime"
$archivePath     = Join-Path $releaseDir $archiveName
$checksumPath    = Join-Path $releaseDir $checksumName

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

if (Test-Path $stageDir) {
    Remove-Item -Path $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Write-Section "Package Settings"
Write-Host "  Runtime       : $Runtime"
Write-Host "  Configuration : $Configuration"
Write-Host "  SelfContained : $effectiveSelfContained"
Write-Host "  Version       : $(if ($versionLabel) { $versionLabel } else { '<timestamp>' })"
Write-Host "  Stage dir     : $stageDir"
Write-Host "  Archive       : $archivePath"

# ─── Projects to publish ──────────────────────────────────────────────────────

$projects = [ordered]@{
    "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj"                   = ""
    "apps/headless-runtime/SirThaddeus.HeadlessRuntime/SirThaddeus.HeadlessRuntime.csproj" = "headless"
    "apps/ui-avalonia/SirThaddeus.UI.Avalonia/SirThaddeus.UI.Avalonia.csproj"               = ""
}

# ─── Publish ──────────────────────────────────────────────────────────────────

Write-Section "Publish"

foreach ($kvp in $projects.GetEnumerator()) {
    $project    = $kvp.Key
    $projName   = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $publishDir = Join-Path $RepoRoot "artifacts/publish/$projName/$Runtime"

    if (Test-Path $publishDir) {
        Remove-Item -Path $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    Write-Host "  Publishing $projName ..."

    $publishArgs = @(
        "publish", (Join-Path $RepoRoot $project),
        "-m:1",
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $selfContainedValue,
        "-f", "net10.0",
        "-o", $publishDir
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet publish failed for $projName (exit code $LASTEXITCODE)." $LASTEXITCODE
    }
}

# ─── Stage ────────────────────────────────────────────────────────────────────

Write-Section "Stage"

foreach ($kvp in $projects.GetEnumerator()) {
    $project    = $kvp.Key
    $projName   = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $subDir     = $kvp.Value
    $publishDir = Join-Path $RepoRoot "artifacts/publish/$projName/$Runtime"
    $destRoot   = if ([string]::IsNullOrWhiteSpace($subDir)) { $stageDir } else { Join-Path $stageDir $subDir }

    Write-Host "  Staging $projName$(if ($subDir) { " -> $subDir/" })..."

    New-Item -ItemType Directory -Force -Path $destRoot | Out-Null

    Get-ChildItem -Path $publishDir -Recurse | ForEach-Object {
        $relativePath = $_.FullName.Substring($publishDir.Length).TrimStart('/').TrimStart('\')
        $dest = Join-Path $destRoot $relativePath
        if ($_.PSIsContainer) {
            New-Item -ItemType Directory -Force -Path $dest | Out-Null
        } else {
            $destParent = Split-Path $dest -Parent
            if (-not (Test-Path $destParent)) {
                New-Item -ItemType Directory -Force -Path $destParent | Out-Null
            }
            Copy-Item -Path $_.FullName -Destination $dest -Force
        }
    }
}

# ─── Copy ancillary files ─────────────────────────────────────────────────────

$filesToCopy = @(
    @{ Source = "README_FIRST_RUN.md";                  Required = $true  }
    @{ Source = "DISCLAIMER.md";                         Required = $false }
    @{ Source = "SirThaddeus.Settings.template.json";   Required = $false }
)

foreach ($entry in $filesToCopy) {
    $srcPath = Join-Path $RepoRoot $entry.Source
    if (Test-Path $srcPath) {
        Copy-Item -Path $srcPath -Destination (Join-Path $stageDir $entry.Source) -Force
        Write-Host "  Staged: $($entry.Source)"
    } elseif ($entry.Required) {
        Fail "Required file missing: $($entry.Source)"
    } else {
        Write-Host "  WARN: optional file not found: $($entry.Source)" -ForegroundColor Yellow
    }
}

# Assets manifest (self-heal for runtime downloads)
$manifestSrc = Join-Path $RepoRoot "assets/manifest.json"
if (Test-Path $manifestSrc) {
    $manifestDest = Join-Path $stageDir "assets"
    New-Item -ItemType Directory -Force -Path $manifestDest | Out-Null
    Copy-Item -Path $manifestSrc -Destination (Join-Path $manifestDest "manifest.json") -Force
    Write-Host "  Staged: assets/manifest.json"
}

# ─── Launcher scripts ─────────────────────────────────────────────────────────

Write-Section "Launcher Scripts"

$targetLinux = $Runtime -in @("linux-x64")
$targetMacOS = $Runtime -in @("osx-x64", "osx-arm64")
$uiBinary = "SirThaddeus.UI.Avalonia"  # no .exe on Linux/macOS

if ($targetLinux) {
    $launchContent = (@'
#!/usr/bin/env bash
# Sir Thaddeus — Linux launcher
# Ensures execute permissions and starts the Avalonia UI.
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")"; pwd)"
chmod +x "$SCRIPT_DIR/{{UI}}" 2>/dev/null || true
chmod +x "$SCRIPT_DIR/headless/SirThaddeus.HeadlessRuntime" 2>/dev/null || true
chmod +x "$SCRIPT_DIR/SirThaddeus.McpServer" 2>/dev/null || true
exec "$SCRIPT_DIR/{{UI}}" "$@"
'@).Replace('{{UI}}', $uiBinary)
    $launchPath = Join-Path $stageDir "launch.sh"
    Set-Content -Path $launchPath -Value $launchContent -Encoding UTF8 -NoNewline
    Write-Host "  Created: launch.sh"

    # Set exec bit on Linux (only on native runner, not on Windows cross-compile)
    if (-not $IsWindows_Host) {
        chmod +x $launchPath 2>/dev/null
    }
}

if ($targetMacOS) {
    $launchContent = (@'
#!/usr/bin/env bash
# Sir Thaddeus — macOS launcher
# Double-click this .command file in Finder to open Sir Thaddeus.
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")"; pwd)"
chmod +x "$SCRIPT_DIR/{{UI}}" 2>/dev/null || true
chmod +x "$SCRIPT_DIR/headless/SirThaddeus.HeadlessRuntime" 2>/dev/null || true
chmod +x "$SCRIPT_DIR/SirThaddeus.McpServer" 2>/dev/null || true
exec "$SCRIPT_DIR/{{UI}}" "$@"
'@).Replace('{{UI}}', $uiBinary)
    $launchPath = Join-Path $stageDir "launch.command"
    Set-Content -Path $launchPath -Value $launchContent -Encoding UTF8 -NoNewline
    Write-Host "  Created: launch.command"

    if (-not $IsWindows_Host) {
        chmod +x $launchPath 2>/dev/null
    }
}

# ─── Set executable bits for published binaries (native only) ─────────────────

if (-not $IsWindows_Host) {
    Write-Section "Set Executable Bits"

    $execTargets = @(
        (Join-Path $stageDir $uiBinary),
        (Join-Path $stageDir "headless/SirThaddeus.HeadlessRuntime"),
        (Join-Path $stageDir "SirThaddeus.McpServer")
    )

    foreach ($target in $execTargets) {
        if (Test-Path $target) {
            chmod +x $target 2>/dev/null
            Write-Host "  chmod +x $(Split-Path $target -Leaf)"
        }
    }
}

# ─── Strip debug symbols ──────────────────────────────────────────────────────

$pdbFiles = @(Get-ChildItem -Path $stageDir -Recurse -Filter "*.pdb" -File)
if ($pdbFiles.Count -gt 0) {
    foreach ($pdb in $pdbFiles) { Remove-Item -Path $pdb.FullName -Force }
    Write-Host "  Stripped: $($pdbFiles.Count) .pdb files"
}

# ─── Package structure validation ─────────────────────────────────────────────

Write-Section "Validate Package"

$requiredEntries = @(
    $uiBinary,
    "SirThaddeus.McpServer",
    "headless/SirThaddeus.HeadlessRuntime",
    "README_FIRST_RUN.md"
)

$structureOk = $true
foreach ($entry in $requiredEntries) {
    $fullPath = Join-Path $stageDir ($entry -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $fullPath)) {
        Write-Host "  MISSING: $entry" -ForegroundColor Red
        $structureOk = $false
    } else {
        Write-Host "  OK:      $entry"
    }
}
if (-not $structureOk) {
    Fail "Package structure validation failed."
}

# ─── Archive ──────────────────────────────────────────────────────────────────

Write-Section "Archive"

foreach ($p in @($archivePath, $checksumPath)) {
    if (Test-Path $p) { Remove-Item $p -Force }
}

if ($IsWindows_Host) {
    # Fallback: zip on Windows (exec bits not preserved; document limitation)
    $zipPath = $archivePath -replace '\.tar\.gz$', '.zip'
    $archivePath  = $zipPath
    $checksumPath = "$zipPath.sha256.txt"
    $archiveName  = [IO.Path]::GetFileName($zipPath)
    Write-Host "  NOTE: running on Windows, producing .zip instead of .tar.gz" -ForegroundColor Yellow
    $stageSource = $stageDir
    if ($stageSource -notmatch '[/\\]$') { $stageSource += [IO.Path]::DirectorySeparatorChar }
    Compress-Archive -Path "$stageSource*" -DestinationPath $archivePath -CompressionLevel Optimal -Force
} else {
    # tar.gz preserves execute bits set above
    $archiveNameInTar = $archiveStem
    Push-Location (Split-Path $stageDir -Parent)
    try {
        $stageDirLeaf = Split-Path $stageDir -Leaf
        tar -czf $archivePath --transform "s|^$stageDirLeaf|$archiveNameInTar|" $stageDirLeaf
        if ($LASTEXITCODE -ne 0) {
            Fail "tar failed (exit code $LASTEXITCODE)." $LASTEXITCODE
        }
    } finally {
        Pop-Location
    }
}

# SHA-256 checksum
if ($IsWindows_Host) {
    $zipHash = Get-FileHash -Path $archivePath -Algorithm SHA256
    "$($zipHash.Hash) *$archiveName" | Out-File -FilePath $checksumPath -Encoding ASCII -Force
} else {
    $rawHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLower()
    "$rawHash  $archiveName" | Out-File -FilePath $checksumPath -Encoding ASCII -NoNewline
}

$sizeMB = [math]::Round((Get-Item $archivePath).Length / 1MB, 1)

Write-Section "Done"
Write-Host "  Runtime    : $Runtime"
Write-Host "  Stage dir  : $stageDir"
Write-Host "  Archive    : $archivePath  (${sizeMB} MB)"
Write-Host "  Checksum   : $checksumPath"
exit 0
