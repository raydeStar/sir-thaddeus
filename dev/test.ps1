#requires -Version 5.1

# ═══════════════════════════════════════════════════════════════
#  test.ps1 — Build + run tests with deterministic output.
#  Writes a TRX report to ./artifacts/test-results/ each run.
#
#  Usage:
#    .\dev\test.ps1                          # defaults
#    .\dev\test.ps1 -Configuration Release   # release build
#    .\dev\test.ps1 -Restore $true           # restore first
#    .\dev\test.ps1 -Filter "FullyQualifiedName~MyClass"
# ═══════════════════════════════════════════════════════════════

param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',

    # Set to $true to restore packages before building (slower).
    [bool]$Restore = $false,

    # dotnet test --filter value. Examples:
    #   "FullyQualifiedName~MyNamespace"
    #   "Category=Unit"
    [string]$Filter = '',

    [switch]$SkipScreenObserveHarness
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$isCi = [string]::Equals($env:CI, 'true', [System.StringComparison]::OrdinalIgnoreCase)
$effectiveRestore = $Restore

if (-not $effectiveRestore -and $isCi) {
    # GitHub runners start clean; force restore so obj/project.assets.json exists.
    $effectiveRestore = $true
}

function Write-Section([string]$Title) {
    Write-Host "`n══════════════════════════════════════════════════════════════"
    Write-Host "  $Title"
    Write-Host "══════════════════════════════════════════════════════════════"
}

function Fail([string]$Message, [int]$Code = 1) {
    Write-Host "  FAIL: $Message" -ForegroundColor Red
    exit $Code
}

# ── Setup ──────────────────────────────────────────────────────
Write-Section "Test Run (.NET)"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$SlnFile       = Join-Path $RepoRoot "SirThaddeus.sln"
$Artifacts     = Join-Path $RepoRoot "artifacts"
$TestArtifacts = Join-Path $Artifacts "test-results"
$HarnessSuitesRoot = Join-Path $Artifacts "harness-suites"
New-Item -ItemType Directory -Force -Path $TestArtifacts | Out-Null

# Unique TRX per run (keeps last few runs visible for debugging)
$stamp  = Get-Date -Format "yyyyMMdd-HHmmss"
$trxName = "test-$stamp.trx"

Write-Host "  Configuration : $Configuration"
Write-Host "  Restore       : $effectiveRestore"
if ($isCi -and -not $Restore) {
    Write-Host "  CI override   : enabled (restore forced)"
}
if ($Filter) { Write-Host "  Filter        : $Filter" }
Write-Host "  Screen Harness: $(if ($SkipScreenObserveHarness) { 'skipped' } elseif ($Filter) { 'skipped (filtered run)' } else { 'enabled' })"
Write-Host "  Results       : $TestArtifacts\$trxName"

# ── Policy Guard: no device geolocation APIs ─────────────────
Write-Section "Policy Guard (No Device Geolocation)"
& "$PSScriptRoot\check-no-device-geolocation.ps1" -RepoRoot $RepoRoot
if ($LASTEXITCODE -ne 0) { Fail "Device geolocation policy guard failed (exit code $LASTEXITCODE)." $LASTEXITCODE }

# ── Optional restore ──────────────────────────────────────────
if ($effectiveRestore) {
    Write-Section "Restore"
    dotnet restore $SlnFile
    if ($LASTEXITCODE -ne 0) { Fail "dotnet restore failed (exit code $LASTEXITCODE)." $LASTEXITCODE }
}

# ── Build ─────────────────────────────────────────────────────
Write-Section "Build"

$buildArgs = @(
    'build', $SlnFile,
    '-c', $Configuration,
    '--nologo',
    '--no-restore'
)
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed (exit code $LASTEXITCODE)." $LASTEXITCODE }

# ── Test ──────────────────────────────────────────────────────
Write-Section "Test"

$testArgs = @(
    'test', $SlnFile,
    '-c', $Configuration,
    '--nologo',
    '--no-build',
    '--logger', "trx;LogFileName=$trxName",
    '--results-directory', $TestArtifacts
)

# Exclude live integration tests in CI — they hit external APIs (NagerDate,
# GitHub status, RSS feeds) that are flaky on hosted runners.
if ($isCi -and -not $Filter) {
    $Filter = 'Category!=Integration'
    Write-Host "  CI override   : excluding Integration tests (flaky network)"
}

if ($Filter) {
    $testArgs += '--filter'
    $testArgs += $Filter
}

& dotnet @testArgs
$testExit = $LASTEXITCODE

$harnessExit = 0
if (-not $Filter -and -not $SkipScreenObserveHarness) {
    Write-Section "Screen Observe Harness"

    $harnessArgs = @(
        '--suites-root', $HarnessSuitesRoot,
        '--suite', 'screen-observe',
        '--max-iters', '1',
        '--judge', 'none'
    )

    & "$PSScriptRoot\harness.ps1" @harnessArgs
    $harnessExit = $LASTEXITCODE

    if ($harnessExit -ne 0) {
        Write-Host "  FAIL  Screen-observe harness failed." -ForegroundColor Red
    }
}

# ── Summary ───────────────────────────────────────────────────
Write-Section "Summary"

if ($testExit -eq 0 -and $harnessExit -eq 0) {
    Write-Host "  OK  All tests passed." -ForegroundColor Green
    Write-Host "  TRX : $TestArtifacts\$trxName"
    exit 0
}

if ($testExit -ne 0) {
    Write-Host "  FAIL  Tests failed." -ForegroundColor Red
}
else {
    Write-Host "  FAIL  Screen-observe harness failed." -ForegroundColor Red
}
Write-Host "  TRX : $TestArtifacts\$trxName"
Write-Host ""
Write-Host "  Tip: run with -Filter to focus, e.g."
Write-Host "    .\dev\test.ps1 -Filter 'FullyQualifiedName~MyProject.Tests.MyClassTests'"
if (-not $SkipScreenObserveHarness -and -not $Filter) {
    Write-Host "  Screen harness: .\dev\harness.ps1 --suites-root .\artifacts\harness-suites --suite screen-observe --max-iters 1 --judge none"
}
exit $(if ($testExit -ne 0) { $testExit } else { $harnessExit })
