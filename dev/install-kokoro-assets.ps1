<#
.SYNOPSIS
    Installs or refreshes a Kokoro voice pack manifest for voice-backend.

.DESCRIPTION
    Copies voice artifacts into apps/voice-backend/voices/<voiceId> (optional)
    and writes manifest.json with SHA-256 hashes. The backend health probe
    uses this manifest for deterministic install checks.

.PARAMETER VoiceId
    Target Kokoro voice id (for example: af_sky).

.PARAMETER SourceDir
    Optional source directory containing Kokoro artifacts to copy.
    If omitted, the script only regenerates manifest.json from existing files.

.PARAMETER AllowUnsafeArtifacts
    Allows .pt/.pth files. Disabled by default for safety.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$VoiceId,

    [string]$SourceDir = "",

    [switch]$AllowUnsafeArtifacts
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$voiceRoot = Join-Path $repoRoot "apps\voice-backend\voices\$VoiceId"

$allowedExtensions = @(".onnx", ".json", ".txt", ".bin", ".safetensors", ".npy", ".wav")
$blockedExtensions = @(".pt", ".pth")

if (-not (Test-Path $voiceRoot)) {
    New-Item -ItemType Directory -Path $voiceRoot -Force | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($SourceDir)) {
    if (-not (Test-Path $SourceDir)) {
        Write-Host "ERROR: SourceDir not found: $SourceDir" -ForegroundColor Red
        exit 1
    }

    Write-Host "Copying voice assets from $SourceDir..." -ForegroundColor Yellow
    Copy-Item -Path (Join-Path $SourceDir "*") -Destination $voiceRoot -Recurse -Force
}

$files = Get-ChildItem -Path $voiceRoot -Recurse -File |
    Where-Object { $_.Name -ne "manifest.json" }

if ($files.Count -eq 0) {
    Write-Host "ERROR: No files found under $voiceRoot" -ForegroundColor Red
    exit 1
}

$manifestFiles = @()
$voiceRootResolved = (Resolve-Path $voiceRoot).Path.TrimEnd("\")
$voiceRootPrefix = "$voiceRootResolved\"

foreach ($file in $files) {
    $ext = $file.Extension.ToLowerInvariant()
    if ($blockedExtensions -contains $ext -and -not $AllowUnsafeArtifacts) {
        Write-Host "ERROR: Blocked artifact type '$ext' in $($file.FullName)." -ForegroundColor Red
        Write-Host "       Re-run with -AllowUnsafeArtifacts only if you trust this pack." -ForegroundColor Yellow
        exit 1
    }

    if (-not ($allowedExtensions -contains $ext) -and -not ($AllowUnsafeArtifacts -and ($blockedExtensions -contains $ext))) {
        Write-Host "ERROR: Unsupported artifact extension '$ext' in $($file.FullName)." -ForegroundColor Red
        exit 1
    }

    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.FullName.StartsWith($voiceRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relativePath = $file.FullName.Substring($voiceRootPrefix.Length).Replace("\", "/")
    }
    else {
        $relativePath = $file.Name
    }

    $manifestFiles += [ordered]@{
        path = $relativePath
        sha256 = $hash
    }
}

$manifest = [ordered]@{
    voiceId = $VoiceId
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    files = $manifestFiles
}

$manifestPath = Join-Path $voiceRoot "manifest.json"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host ""
Write-Host "Kokoro voice manifest written:" -ForegroundColor Green
Write-Host "  $manifestPath" -ForegroundColor Green
Write-Host "  Files: $($manifestFiles.Count)" -ForegroundColor Green
Write-Host ""
Write-Host "Next step: set settings.voice.ttsEngine='kokoro' and settings.voice.ttsVoiceId='$VoiceId'." -ForegroundColor Cyan
