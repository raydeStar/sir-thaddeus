#requires -Version 5.1

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

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

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$publishDir = Join-Path $RepoRoot "artifacts/publish/$Runtime"
$releaseDir = Join-Path $RepoRoot "artifacts/release"
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

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

Write-Section "Archive + Checksums"

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$archiveName = "sir-thaddeus-$Runtime-$stamp.zip"
$archivePath = Join-Path $releaseDir $archiveName
$checksumPath = "$archivePath.sha256.txt"

if (Test-Path $archivePath) {
    Remove-Item $archivePath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $archivePath -CompressionLevel Optimal -Force

$zipHash = Get-FileHash -Path $archivePath -Algorithm SHA256
"$($zipHash.Hash) *$archiveName" | Out-File -FilePath $checksumPath -Encoding ASCII -Force

$binaryChecksumsPath = Join-Path $releaseDir "sir-thaddeus-$Runtime-$stamp-binaries.sha256.txt"
$binaries = Get-ChildItem -Path $publishDir -File
$binaryLines = foreach ($file in $binaries) {
    $hash = Get-FileHash -Path $file.FullName -Algorithm SHA256
    "$($hash.Hash) *$($file.Name)"
}
$binaryLines | Out-File -FilePath $binaryChecksumsPath -Encoding ASCII -Force

Write-Section "Done"
Write-Host "  Publish dir : $publishDir"
Write-Host "  Archive     : $archivePath"
Write-Host "  Checksums   : $checksumPath"
Write-Host "  Binary SHA  : $binaryChecksumsPath"

exit 0
