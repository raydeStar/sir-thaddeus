#requires -Version 5.1

param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$HarnessArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$ProjectPath = Join-Path $RepoRoot "tools/SirThaddeus.Harness/SirThaddeus.Harness.csproj"

if (-not (Test-Path $ProjectPath)) {
    Write-Host "Harness project not found at $ProjectPath" -ForegroundColor Red
    exit 1
}

$argsToRun = @('run', '--project', $ProjectPath, '--') + $HarnessArgs
& dotnet @argsToRun
exit $LASTEXITCODE
