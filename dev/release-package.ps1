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

Write-Section "Publish Artifacts"

$projects = @(
    "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj",
    "apps/voice-host/SirThaddeus.VoiceHost/SirThaddeus.VoiceHost.csproj",
    "apps/desktop-runtime/SirThaddeus.DesktopRuntime/SirThaddeus.DesktopRuntime.csproj"
)

foreach ($project in $projects) {
    Write-Host "  Publishing $project"
    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained $selfContainedValue `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet publish failed for $project (exit code $LASTEXITCODE)." $LASTEXITCODE
    }
}

Write-Section "Stage Package Contents"
if (Test-Path $stageDir) {
    Remove-Item -Path $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Copy-Item -Path (Join-Path $publishDir "*") -Destination $stageDir -Recurse -Force

if (-not (Test-Path $firstRunReadmeSource)) {
    Fail "required file is missing: $firstRunReadmeSource"
}

Copy-Item -Path $firstRunReadmeSource -Destination (Join-Path $stageDir "README_FIRST_RUN.md") -Force

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

Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $archivePath -CompressionLevel Optimal -Force

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
