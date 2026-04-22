#requires -Version 5.1
<#
.SYNOPSIS
    Builds and publishes the Sir Thaddeus hybrid runtime as a self-contained
    single-file binary for one or more runtime identifiers.

.DESCRIPTION
    Produces self-contained publishes under artifacts/publish/<rid>/. Each
    output directory contains a single Thaddeus.Runtime executable plus the
    embedded wwwroot bundle. The web bundle must already be built and copied
    to src/Thaddeus.Runtime/wwwroot/ before invoking this script (run
    `web/build.ps1` or the Phase 8.3 release pipeline).

.PARAMETER Rids
    One or more runtime identifiers to publish for. Defaults to win-x64.
    Common values: win-x64, win-arm64, osx-arm64, osx-x64, linux-x64.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    pwsh dev/package-runtime.ps1 -Rids win-x64,osx-arm64
#>
[CmdletBinding()]
param(
    [string[]]$Rids = @('win-x64'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeProj = Join-Path $repoRoot 'src/Thaddeus.Runtime/Thaddeus.Runtime.csproj'
$wwwroot = Join-Path $repoRoot 'src/Thaddeus.Runtime/wwwroot/index.html'

if (-not (Test-Path $wwwroot)) {
    Write-Warning "wwwroot/index.html missing. The runtime will boot but will serve a 404 for /."
    Write-Warning "Run the web build first: cd web; npm run build; then sync dist/ -> src/Thaddeus.Runtime/wwwroot/."
}

$publishRoot = Join-Path $repoRoot 'artifacts/publish'
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

foreach ($rid in $Rids) {
    $outDir = Join-Path $publishRoot $rid
    Write-Host "==> Publishing $rid -> $outDir" -ForegroundColor Cyan

    & dotnet publish $runtimeProj `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=embedded `
        -p:EnableCompressionInSingleFile=true `
        -o $outDir `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid (exit $LASTEXITCODE)"
    }
}

Write-Host "All publishes complete." -ForegroundColor Green
Write-Host "Outputs:" -ForegroundColor Green
Get-ChildItem $publishRoot -Directory | ForEach-Object { Write-Host "  $($_.FullName)" }
