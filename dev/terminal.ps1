#requires -Version 5.1
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$NoTools
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$headlessProject = Join-Path $repoRoot "apps/headless-runtime/SirThaddeus.HeadlessRuntime/SirThaddeus.HeadlessRuntime.csproj"
$mcpProject = Join-Path $repoRoot "apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj"

function Invoke-Step([string]$Title, [scriptblock]$Action) {
    Write-Host ""
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Title failed with exit code $LASTEXITCODE."
    }
}

if (-not $NoBuild) {
    Invoke-Step "Build MCP Server ($Configuration)" {
        dotnet build $mcpProject -c $Configuration -m:1 -v m
    }

    Invoke-Step "Build Headless Runtime ($Configuration)" {
        dotnet build $headlessProject -c $Configuration -m:1 -v m
    }
}

$runtimeArgs = @("--no-build", "--no-restore", "--project", $headlessProject, "--configuration", $Configuration, "--")
if (-not $NoTools) {
    $runtimeArgs += "--tools"
}
$runtimeArgs += $args

Write-Host ""
Write-Host "Launching headless terminal runtime..." -ForegroundColor Green
Write-Host "  /help for commands, /exit to quit." -ForegroundColor DarkGray
Write-Host ""

& dotnet run @runtimeArgs
exit $LASTEXITCODE
