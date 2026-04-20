#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the web workspace and copies it into the runtime's wwwroot.

.DESCRIPTION
    Runs `npm install` (idempotent) and `npm run build` in `web/`, then mirrors
    `web/dist/` into `src/Thaddeus.Runtime/wwwroot/`. The runtime serves whatever it
    finds in wwwroot; in dev you can skip this and rely on `npm run dev` against the
    Vite server, but in production the runtime is the only HTTP origin.
#>

[CmdletBinding()]
param(
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repoRoot 'web'
$wwwroot = Join-Path $repoRoot 'src/Thaddeus.Runtime/wwwroot'

Write-Host "[build-web] repo root: $repoRoot"
Write-Host "[build-web] web dir:   $webDir"
Write-Host "[build-web] wwwroot:   $wwwroot"

Push-Location $webDir
try {
    if (-not $SkipInstall) {
        Write-Host '[build-web] npm install ...'
        npm install --no-audit --no-fund --silent
        if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE" }
    }

    Write-Host '[build-web] npm run build ...'
    npm run build --silent
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

# Mirror dist/ into wwwroot/. Keep wwwroot/README.md (the placeholder note) untouched.
$dist = Join-Path $webDir 'dist'
if (-not (Test-Path $dist)) { throw "Expected $dist after build but it does not exist." }

# Wipe everything in wwwroot except README.md.
Get-ChildItem -Path $wwwroot -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'README.md' } |
    Remove-Item -Recurse -Force

# Copy dist contents.
Copy-Item -Path (Join-Path $dist '*') -Destination $wwwroot -Recurse -Force

Write-Host "[build-web] mirrored dist/ -> wwwroot/ (preserved README.md)"
