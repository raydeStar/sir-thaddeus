#requires -Version 5.1

# ═══════════════════════════════════════════════════════════════
#  preflight.ps1 — production preflight gate
#
#  Runs the same checks we expect before cutting a release build:
#    1) bootstrap environment + restore
#    2) full Release build + test suite
#
#  Usage:
#    .\dev\preflight.ps1
#    .\dev\preflight.ps1 -SkipBootstrap
# ═══════════════════════════════════════════════════════════════

param(
    [switch]$SkipBootstrap
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host "══════════════════════════════════════════════════════════════"
    Write-Host "  $Title"
    Write-Host "══════════════════════════════════════════════════════════════"
}

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

if (-not $SkipBootstrap) {
    Write-Section "Bootstrap"
    & "$PSScriptRoot\bootstrap.ps1"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Section "Release Test Suite"
& "$PSScriptRoot\test_all.ps1"
exit $LASTEXITCODE
