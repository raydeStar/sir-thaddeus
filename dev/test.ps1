#requires -Version 5.1

# ═══════════════════════════════════════════════════════════════
#  test.ps1 — Build + run tests with deterministic output.
#  Writes a TRX report to ./artifacts/test/ each run.
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
    [string]$Filter = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
$TestArtifacts = Join-Path $Artifacts "test"
New-Item -ItemType Directory -Force -Path $TestArtifacts | Out-Null

# Unique TRX per run (keeps last few runs visible for debugging)
$stamp  = Get-Date -Format "yyyyMMdd-HHmmss"
$trxName = "test-$stamp.trx"

Write-Host "  Configuration : $Configuration"
Write-Host "  Restore       : $Restore"
if ($Filter) { Write-Host "  Filter        : $Filter" }
Write-Host "  Results       : $TestArtifacts\$trxName"

# ── Optional restore ──────────────────────────────────────────
if ($Restore) {
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

if ($Filter) {
    $testArgs += '--filter'
    $testArgs += $Filter
}

& dotnet @testArgs
$testExit = $LASTEXITCODE

# ── Summary ───────────────────────────────────────────────────
Write-Section "Summary"

if ($testExit -eq 0) {
    Write-Host "  OK  All tests passed." -ForegroundColor Green
    Write-Host "  TRX : $TestArtifacts\$trxName"
    exit 0
}

Write-Host "  FAIL  Tests failed." -ForegroundColor Red
Write-Host "  TRX : $TestArtifacts\$trxName"
Write-Host ""
Write-Host "  Tip: run with -Filter to focus, e.g."
Write-Host "    .\dev\test.ps1 -Filter 'FullyQualifiedName~MyProject.Tests.MyClassTests'"
exit $testExit
