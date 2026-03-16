#requires -Version 5.1

param(
    [string]$SettingsPath = "",
    [switch]$IncludeUnitTests,
    [switch]$RequirePlaces
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

if ([string]::IsNullOrWhiteSpace($SettingsPath)) {
    $SettingsPath = Join-Path $env:LOCALAPPDATA "SirThaddeus\settings.json"
}

if (-not (Test-Path $SettingsPath)) {
    Write-Host "settings.json not found at: $SettingsPath" -ForegroundColor Red
    exit 1
}

function Test-HttpEndpoint {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [int]$TimeoutSec = 6
    )

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec $TimeoutSec
        return [PSCustomObject]@{
            Ok      = $true
            Status  = [int]$response.StatusCode
            Message = "OK"
        }
    }
    catch {
        return [PSCustomObject]@{
            Ok      = $false
            Status  = 0
            Message = $_.Exception.Message
        }
    }
}

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host ""
    Write-Host "== $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Step failed: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

function Get-ObjectPropertyValue {
    param(
        [Parameter(Mandatory = $false)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $false)][object]$DefaultValue = $null
    )

    if ($null -eq $Object) { return $DefaultValue }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $DefaultValue }
    return $property.Value
}

$settingsJson = Get-Content -Raw -Path $SettingsPath
$settings = $settingsJson | ConvertFrom-Json

$llm = Get-ObjectPropertyValue -Object $settings -Name "llm"
$webSearch = Get-ObjectPropertyValue -Object $settings -Name "webSearch"
$deepDive = Get-ObjectPropertyValue -Object $settings -Name "deepDive"

$llmBaseUrlValue = Get-ObjectPropertyValue -Object $llm -Name "baseUrl" -DefaultValue ""
$llmBaseUrl = if (-not [string]::IsNullOrWhiteSpace($llmBaseUrlValue)) {
    $llmBaseUrlValue.ToString().Trim()
} else {
    ""
}

$webModeValue = Get-ObjectPropertyValue -Object $webSearch -Name "mode" -DefaultValue "auto"
$webMode = if (-not [string]::IsNullOrWhiteSpace($webModeValue)) {
    $webModeValue.ToString().Trim().ToLowerInvariant()
} else {
    "auto"
}

$searxUrlValue = Get-ObjectPropertyValue -Object $webSearch -Name "searxngBaseUrl" -DefaultValue "http://localhost:8080"
$searxUrl = if (-not [string]::IsNullOrWhiteSpace($searxUrlValue)) {
    $searxUrlValue.ToString().Trim()
} else {
    "http://localhost:8080"
}

$placesApiKeyValue = Get-ObjectPropertyValue -Object $deepDive -Name "placesApiKey" -DefaultValue ""
$placesApiKey = if (-not [string]::IsNullOrWhiteSpace($placesApiKeyValue)) { $placesApiKeyValue.ToString() } else { "" }
$hasPlacesKey = -not [string]::IsNullOrWhiteSpace($placesApiKey)

if ([string]::IsNullOrWhiteSpace($llmBaseUrl)) {
    Write-Host "llm.baseUrl is missing in settings.json" -ForegroundColor Red
    exit 1
}

Write-Host "Harness E2E preflight" -ForegroundColor Green
Write-Host "Repo: $RepoRoot"
Write-Host "Settings: $SettingsPath"
Write-Host "LLM: $llmBaseUrl"
Write-Host "WebSearch mode: $webMode"
Write-Host "SearxNG URL: $searxUrl"
Write-Host ("Places API key configured: " + ($(if ($hasPlacesKey) { "yes" } else { "no" })))

$modelsUrl = $llmBaseUrl.TrimEnd('/') + "/v1/models"
$llmProbe = Test-HttpEndpoint -Url $modelsUrl
if (-not $llmProbe.Ok) {
    Write-Host "LLM endpoint probe failed at ${modelsUrl}: $($llmProbe.Message)" -ForegroundColor Red
    Write-Host "Start LM Studio (or update llm.baseUrl) and retry." -ForegroundColor Yellow
    exit 1
}
Write-Host "LLM endpoint probe OK ($($llmProbe.Status))."

if ($webMode -eq "searxng") {
    $searxProbe = Test-HttpEndpoint -Url $searxUrl
    if (-not $searxProbe.Ok) {
        Write-Host "webSearch.mode is 'searxng' but SearxNG is unreachable: $($searxProbe.Message)" -ForegroundColor Red
        Write-Host "Either start SearxNG or switch webSearch.mode to 'auto'/'search_api' with a configured hosted fallback." -ForegroundColor Yellow
        exit 1
    }
    Write-Host "SearxNG probe OK ($($searxProbe.Status))."
}
elseif ($webMode -eq "auto") {
    $searxProbe = Test-HttpEndpoint -Url $searxUrl
    if ($searxProbe.Ok) {
        Write-Host "Auto mode: SearxNG reachable ($($searxProbe.Status))."
    }
    else {
        Write-Host "Auto mode: SearxNG not reachable ($($searxProbe.Message)); runtime will fallback to SearchApi/Google News if configured." -ForegroundColor Yellow
    }
}

if ($RequirePlaces -and -not $hasPlacesKey) {
    Write-Host "RequirePlaces was set, but deepDive.placesApiKey is empty." -ForegroundColor Red
    exit 1
}

if (-not $hasPlacesKey) {
    Write-Host "Places key missing: deep-dive place tests will run in fallback mode (lower confidence)." -ForegroundColor Yellow
}

if ($IncludeUnitTests) {
    Write-Host ""
    Write-Host "Tip: close SirThaddeus UI windows before unit tests to avoid file-lock build failures." -ForegroundColor Yellow
    Invoke-Step -Name "Unit tests" -Action { ./dev/test.ps1 }
}

Invoke-Step -Name "Harness smoke suite (live)" -Action { ./dev/harness.ps1 smoke --mode live }
Invoke-Step -Name "Harness personality suite (live)" -Action { ./dev/harness.ps1 run --suite personality --mode live }
Invoke-Step -Name "Harness web-search suite (live)" -Action { ./dev/harness.ps1 run --suite web-search --mode live }

Write-Host ""
Write-Host "E2E harness run completed successfully." -ForegroundColor Green
Write-Host "Check artifacts in: artifacts/harness/" -ForegroundColor Green
