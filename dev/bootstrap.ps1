#requires -Version 5.1
param(
    [switch]$SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ═══════════════════════════════════════════════════════════════
#  bootstrap.ps1 — Validate prerequisites + restore once.
#  Run this before your first test loop, or after pulling
#  changes that modify project references / packages.
# ═══════════════════════════════════════════════════════════════

function Write-Section([string]$Title) {
    Write-Host "`n══════════════════════════════════════════════════════════════"
    Write-Host "  $Title"
    Write-Host "══════════════════════════════════════════════════════════════"
}

function Fail([string]$Message, [int]$Code = 1) {
    Write-Host "  FAIL: $Message" -ForegroundColor Red
    exit $Code
}

Write-Section "Bootstrap (.NET)"

# Ensure we're at repo root (script lives in /dev)
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot
$SlnFile = Join-Path $RepoRoot "SirThaddeus.sln"

# Keep dotnet first-run state inside the repo so bootstrap/localrunner do not
# depend on a writable user profile path.
$DotnetCliHome = Join-Path $RepoRoot ".dotnet_cli"
New-Item -ItemType Directory -Force -Path $DotnetCliHome | Out-Null
$env:DOTNET_CLI_HOME = $DotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'

# ── Verify dotnet CLI ──────────────────────────────────────────
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Fail "dotnet CLI not found. Install the .NET SDK and ensure dotnet is on PATH." 2
}

Write-Host "  OK  dotnet found: $($dotnet.Source)" -ForegroundColor Green

# Print SDK info (helpful for CI + agent debugging)
Write-Host ""
Write-Host "  dotnet --info (abbrev):"
$info = & dotnet --info
if ($LASTEXITCODE -ne 0) {
    Fail "dotnet --info failed (exit code $LASTEXITCODE)." $LASTEXITCODE
}
$infoLines = $info -split "`n"
$infoLines | Select-Object -First 25 | ForEach-Object { Write-Host "    $_" }

# ── Prepare artifacts folder ───────────────────────────────────
$Artifacts     = Join-Path $RepoRoot "artifacts"
$TestArtifacts = Join-Path $Artifacts "test-results"
$LegacyTestArtifacts = Join-Path $Artifacts "test"
New-Item -ItemType Directory -Force -Path $TestArtifacts | Out-Null
New-Item -ItemType Directory -Force -Path $LegacyTestArtifacts | Out-Null

Write-Host ""
Write-Host "  OK  artifacts folder ready: $TestArtifacts" -ForegroundColor Green

# ── Restore ────────────────────────────────────────────────────
Write-Section "Restore"

if ($SkipRestore) {
    Write-Host "  Skipping restore (caller requested)." -ForegroundColor DarkGray
}
else {
    dotnet restore $SlnFile
    if ($LASTEXITCODE -ne 0) { Fail "dotnet restore failed (exit code $LASTEXITCODE)." $LASTEXITCODE }

    Write-Host "  OK  Restore complete" -ForegroundColor Green
}

Write-Section "Done"
Write-Host "  Bootstrap complete. Next: dev\test.ps1"
exit 0
