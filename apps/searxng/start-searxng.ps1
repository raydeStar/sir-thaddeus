#requires -Version 5.1

param(
    [string]$BindHost = "127.0.0.1",
    [int]$Port = 8080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-SidecarRoot {
    return (Resolve-Path $PSScriptRoot).Path
}

function Resolve-PythonPath([string]$SidecarRoot) {
    $candidates = @(
        (Join-Path $SidecarRoot "runtime\python\python.exe"),
        (Join-Path $SidecarRoot "..\voice\runtime\python\python.exe"),
        (Join-Path $SidecarRoot "..\..\voice-backend\runtime\python\python.exe")
    )

    foreach ($candidate in $candidates) {
        $resolved = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path $resolved) {
            return $resolved
        }
    }

    throw "Could not find a bundled Python runtime for the SearXNG sidecar."
}

function Get-OrCreateSecret([string]$SecretPath) {
    if (Test-Path $SecretPath) {
        $existing = (Get-Content $SecretPath -Raw).Trim()
        if (-not [string]::IsNullOrWhiteSpace($existing)) {
            return $existing
        }
    }

    $secret = (([guid]::NewGuid().ToString("N")) + ([guid]::NewGuid().ToString("N")))
    New-Item -ItemType Directory -Force -Path (Split-Path $SecretPath -Parent) | Out-Null
    Set-Content -Path $SecretPath -Value $secret -Encoding ASCII
    return $secret
}

function Resolve-DataRoot([string]$SidecarRoot) {
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        return (Join-Path $env:LOCALAPPDATA "SirThaddeus\search")
    }

    return (Join-Path $SidecarRoot ".localstate")
}

$sidecarRoot = Resolve-SidecarRoot
$pythonExe = Resolve-PythonPath -SidecarRoot $sidecarRoot
$sitePackages = Join-Path $sidecarRoot "deps\site-packages"
$sourceRoot = Join-Path $sidecarRoot "source\searxng-upstream"
$templatePath = Join-Path $sidecarRoot "settings.template.yml"

if (-not (Test-Path $sitePackages)) {
    throw "Missing Python dependencies at $sitePackages"
}

if (-not (Test-Path (Join-Path $sourceRoot "searx\webapp.py"))) {
    throw "Missing SearXNG source payload at $sourceRoot"
}

if (-not (Test-Path $templatePath)) {
    throw "Missing SearXNG settings template at $templatePath"
}

$appDataRoot = Resolve-DataRoot -SidecarRoot $sidecarRoot
$settingsPath = Join-Path $appDataRoot "settings.yml"
$secretPath = Join-Path $appDataRoot "secret.txt"
$secret = Get-OrCreateSecret -SecretPath $secretPath

$settingsTemplate = Get-Content $templatePath -Raw
$settingsContent = $settingsTemplate.Replace("__HOST__", $BindHost)
$settingsContent = $settingsContent.Replace("__PORT__", [string]$Port)
$settingsContent = $settingsContent.Replace("__SECRET__", $secret)

New-Item -ItemType Directory -Force -Path $appDataRoot | Out-Null
Set-Content -Path $settingsPath -Value $settingsContent -Encoding UTF8

$pythonPathEntries = @($sourceRoot, $sitePackages)
$existingPythonPath = [Environment]::GetEnvironmentVariable("PYTHONPATH")
if (-not [string]::IsNullOrWhiteSpace($existingPythonPath)) {
    $pythonPathEntries += $existingPythonPath
}

$env:PYTHONPATH = ($pythonPathEntries -join ';')
$env:SEARXNG_SETTINGS_PATH = $settingsPath
$env:SEARXNG_DISABLE_UPDATE_CHECK = "1"
$env:PYTHONUTF8 = "1"

Push-Location $sourceRoot
try {
    & $pythonExe -m searx.webapp --host $BindHost --port $Port
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
