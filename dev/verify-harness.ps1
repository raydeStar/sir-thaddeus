param(
    [switch]$RunLiveHarness
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

Push-Location $repo
try {
    Invoke-Step "Harness typecheck/build" {
        dotnet build "tools/SirThaddeus.Harness/SirThaddeus.Harness.csproj"
    }

    Invoke-Step "Rubric evaluator tests" {
        dotnet test "tests/SirThaddeus.Tests/SirThaddeus.Tests.csproj" `
            -p:SkipWebBuild=true `
            --filter "FullyQualifiedName~ScoringEngineTests"
    }

    if ($RunLiveHarness) {
        Invoke-Step "Live smoke harness" {
            & "$PSScriptRoot/harness.ps1" --suite smoke --test smoke_casual_no_tools --judge none
        }

        Invoke-Step "Latest harness result validation" {
            & "$PSScriptRoot/validate-harness-results.ps1"
        }
    }
    else {
        Write-Host ""
        Write-Host "Skipped live harness smoke. Re-run with -RunLiveHarness when LM Studio is healthy." -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "Harness verification completed." -ForegroundColor Green
}
finally {
    Pop-Location
}
