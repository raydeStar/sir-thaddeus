#requires -Version 5.1
<#
.SYNOPSIS
    Measures a local model on supported Sir Thaddeus harness suites.

.DESCRIPTION
    Runs the production baseline repeatedly with temperature 0 and writes an
    auditable scorecard plus Markdown report. Rejected reasoning strategies do
    not remain as configurable arms. Partial runs are surfaced explicitly.

    Use -ReuseSummaryPath to regenerate a report from one completed
    harness-repeat summary without loading the model or rerunning inference.

.EXAMPLE
    dev/model-intake.ps1 -ModelId liquid/lfm2.5-8b-a1b
.EXAMPLE
    dev/model-intake.ps1 -ModelId my/new-model -Suites python-probe,solver-probe -Repeats 5
.EXAMPLE
    dev/model-intake.ps1 -ModelId lfm2.5-8b-a1b -Suites python-probe -Repeats 1 -ReuseSummaryPath artifacts/harness-repeat/20260709_215509-python-probe.json
.EXAMPLE
    dev/model-intake.ps1 -Backend llamacpp -ModelId gemma-4-12b-it -LlamaServerPath C:\llama.cpp\llama-server.exe -ModelPath D:\models\gemma-4-12b-it.gguf -PlanOnly
.EXAMPLE
    dev/model-intake.ps1 -Backend external -ProviderName ollama -BaseUrl http://127.0.0.1:11434 -ModelId gemma3:4b
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ModelId,

    [string]$GatekeeperModelId = '',

    [string[]]$Suites = @('python-probe', 'solver-probe'),

    [ValidateRange(1, 100)]
    [int]$Repeats = 3,

    [string]$SettingsTemplate = '',

    [string]$ReuseSummaryPath = '',

    [ValidateSet('lmstudio', 'llamacpp', 'external')]
    [string]$Backend = 'lmstudio',

    [string]$ProviderName = '',

    [string]$BaseUrl = '',

    [string]$LlamaServerPath = '',

    [string]$ModelPath = '',

    [ValidateRange(0, 65535)]
    [int]$Port = 0,

    [ValidateRange(0, 1048576)]
    [int]$ContextWindowTokens = 0,

    [ValidateSet('', 'auto', 'max', 'off')]
    [string]$GpuOffload = '',

    [ValidateRange(0, 64)]
    [int]$Parallel = 0,

    [ValidateRange(1, 3600)]
    [int]$StartupTimeoutSeconds = 120,

    [switch]$HashModel,

    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($GatekeeperModelId)) {
    $GatekeeperModelId = $ModelId
}
if (@($Suites).Count -eq 0 -or @($Suites | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    throw 'At least one non-empty suite name is required.'
}
if (-not [string]::IsNullOrWhiteSpace($ReuseSummaryPath) -and @($Suites).Count -ne 1) {
    throw '-ReuseSummaryPath requires exactly one suite.'
}
if ($PlanOnly -and -not [string]::IsNullOrWhiteSpace($ReuseSummaryPath)) {
    throw '-PlanOnly cannot be combined with -ReuseSummaryPath.'
}
if ($Backend -eq 'llamacpp' -and
    -not [string]::Equals($GatekeeperModelId, $ModelId, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The managed llama.cpp backend launches one model. GatekeeperModelId must match ModelId; use an external backend for a separately managed gatekeeper.'
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepeatScript = Join-Path $RepoRoot 'dev/harness-repeat.ps1'
$ProviderAdapterModule = Join-Path $RepoRoot 'dev/ModelProviderAdapter.psm1'
$RepeatSummaryDir = Join-Path $RepoRoot 'artifacts/harness-repeat'
$script:HarnessBuildPrepared = $false
Set-Location $RepoRoot
Import-Module $ProviderAdapterModule -Force

function Resolve-SettingsTemplate {
    param([string]$ExplicitPath)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            throw "SettingsTemplate '$ExplicitPath' does not exist."
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $candidate = Join-Path $localAppData 'SirThaddeus/settings.json'
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "Could not discover settings at '$candidate'. Pass -SettingsTemplate to override."
    }
    return (Resolve-Path -LiteralPath $candidate).Path
}

function Get-SuiteItemCount {
    param([string]$Suite)
    $suiteDir = Join-Path $RepoRoot "tools/SirThaddeus.Harness/Suites/$Suite"
    if (-not (Test-Path -LiteralPath $suiteDir)) { return 0 }
    return @(Get-ChildItem -LiteralPath $suiteDir -Filter *.yaml -File).Count
}

function Get-NewestSummaryPath {
    param([string]$Suite)
    if (-not (Test-Path -LiteralPath $RepeatSummaryDir)) { return '' }
    $file = Get-ChildItem -LiteralPath $RepeatSummaryDir -Filter "*-$Suite.json" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $file) { return '' }
    return $file.FullName
}

function Invoke-SuiteMeasurement {
    param([string]$Suite, [int]$RepeatCount)

    $before = Get-NewestSummaryPath -Suite $Suite
    Write-Host ""
    Write-Host "--- baseline suite=$Suite repeats=$RepeatCount ---" -ForegroundColor Cyan

    $arguments = @('-Suite', $Suite, '-Repeats', $RepeatCount)
    if ($script:HarnessBuildPrepared) { $arguments += '-SkipBuild' }
    $previousErrorActionPreference = $ErrorActionPreference
    $nativePreference = Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    $previousNativePreference = if ($null -ne $nativePreference) { $nativePreference.Value } else { $null }
    try {
        # Harness phase markers are deliberately written to stderr. Capture the
        # native exit code explicitly so PowerShell 7 does not turn ordinary
        # progress diagnostics into a terminating NativeCommandError.
        $ErrorActionPreference = 'Continue'
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $false }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $RepeatScript @arguments 2>&1 |
            ForEach-Object { Write-Host $_.ToString() }
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $previousNativePreference }
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "harness-repeat failed for suite '$Suite' with exit code $exitCode."
    }
    $script:HarnessBuildPrepared = $true

    $after = Get-NewestSummaryPath -Suite $Suite
    if ([string]::IsNullOrWhiteSpace($after) -or $after -eq $before) {
        throw "No new harness-repeat summary was produced for suite '$Suite'."
    }
    return [pscustomobject]@{
        suite = $Suite
        summary_path = $after
        summary = (Get-Content -LiteralPath $after -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
}

function Get-PartialRunCount {
    param($Summary)
    if ($null -eq $Summary -or -not ($Summary.PSObject.Properties.Name -contains 'runs')) { return 0 }
    return @($Summary.runs | Where-Object { $_.PSObject.Properties.Name -contains 'partial' -and $_.partial }).Count
}

function Format-Percent {
    param([double]$Value)
    return ('{0:0.0}%' -f ($Value * 100.0))
}

if (-not (Test-Path -LiteralPath $RepeatScript)) {
    throw "harness-repeat script not found at '$RepeatScript'."
}
if (-not (Test-Path -LiteralPath $ProviderAdapterModule)) {
    throw "provider adapter module not found at '$ProviderAdapterModule'."
}

Write-Host ""
Write-Host '========================================================================' -ForegroundColor Cyan
Write-Host 'MODEL INTAKE - PRODUCTION BASELINE' -ForegroundColor Cyan
Write-Host "model:      $ModelId" -ForegroundColor Cyan
Write-Host "gatekeeper: $GatekeeperModelId" -ForegroundColor Cyan
Write-Host "backend:    $Backend" -ForegroundColor Cyan
Write-Host "suites:     $($Suites -join ', ')" -ForegroundColor Cyan
Write-Host "repeats:    $Repeats" -ForegroundColor Cyan
Write-Host '========================================================================' -ForegroundColor Cyan

$previousSettingsPath = $env:ST_SETTINGS_PATH
$previousSkipPrewarm = $env:ST_HARNESS_SKIP_PREWARM
$previousDisableFastPath = $env:ST_HARNESS_DISABLE_FASTPATH
$tempRoot = $null
$providerSession = $null
$providerPlan = $null
$providerPlanPath = $null
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$safeModel = $ModelId -replace '[^A-Za-z0-9._-]', '_'
$safeBackend = $Backend -replace '[^A-Za-z0-9._-]', '_'
$outputDirectory = Join-Path $RepoRoot "artifacts/model-intake/$stamp-$safeModel-$safeBackend"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

try {
    $measurements = @()
    if (-not [string]::IsNullOrWhiteSpace($ReuseSummaryPath)) {
        if (-not (Test-Path -LiteralPath $ReuseSummaryPath)) {
            throw "ReuseSummaryPath '$ReuseSummaryPath' does not exist."
        }
        $resolved = (Resolve-Path -LiteralPath $ReuseSummaryPath).Path
        $summary = Get-Content -LiteralPath $resolved -Raw -Encoding UTF8 | ConvertFrom-Json
        $suite = @($Suites)[0]
        if (-not [string]::Equals([string]$summary.suite, $suite, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Reused summary suite '$($summary.suite)' does not match '$suite'."
        }
        if ([int]$summary.repeats -ne $Repeats) {
            throw "Reused summary has $($summary.repeats) repeats; -Repeats is $Repeats."
        }
        $measurements = @([pscustomobject]@{ suite = $suite; summary_path = $resolved; summary = $summary })
        Write-Host "Reusing completed summary: $resolved" -ForegroundColor Yellow
    }
    else {
        $template = Resolve-SettingsTemplate -ExplicitPath $SettingsTemplate
        $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('st-model-intake-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
        $patchedSettings = Join-Path $tempRoot 'settings.json'
        $shouldHashModel = $HashModel -or ($Backend -eq 'llamacpp' -and -not $PlanOnly)
        $providerPlan = New-ModelProviderPlan `
            -Backend $Backend `
            -ModelId $ModelId `
            -ProviderName $ProviderName `
            -BaseUrl $BaseUrl `
            -LlamaServerPath $LlamaServerPath `
            -ModelPath $ModelPath `
            -Port $Port `
            -ContextWindowTokens $ContextWindowTokens `
            -GpuOffload $GpuOffload `
            -Parallel $Parallel `
            -StartupTimeoutSeconds $StartupTimeoutSeconds `
            -HashModel:$shouldHashModel
        New-ModelIntakeSettings `
            -TemplatePath $template `
            -ProviderPlan $providerPlan `
            -GatekeeperModelId $GatekeeperModelId `
            -DestinationPath $patchedSettings
        $providerPlan | Add-Member -NotePropertyName settings_sha256 -NotePropertyValue ((Get-FileHash -LiteralPath $patchedSettings -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
        $providerPlan | Add-Member -NotePropertyName process_id -NotePropertyValue $null -Force
        $providerPlan | Add-Member -NotePropertyName ready_utc -NotePropertyValue $null -Force
        $providerPlan | Add-Member -NotePropertyName stdout_path -NotePropertyValue $null -Force
        $providerPlan | Add-Member -NotePropertyName stderr_path -NotePropertyValue $null -Force
        $providerPlan | Add-Member -NotePropertyName ownership_verified -NotePropertyValue $false -Force
        $providerPlan | Add-Member -NotePropertyName provider_observation -NotePropertyValue $null -Force
        $providerPlan | Add-Member -NotePropertyName cleanup_verified -NotePropertyValue $false -Force
        $providerPlan | Add-Member -NotePropertyName cleanup_utc -NotePropertyValue $null -Force
        $providerPlanPath = Join-Path $outputDirectory 'provider-plan.json'
        [IO.File]::WriteAllText($providerPlanPath, ($providerPlan | ConvertTo-Json -Depth 10), $utf8NoBom)

        if ($PlanOnly) {
            Write-Host "Provider plan: $providerPlanPath" -ForegroundColor Green
            Write-Host 'Plan-only validation complete; no provider was started and no model call was made.' -ForegroundColor Green
            return
        }

        $env:ST_SETTINGS_PATH = $patchedSettings
        $env:ST_HARNESS_SKIP_PREWARM = '1'
        $env:ST_HARNESS_DISABLE_FASTPATH = '1'

        $providerSession = Start-ModelProvider -ProviderPlan $providerPlan -LogDirectory $outputDirectory
        $providerPlan.ready_utc = (Get-Date).ToUniversalTime().ToString('O')
        $providerPlan.ownership_verified = [bool]$providerSession.ownership_verified
        $providerPlan.provider_observation = $providerSession.provider_observation
        if ($null -ne $providerSession.process) {
            $providerPlan.process_id = $providerSession.process.Id
            $providerPlan.stdout_path = $providerSession.stdout_path
            $providerPlan.stderr_path = $providerSession.stderr_path
        }
        [IO.File]::WriteAllText($providerPlanPath, ($providerPlan | ConvertTo-Json -Depth 10), $utf8NoBom)
        foreach ($suite in $Suites) {
            $measurements += Invoke-SuiteMeasurement -Suite $suite -RepeatCount $Repeats
        }
    }

    $suiteCards = foreach ($measurement in $measurements) {
        $summary = $measurement.summary
        [pscustomobject]@{
            suite = $measurement.suite
            item_count = Get-SuiteItemCount -Suite $measurement.suite
            repeats = $summary.repeats
            mean_pass_rate = $summary.overall.mean_pass_rate
            stddev_pass_rate = $summary.overall.stddev_pass_rate
            aggregation = $summary.overall.aggregation
            total_passes = $summary.overall.total_passes
            total_item_results = $summary.overall.total_item_results
            partial_run_count = Get-PartialRunCount -Summary $summary
            summary_path = $measurement.summary_path
        }
    }

    $scorecard = [pscustomobject]@{
        schema_version = 3
        generated_utc = (Get-Date).ToUniversalTime().ToString('O')
        mode = 'production-baseline'
        model_id = $ModelId
        gatekeeper_id = $GatekeeperModelId
        provider_backend = if ($null -eq $providerPlan) { 'reused-artifact' } else { $providerPlan.backend }
        provider_name = if ($null -eq $providerPlan) { $null } else { $providerPlan.provider }
        provider_base_url = if ($null -eq $providerPlan) { $null } else { $providerPlan.base_url }
        provider_plan_path = $providerPlanPath
        repeats = $Repeats
        suites_requested = @($Suites)
        honesty_note = 'Baseline production behavior only. Partial runs are surfaced and no experimental reasoning arms are retained.'
        suites = @($suiteCards)
    }

    $scorecardPath = Join-Path $outputDirectory 'scorecard.json'
    [IO.File]::WriteAllText($scorecardPath, ($scorecard | ConvertTo-Json -Depth 10), $utf8NoBom)

    $report = New-Object System.Collections.ArrayList
    [void]$report.Add('# Model intake report')
    [void]$report.Add('')
    [void]$report.Add("- Model: ``$ModelId``")
    [void]$report.Add("- Gatekeeper: ``$GatekeeperModelId``")
    [void]$report.Add("- Provider backend: ``$($scorecard.provider_backend)``")
    if ($null -ne $providerPlan) {
        [void]$report.Add("- Provider: ``$($providerPlan.provider)`` at ``$($providerPlan.base_url)``")
        [void]$report.Add("- Provider plan: ``$providerPlanPath``")
    }
    [void]$report.Add("- Repeats: $Repeats")
    [void]$report.Add('- Mode: production baseline')
    [void]$report.Add('')
    [void]$report.Add('| Suite | Items | Pass rate | Stddev | Partial runs |')
    [void]$report.Add('|---|---:|---:|---:|---:|')
    foreach ($card in $suiteCards) {
        [void]$report.Add(('| {0} | {1} | {2} | {3} | {4} |' -f
            $card.suite,
            $card.item_count,
            (Format-Percent -Value ([double]$card.mean_pass_rate)),
            (Format-Percent -Value ([double]$card.stddev_pass_rate)),
            $card.partial_run_count))
    }
    [void]$report.Add('')
    [void]$report.Add('This report measures the supported production baseline. Evaluate new strategies on short-lived branches with predeclared promotion gates; do not store rejected behavior behind dormant flags.')

    $reportPath = Join-Path $outputDirectory 'report.md'
    [IO.File]::WriteAllText($reportPath, (($report.ToArray()) -join "`r`n"), $utf8NoBom)

    Write-Host ""
    Write-Host "Scorecard: $scorecardPath" -ForegroundColor Green
    Write-Host "Report:    $reportPath" -ForegroundColor Green
}
finally {
    $cleanupFailure = $null
    try {
        Stop-ModelProvider -ProviderSession $providerSession
        if ($null -ne $providerSession -and $null -ne $providerPlan) {
            $providerPlan.cleanup_verified = [bool]$providerSession.cleanup_verified
            $providerPlan.cleanup_utc = (Get-Date).ToUniversalTime().ToString('O')
        }
    }
    catch {
        $cleanupFailure = $_
    }
    if ($null -ne $providerPlan -and -not [string]::IsNullOrWhiteSpace([string]$providerPlanPath)) {
        [IO.File]::WriteAllText($providerPlanPath, ($providerPlan | ConvertTo-Json -Depth 12), $utf8NoBom)
    }
    $env:ST_SETTINGS_PATH = $previousSettingsPath
    $env:ST_HARNESS_SKIP_PREWARM = $previousSkipPrewarm
    $env:ST_HARNESS_DISABLE_FASTPATH = $previousDisableFastPath
    if ($null -ne $tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        $temporarySettings = Join-Path $tempRoot 'settings.json'
        Remove-Item -LiteralPath $temporarySettings -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $tempRoot -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $cleanupFailure) { throw $cleanupFailure }
}
