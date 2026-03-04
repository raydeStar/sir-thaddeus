#requires -Version 5.1

param(
    [string]$Suite = "smoke",
    [switch]$Replay,
    [int]$MaxIters = 1,
    [switch]$StrictExternal
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$HarnessProject = Join-Path $RepoRoot "tools/SirThaddeus.Harness/SirThaddeus.Harness.csproj"
$HarnessDll = Join-Path $RepoRoot "tools/SirThaddeus.Harness/bin/Debug/net9.0/SirThaddeus.Harness.dll"

function Write-Info([string]$Text) {
    Write-Host "[harness-pr-fast] $Text"
}

function IsKnownExternalOutage([string]$StepsContent) {
    if ([string]::IsNullOrWhiteSpace($StepsContent)) {
        return $false
    }

    $patterns = @(
        "geocoding-api.open-meteo.com:443",
        "api.open-meteo.com:443",
        "localhost:8080",
        "searxng",
        "attempt was made to access a socket in a way forbidden",
        "connection refused",
        "timed out",
        "no such host is known",
        "name or service not known"
    )

    $lower = $StepsContent.ToLowerInvariant()
    foreach ($pattern in $patterns) {
        if ($lower.Contains($pattern)) {
            return $true
        }
    }

    return $false
}

function Get-LatestRunSuitePath([string]$ArtifactsRoot, [string]$SuiteName) {
    if (-not (Test-Path $ArtifactsRoot)) {
        return $null
    }

    $latestRun = Get-ChildItem -Path $ArtifactsRoot -Directory |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $latestRun) {
        return $null
    }

    $suitePath = Join-Path $latestRun.FullName $SuiteName
    if (-not (Test-Path $suitePath)) {
        return $null
    }

    return $suitePath
}

Write-Info "Building harness project..."
& dotnet build $HarnessProject --no-restore -m:1 -v m | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Info "Harness build failed."
    exit $LASTEXITCODE
}

$mode = if ($Replay) { "replay" } else { "live" }

Write-Info "Running suite '$Suite' in mode '$mode' (max iters: $MaxIters)..."
& dotnet $HarnessDll run --suite $Suite --mode $mode --max-iters $MaxIters --judge none | Out-Host
$harnessExit = $LASTEXITCODE

if ($harnessExit -eq 0) {
    Write-Info "Harness completed successfully."
    exit 0
}

if ($Replay) {
    Write-Info "Replay mode failed (no external-outage downgrade in replay mode)."
    exit $harnessExit
}

$suitePath = Get-LatestRunSuitePath -ArtifactsRoot (Join-Path $RepoRoot "artifacts/harness") -SuiteName $Suite
if ($null -eq $suitePath) {
    Write-Info "Could not locate harness artifacts for suite '$Suite'."
    exit $harnessExit
}

$scoreFiles = Get-ChildItem -Path $suitePath -Recurse -File -Filter "score.json"
if ($scoreFiles.Count -eq 0) {
    Write-Info "No score files found in suite artifacts."
    exit $harnessExit
}

$actionableFailures = @()
$inconclusiveFailures = @()

foreach ($scoreFile in $scoreFiles) {
    $score = Get-Content -Raw -Path $scoreFile.FullName | ConvertFrom-Json
    $hardPass = [bool]$score.hard_pass
    if ($hardPass) {
        continue
    }

    $hardFailures = @($score.hard_failures)
    $iterDir = Split-Path -Parent $scoreFile.FullName
    $stepsPath = Join-Path $iterDir "steps.jsonl"
    $stepsContent = if (Test-Path $stepsPath) {
        Get-Content -Raw -Path $stepsPath
    }
    else {
        ""
    }

    $missingRequiredToolOnly = $true
    foreach ($failure in $hardFailures) {
        if (-not ($failure -like "Required tool not called:*")) {
            $missingRequiredToolOnly = $false
            break
        }
    }

    $isExternal = $missingRequiredToolOnly -and (IsKnownExternalOutage -StepsContent $stepsContent)
    $testId = Split-Path -Parent $iterDir | Split-Path -Leaf

    if ($isExternal) {
        $inconclusiveFailures += $testId
    }
    else {
        $actionableFailures += $testId
    }
}

if ($actionableFailures.Count -gt 0) {
    Write-Info ("Actionable harness failures: " + ($actionableFailures -join ", "))
    exit $harnessExit
}

if ($inconclusiveFailures.Count -gt 0) {
    Write-Info ("INCONCLUSIVE due to external provider outages: " + ($inconclusiveFailures -join ", "))
    if ($StrictExternal) {
        Write-Info "Strict external mode is enabled; treating inconclusive as failure."
        exit $harnessExit
    }

    Write-Info "Downgrading to success for PR-fast mode."
    exit 0
}

exit $harnessExit
