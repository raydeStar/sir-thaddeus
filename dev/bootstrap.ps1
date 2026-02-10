#requires -Version 5.1
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

# ── Verify dotnet CLI ──────────────────────────────────────────
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Fail "dotnet CLI not found. Install the .NET SDK and ensure dotnet is on PATH." 2
}

Write-Host "  OK  dotnet found: $($dotnet.Source)" -ForegroundColor Green

# Print SDK info (helpful for CI + agent debugging)
Write-Host ""
Write-Host "  dotnet --info (abbrev):"
$info = dotnet --info
$infoLines = $info -split "`n"
$infoLines | Select-Object -First 25 | ForEach-Object { Write-Host "    $_" }

# ── Prepare artifacts folder ───────────────────────────────────
$Artifacts     = Join-Path $RepoRoot "artifacts"
$TestArtifacts = Join-Path $Artifacts "test"
New-Item -ItemType Directory -Force -Path $TestArtifacts | Out-Null

Write-Host ""
Write-Host "  OK  artifacts folder ready: $TestArtifacts" -ForegroundColor Green

# ── Restore ────────────────────────────────────────────────────
Write-Section "Restore"

dotnet restore SirThaddeus.sln
if ($LASTEXITCODE -ne 0) { Fail "dotnet restore failed (exit code $LASTEXITCODE)." $LASTEXITCODE }

Write-Host "  OK  Restore complete" -ForegroundColor Green

Write-Section "Done"
Write-Host "  Bootstrap complete. Next: dev\test.ps1"
exit 0
