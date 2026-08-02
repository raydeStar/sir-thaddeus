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

function Ensure-NpmDependencies([string]$PackageDir, [string]$Label) {
    if (-not (Test-Path -LiteralPath $PackageDir -PathType Container)) {
        return
    }

    $packageJson = Join-Path $PackageDir "package.json"
    $packageLock = Join-Path $PackageDir "package-lock.json"
    $nodeModules = Join-Path $PackageDir "node_modules"

    if (-not (Test-Path -LiteralPath $packageJson -PathType Leaf) -or
        -not (Test-Path -LiteralPath $packageLock -PathType Leaf) -or
        (Test-Path -LiteralPath $nodeModules -PathType Container)) {
        return
    }

    Write-Host "  Installing npm dependencies for $Label"
    & npm ci --prefix $PackageDir
    if ($LASTEXITCODE -ne 0) { Fail "npm ci failed for $Label (exit code $LASTEXITCODE)." $LASTEXITCODE }
}

# ── Setup ──────────────────────────────────────────────────────
Write-Section "Test Run (.NET)"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$SlnFile       = Join-Path $RepoRoot "SirThaddeus.sln"
$Artifacts     = Join-Path $RepoRoot "artifacts"
$TestArtifacts = Join-Path $Artifacts "test-results"
$HarnessSuitesRoot = Join-Path $Artifacts "harness-suites"
$ScreenObserveSuite = Join-Path $HarnessSuitesRoot "screen-observe"
$HealthPackDir = Join-Path $RepoRoot "thaddeus-health-pack"
New-Item -ItemType Directory -Force -Path $TestArtifacts | Out-Null

# Unique TRX per run (keeps last few runs visible for debugging)
$stamp  = Get-Date -Format "yyyyMMdd-HHmmss"
$trxName = "test-$stamp"

Write-Host "  Configuration : $Configuration"
Write-Host "  Restore       : $effectiveRestore"
if ($isCi -and -not $Restore) {
    Write-Host "  CI override   : enabled (restore forced)"
}
if ($Filter) { Write-Host "  Filter        : $Filter" }
Write-Host "  Screen Harness: $(if ($SkipScreenObserveHarness) { 'skipped' } elseif ($Filter) { 'skipped (filtered run)' } elseif (-not (Test-Path -LiteralPath $ScreenObserveSuite -PathType Container)) { 'skipped (fixtures not found)' } else { 'enabled' })"
Write-Host "  Results       : $TestArtifacts\$trxName"

# ── Policy Guard: no device geolocation APIs ─────────────────
Write-Section "Policy Guard (No Device Geolocation)"
& "$PSScriptRoot\check-no-device-geolocation.ps1" -RepoRoot $RepoRoot
if ($LASTEXITCODE -ne 0) { Fail "Device geolocation policy guard failed (exit code $LASTEXITCODE)." $LASTEXITCODE }

Write-Section "Model Provider Adapter Contract"
& "$PSScriptRoot\test-model-provider-adapter.ps1"
if ($LASTEXITCODE -ne 0) { Fail "Model provider adapter contract failed (exit code $LASTEXITCODE)." $LASTEXITCODE }

Write-Section "Model Qualification Profile Contract"
& "$PSScriptRoot\test-model-qualification-profile.ps1"
if ($LASTEXITCODE -ne 0) { Fail "Model qualification profile contract failed (exit code $LASTEXITCODE)." $LASTEXITCODE }

# ── Optional restore ──────────────────────────────────────────
if ($effectiveRestore) {
    Write-Section "Restore"
    dotnet restore $SlnFile
    if ($LASTEXITCODE -ne 0) { Fail "dotnet restore failed (exit code $LASTEXITCODE)." $LASTEXITCODE }
}

# ── Build ─────────────────────────────────────────────────────
# Runtime module tests invoke the external Health Pack MCP server via its
# manifest (`npm run mcp`). Fresh CI runners need that package restored before
# dotnet test starts the child process.
Write-Section "Node Packages"
Ensure-NpmDependencies $HealthPackDir "Health Pack"

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
    '--logger', "trx;LogFilePrefix=$trxName",
    '--results-directory', $TestArtifacts
)

# Several test projects spawn the desktop runtime as a child process. Running
# those projects concurrently on a constrained hosted runner can starve startup
# or make shutdown assertions race even though every project has its own lock
# directory. Serialize test projects in CI; individual test collections retain
# their normal xUnit behavior inside each project.
if ($isCi) {
    $testArgs += '-m:1'
    Write-Host "  CI override   : serializing test projects (runtime process isolation)"
}

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
    if (Test-Path -LiteralPath $ScreenObserveSuite -PathType Container) {
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
    else {
        Write-Section "Screen Observe Harness"
        Write-Host "  SKIP  Fixtures not found: $ScreenObserveSuite" -ForegroundColor Yellow
        Write-Host "  INFO  Populate artifacts\harness-suites\screen-observe to include this optional harness." -ForegroundColor DarkGray
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
if ($testExit -ne 0) {
    exit $testExit
}
exit $harnessExit
