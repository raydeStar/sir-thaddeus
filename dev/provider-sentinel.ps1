#requires -Version 5.1
<#
.SYNOPSIS
    Proves exact local-provider ownership and configuration with one non-benchmark request.

.DESCRIPTION
    Starts one LM Studio or native llama.cpp provider through the research
    adapter, verifies the effective provider state, sends exactly one neutral
    OpenAI-compatible chat request, and verifies adapter-owned cleanup. The
    script does not run a Sir Thaddeus harness suite or consume benchmark tasks.

.EXAMPLE
    ./dev/provider-sentinel.ps1 -Backend lmstudio -ModelId liquid/lfm2.5-1.2b -ContextWindowTokens 4096 -GpuOffload max -Parallel 1

.EXAMPLE
    ./dev/provider-sentinel.ps1 -Backend llamacpp -ModelId liquid/lfm2.5-1.2b -LlamaServerPath C:\tools\llama.cpp\llama-server.exe -ModelPath D:\models\LFM2.5-1.2B-Instruct-Q4_K_M.gguf -ContextWindowTokens 4096 -GpuOffload max -Parallel 1
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('lmstudio', 'llamacpp')]
    [string]$Backend,

    [Parameter(Mandatory = $true)]
    [string]$ModelId,

    [string]$BaseUrl = '',
    [string]$LlamaServerPath = '',
    [string]$ModelPath = '',

    [ValidateRange(0, 65535)]
    [int]$Port = 0,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1048576)]
    [int]$ContextWindowTokens,

    [ValidateSet('', 'auto', 'max', 'off')]
    [string]$GpuOffload = 'max',

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 64)]
    [int]$Parallel,

    [ValidateSet('chat', 'tool')]
    [string]$SentinelMode = 'chat',

    [ValidateRange(1, 300)]
    [int]$RequestTimeoutSeconds = 60,

    [ValidateRange(1, 3600)]
    [int]$StartupTimeoutSeconds = 120,

    [string]$ArtifactRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$modulePath = Join-Path $PSScriptRoot 'ModelProviderAdapter.psm1'
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $repoRoot 'artifacts/provider-sentinel'
}
elseif (-not [IO.Path]::IsPathRooted($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $repoRoot $ArtifactRoot
}

Import-Module $modulePath -Force
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$safeModel = $ModelId -replace '[^A-Za-z0-9._-]', '_'
$outputDirectory = Join-Path $ArtifactRoot "$stamp-$safeModel-$Backend-$SentinelMode"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$planPath = Join-Path $outputDirectory 'provider-plan.json'
$resultPath = Join-Path $outputDirectory 'sentinel.json'

$providerPlan = New-ModelProviderPlan `
    -Backend $Backend `
    -ModelId $ModelId `
    -BaseUrl $BaseUrl `
    -LlamaServerPath $LlamaServerPath `
    -ModelPath $ModelPath `
    -Port $Port `
    -ContextWindowTokens $ContextWindowTokens `
    -GpuOffload $GpuOffload `
    -Parallel $Parallel `
    -StartupTimeoutSeconds $StartupTimeoutSeconds `
    -HashModel:($Backend -eq 'llamacpp')

[IO.File]::WriteAllText($planPath, ($providerPlan | ConvertTo-Json -Depth 12), $utf8NoBom)
$providerSession = $null
$failure = $null
$sentinel = $null
$cleanupError = $null
$cleanupVerified = $false

try {
    $providerSession = Start-ModelProvider -ProviderPlan $providerPlan -LogDirectory $outputDirectory
    $sentinel = Invoke-ModelProviderSentinel `
        -ProviderPlan $providerPlan `
        -ProviderSession $providerSession `
        -Mode $SentinelMode `
        -TimeoutSeconds $RequestTimeoutSeconds
}
catch {
    $failure = $_
}
finally {
    try {
        Stop-ModelProvider -ProviderSession $providerSession
        if ($null -ne $providerSession) {
            $cleanupVerified = [bool]$providerSession.cleanup_verified
        }
    }
    catch {
        $cleanupError = $_
    }

    $status = if ($null -eq $failure -and $null -eq $cleanupError -and $cleanupVerified) { 'passed' } else { 'failed' }
    $record = [ordered]@{
        schema_version = 1
        status = $status
        completed_utc = (Get-Date).ToUniversalTime().ToString('O')
        benchmark_case_evaluations = 0
        provider_model_requests = if ($null -ne $providerSession -and
            ($providerSession.PSObject.Properties.Name -contains 'sentinel_request_attempted') -and
            [bool]$providerSession.sentinel_request_attempted) { 1 } else { 0 }
        provider_plan_path = $planPath
        provider_plan_sha256 = (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash.ToLowerInvariant()
        provider_observation = if ($null -ne $providerSession) { $providerSession.provider_observation } else { $null }
        sentinel = $sentinel
        failed_response_observation = if ($null -ne $providerSession -and
            ($providerSession.PSObject.Properties.Name -contains 'sentinel_response_observation')) {
            $providerSession.sentinel_response_observation
        } else { $null }
        cleanup_verified = $cleanupVerified
        failure_type = if ($null -ne $failure) { $failure.Exception.GetType().FullName } elseif ($null -ne $cleanupError) { $cleanupError.Exception.GetType().FullName } else { $null }
        failure_message = if ($null -ne $failure) { $failure.Exception.Message } elseif ($null -ne $cleanupError) { $cleanupError.Exception.Message } else { $null }
    }
    [IO.File]::WriteAllText($resultPath, ($record | ConvertTo-Json -Depth 16), $utf8NoBom)
}

if ($null -ne $failure) { throw $failure }
if ($null -ne $cleanupError) { throw $cleanupError }
if (-not $cleanupVerified) { throw "Provider sentinel did not prove cleanup. Artifact: $resultPath" }

Write-Host "PASS provider ownership sentinel: $resultPath" -ForegroundColor Green
