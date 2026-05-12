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

$effectiveHarnessArgs = if ($HarnessArgs.Count -eq 0 -or $HarnessArgs[0].StartsWith('--')) {
    @('run') + $HarnessArgs
}
else {
    $HarnessArgs
}

$argsToRun = @('run', '--project', $ProjectPath, '--') + $effectiveHarnessArgs

$prevPref = $ErrorActionPreference
$nativePrefVar = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
$prevNativeStderrPref = if ($null -ne $nativePrefVar) { $nativePrefVar.Value } else { $null }
$ErrorActionPreference = 'Continue'
if ($null -ne $nativePrefVar) {
    $PSNativeCommandUseErrorActionPreference = $false
}
& dotnet @argsToRun 2>&1 | ForEach-Object {
    if ($_ -is [System.Management.Automation.ErrorRecord]) {
        $_.ToString()
    }
    else {
        $_
    }
}
if ($null -ne $nativePrefVar) {
    $PSNativeCommandUseErrorActionPreference = $prevNativeStderrPref
}
$ErrorActionPreference = $prevPref

exit $LASTEXITCODE
