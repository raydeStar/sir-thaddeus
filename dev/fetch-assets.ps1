<#
.SYNOPSIS
    Downloads voice backend binary assets from GitHub Releases.
.DESCRIPTION
    Reads assets/manifest.json, downloads each asset pack from the configured
    GitHub Release URL, verifies SHA-256, and extracts to the target directory.
    Idempotent: skips assets that are already installed and verified.
.EXAMPLE
    .\dev\fetch-assets.ps1
    .\dev\fetch-assets.ps1 -AssetId stt-model-whisper-base
    .\dev\fetch-assets.ps1 -Force
#>
param(
    [string]$AssetId,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$manifestPath = Join-Path $repoRoot 'assets\manifest.json'

if (-not (Test-Path $manifestPath)) {
    Write-Error "Asset manifest not found: $manifestPath"
    exit 1
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$baseUrl = $manifest.baseUrl

function Get-FileSHA256($path) {
    (Get-FileHash $path -Algorithm SHA256).Hash.ToLower()
}

function Get-RequiredRelativePaths($assetId) {
    switch ($assetId) {
        'voice-runtime-win-x64' { return @('runtime\python\python.exe', 'bin\uv.exe') }
        'voice-deps-win-x64' { return @('faster_whisper-1.2.1-py3-none-any.whl') }
        'piper-win-x64' { return @('piper.exe') }
        'piper-voice-en_US-john-medium' { return @('en_US-john-medium.onnx', 'en_US-john-medium.onnx.json') }
        'stt-model-whisper-base' { return @('model.bin') }
        default { return @() }
    }
}

function Test-AssetPayload($asset, $extractDir) {
    $required = Get-RequiredRelativePaths $asset.id
    foreach ($relative in $required) {
        $candidate = Join-Path $extractDir $relative
        if (-not (Test-Path $candidate)) {
            return $false
        }
    }
    return $true
}

function Install-Asset($asset) {
    $extractDir = Join-Path $repoRoot ($asset.extractTo -replace '/', '\')
    $markerPath = Join-Path $extractDir '.installed.marker'

    if (-not $Force -and (Test-Path $markerPath)) {
        $existing = (Get-Content $markerPath -Raw).Trim()
        if ($existing -eq $asset.sha256 -and (Test-AssetPayload -asset $asset -extractDir $extractDir)) {
            Write-Host "  [SKIP] $($asset.id) -- already installed (sha256 matches)" -ForegroundColor DarkGray
            return
        }

        Write-Host "  [REPAIR] $($asset.id) marker present but payload invalid/stale; reinstalling..." -ForegroundColor Yellow
        Remove-Item -Force $markerPath -ErrorAction SilentlyContinue
    }

    $url = if ($asset.url) { $asset.url } else { "$baseUrl$($asset.filename)" }
    $sizeMB = [math]::Round($asset.sizeBytes / 1MB, 1)
    Write-Host "  [DOWNLOAD] $($asset.filename) ($sizeMB MB)" -ForegroundColor Cyan
    Write-Host "    URL: $url"

    $tempZip = Join-Path $env:TEMP "st-asset-$($asset.id)-$(New-Guid).zip"
    try {
        # Use .NET HttpClient for streaming download with progress
        $wc = New-Object System.Net.WebClient
        $wc.DownloadFile($url, $tempZip)

        Write-Host "  [VERIFY] Checking SHA-256..." -ForegroundColor Yellow
        $actualHash = Get-FileSHA256 $tempZip
        if ($actualHash -ne $asset.sha256) {
            Write-Error "SHA-256 mismatch for $($asset.filename): expected $($asset.sha256), got $actualHash"
            return
        }
        Write-Host "    SHA-256 OK" -ForegroundColor Green

        Write-Host "  [EXTRACT] -> $extractDir" -ForegroundColor Yellow
        New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
        Expand-Archive -Path $tempZip -DestinationPath $extractDir -Force

        if (-not (Test-AssetPayload -asset $asset -extractDir $extractDir)) {
            Write-Error "Asset payload validation failed for $($asset.id) at $extractDir"
            return
        }

        Set-Content -Path $markerPath -Value $asset.sha256
        Write-Host "  [OK] $($asset.id) installed" -ForegroundColor Green
    }
    finally {
        if (Test-Path $tempZip) { Remove-Item $tempZip -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ""
Write-Host "Sir Thaddeus Asset Fetcher" -ForegroundColor White
Write-Host "Manifest: $manifestPath"
Write-Host "Base URL: $baseUrl"
Write-Host ""

$assets = $manifest.assets
if ($AssetId) {
    $assets = $assets | Where-Object { $_.id -eq $AssetId }
    if (-not $assets) {
        Write-Error "Unknown asset ID: $AssetId. Available: $($manifest.assets.id -join ', ')"
        exit 1
    }
}

foreach ($asset in $assets) {
    Install-Asset $asset
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
