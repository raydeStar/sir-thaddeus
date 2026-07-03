#requires -Version 5.1
<#
.SYNOPSIS
    Model intake rig. Answers "a new local model just released: is it good, and how
    should we configure it?" by MEASURING a config matrix (arms x suites) instead of
    guessing.

.DESCRIPTION
    Optimal scaffolding is per-model, not universal: self-consistency took a 1.2B model
    from 0/6 to 6/6 on one suite but took an 8B from 6/6 to 4/6 on another. So this rig
    never assumes an arm helps - it runs each arm against each suite K times via
    dev/harness-repeat.ps1, collects the aggregate JSON that script emits, and writes a
    scorecard plus a compact report with a per-suite recommended config.

    For each arm the rig sets the arm's env vars, then invokes:
        dev/harness-repeat.ps1 -Suite <suite> -Repeats <K>
    and reads the newest artifacts/harness-repeat/<stamp>-<suite>.json it produced.

    Honesty rules baked in:
      - Partial runs are never dropped silently; their counts are surfaced per cell.
      - K and stddev sit next to every number.
      - Recommendation prefers baseline on ties within 1 stddev: scaffolding must EARN
        its extra latency before we recommend it.

    This script does NOT embed expected answers. It only aggregates harness output.

.EXAMPLE
    dev/model-intake.ps1 -ModelId liquid/lfm2.5-8b-a1b
.EXAMPLE
    dev/model-intake.ps1 -ModelId my/new-model -Suites python-probe,solver-probe -Repeats 5
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ModelId,

    [string]$GatekeeperModelId = '',

    [string[]]$Suites = @('python-probe', 'solver-probe'),

    [int]$Repeats = 3,

    [string[]]$Arms = @('baseline', 'sc', 'sc-tools'),

    [string]$SettingsTemplate = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# GatekeeperModelId defaults to the primary model id (PS 5.1 has no ?? / ternary).
if ([string]::IsNullOrWhiteSpace($GatekeeperModelId)) {
    $GatekeeperModelId = $ModelId
}

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $RepoRoot

# ---------------------------------------------------------------------------
# Arm definitions. Each arm is a named set of scaffolding env vars.
#   baseline : both SC flags cleared (the honest floor).
#   sc       : CoT self-consistency, 5 samples.
#   sc-tools : tool-aware self-consistency, 3 samples (requires ST_SELF_CONSISTENCY too).
# ST_HARNESS_SKIP_PREWARM and ST_HARNESS_DISABLE_FASTPATH are ALWAYS on for every arm
# (including baseline) so the harness measures the model, not the pre-LLM shortcuts.
# ---------------------------------------------------------------------------
function Get-ArmEnv {
    param([string]$arm)
    switch ($arm) {
        'baseline' {
            return [pscustomobject]@{
                ST_SELF_CONSISTENCY       = $null
                ST_SELF_CONSISTENCY_TOOLS = $null
            }
        }
        'sc' {
            return [pscustomobject]@{
                ST_SELF_CONSISTENCY       = '5'
                ST_SELF_CONSISTENCY_TOOLS = $null
            }
        }
        'sc-tools' {
            return [pscustomobject]@{
                ST_SELF_CONSISTENCY       = '3'
                ST_SELF_CONSISTENCY_TOOLS = '1'
            }
        }
        default {
            throw "Unknown arm '$arm'. Known arms: baseline, sc, sc-tools."
        }
    }
}

# Human-readable env-var list for an arm (used in report.md recommendations).
function Get-ArmEnvDescription {
    param([string]$arm)
    $armEnv = Get-ArmEnv -arm $arm
    $parts = New-Object System.Collections.ArrayList
    # Always-on measurement flags come first so the reader can copy the full set.
    [void]$parts.Add('ST_HARNESS_SKIP_PREWARM=1')
    [void]$parts.Add('ST_HARNESS_DISABLE_FASTPATH=1')
    if ($null -ne $armEnv.ST_SELF_CONSISTENCY) {
        [void]$parts.Add(('ST_SELF_CONSISTENCY={0}' -f $armEnv.ST_SELF_CONSISTENCY))
    }
    else {
        [void]$parts.Add('ST_SELF_CONSISTENCY (unset)')
    }
    if ($null -ne $armEnv.ST_SELF_CONSISTENCY_TOOLS) {
        [void]$parts.Add(('ST_SELF_CONSISTENCY_TOOLS={0}' -f $armEnv.ST_SELF_CONSISTENCY_TOOLS))
    }
    else {
        [void]$parts.Add('ST_SELF_CONSISTENCY_TOOLS (unset)')
    }
    return ($parts.ToArray() -join '; ')
}

# Apply an arm's env vars into the current process env. $null clears the var.
function Set-ArmEnvironment {
    param([string]$arm)
    $armEnv = Get-ArmEnv -arm $arm

    # Always-on measurement flags (every arm, baseline included).
    $env:ST_HARNESS_SKIP_PREWARM = '1'
    $env:ST_HARNESS_DISABLE_FASTPATH = '1'

    if ($null -eq $armEnv.ST_SELF_CONSISTENCY) {
        Remove-Item Env:ST_SELF_CONSISTENCY -ErrorAction SilentlyContinue
    }
    else {
        $env:ST_SELF_CONSISTENCY = $armEnv.ST_SELF_CONSISTENCY
    }

    if ($null -eq $armEnv.ST_SELF_CONSISTENCY_TOOLS) {
        Remove-Item Env:ST_SELF_CONSISTENCY_TOOLS -ErrorAction SilentlyContinue
    }
    else {
        $env:ST_SELF_CONSISTENCY_TOOLS = $armEnv.ST_SELF_CONSISTENCY_TOOLS
    }
}

# ---------------------------------------------------------------------------
# Settings discovery + patching.
# Default template = the real settings file the runtime reads, per SettingsManager.cs:
#   %LOCALAPPDATA%\SirThaddeus\settings.json
# We copy it to a temp dir, set llm.model + llm.gatekeeperModelId, force
# llm.temperature = 0, and point ST_SETTINGS_PATH at the copy.
# ---------------------------------------------------------------------------
function Resolve-SettingsTemplate {
    param([string]$explicit)
    if (-not [string]::IsNullOrWhiteSpace($explicit)) {
        if (-not (Test-Path $explicit)) {
            throw "SettingsTemplate '$explicit' does not exist."
        }
        return (Resolve-Path $explicit).Path
    }
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $candidate = Join-Path $localAppData 'SirThaddeus/settings.json'
    if (-not (Test-Path $candidate)) {
        throw ("Could not discover a settings template. Expected the real settings file at " +
            "'$candidate' (per SettingsManager.cs). Pass -SettingsTemplate <path> to override.")
    }
    return (Resolve-Path $candidate).Path
}

function New-PatchedSettings {
    param(
        [string]$templatePath,
        [string]$modelId,
        [string]$gatekeeperModelId,
        [string]$destPath
    )
    $raw = Get-Content -Path $templatePath -Raw -Encoding UTF8
    $settings = $raw | ConvertFrom-Json

    # Ensure the llm block exists before patching it.
    if (-not ($settings.PSObject.Properties.Name -contains 'llm') -or $null -eq $settings.llm) {
        $settings | Add-Member -NotePropertyName 'llm' -NotePropertyValue ([pscustomobject]@{}) -Force
    }

    Set-JsonProperty -object $settings.llm -name 'model' -value $modelId
    Set-JsonProperty -object $settings.llm -name 'gatekeeperModelId' -value $gatekeeperModelId
    # Temperature MUST stay 0 for measurement determinism.
    Set-JsonProperty -object $settings.llm -name 'temperature' -value 0

    $json = $settings | ConvertTo-Json -Depth 32
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($destPath, $json, $utf8NoBom)
}

# Add-or-overwrite a property on a PSCustomObject (ConvertFrom-Json gives PSCustomObject).
function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]$object,
        [Parameter(Mandatory = $true)][string]$name,
        [Parameter(Mandatory = $true)]$value
    )
    if ($object.PSObject.Properties.Name -contains $name) {
        $object.$name = $value
    }
    else {
        $object | Add-Member -NotePropertyName $name -NotePropertyValue $value -Force
    }
}

# ---------------------------------------------------------------------------
# LM Studio model availability.
# ---------------------------------------------------------------------------
function Assert-LmsAvailable {
    $lms = Get-Command lms -ErrorAction SilentlyContinue
    if ($null -eq $lms) {
        throw ("LM Studio CLI 'lms' was not found on PATH. Install/enable it, or start LM Studio, " +
            "before running the intake rig.")
    }
}

function Test-ModelLoaded {
    param([string]$modelId)
    # `lms ps` lists loaded models. We match the model id as a substring of its output.
    $psOutput = & lms ps 2>&1 | ForEach-Object { [string]$_ }
    if ($null -eq $psOutput) { return $false }
    $joined = ($psOutput -join "`n")
    return $joined.Contains($modelId)
}

function Ensure-ModelLoaded {
    param([string]$modelId)
    if (Test-ModelLoaded -modelId $modelId) {
        Write-Host ("Model '{0}' already loaded (per lms ps)." -f $modelId) -ForegroundColor Green
        return
    }
    Write-Host ("Model '{0}' not loaded. Loading via lms..." -f $modelId) -ForegroundColor Yellow
    & lms load $modelId -y 2>&1 | ForEach-Object { Write-Host ([string]$_) }
    if ($LASTEXITCODE -ne 0) {
        throw ("lms load '$modelId' failed (exit $LASTEXITCODE). Confirm the model id is correct " +
            "and downloaded (lms ls).")
    }
    if (-not (Test-ModelLoaded -modelId $modelId)) {
        throw ("Loaded '$modelId' but it does not appear in lms ps. Aborting so we never measure the " +
            "wrong model.")
    }
    Write-Host ("Model '{0}' loaded." -f $modelId) -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Suite item counts (for context in the report). Matches harness-repeat.ps1's
# expected-item logic: tools/SirThaddeus.Harness/Suites/<suite>/*.yaml.
# ---------------------------------------------------------------------------
function Get-SuiteItemCount {
    param([string]$suite)
    $suiteDir = Join-Path $RepoRoot ("tools/SirThaddeus.Harness/Suites/{0}" -f $suite)
    if (-not (Test-Path $suiteDir)) { return 0 }
    return @(Get-ChildItem -Path $suiteDir -Filter *.yaml -File -ErrorAction SilentlyContinue).Count
}

# ---------------------------------------------------------------------------
# Run one arm x suite and collect the harness-repeat summary JSON.
# ---------------------------------------------------------------------------
$RepeatScript = Join-Path $RepoRoot 'dev/harness-repeat.ps1'
$RepeatSummaryDir = Join-Path $RepoRoot 'artifacts/harness-repeat'

function Get-NewestSummaryStamp {
    param([string]$suite)
    if (-not (Test-Path $RepeatSummaryDir)) { return '' }
    $pattern = ('*-{0}.json' -f $suite)
    $newest = Get-ChildItem -Path $RepeatSummaryDir -Filter $pattern -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $newest) { return '' }
    return $newest.FullName
}

function Invoke-ArmSuite {
    param(
        [string]$arm,
        [string]$suite,
        [int]$k
    )
    Set-ArmEnvironment -arm $arm

    $beforePath = Get-NewestSummaryStamp -suite $suite

    Write-Host ""
    Write-Host ("--- arm={0} suite={1} repeats={2} ---" -f $arm, $suite, $k) -ForegroundColor Cyan
    Write-Host ("    env: {0}" -f (Get-ArmEnvDescription -arm $arm)) -ForegroundColor DarkGray

    & powershell -NoProfile -ExecutionPolicy Bypass -File $RepeatScript -Suite $suite -Repeats $k 2>&1 |
        ForEach-Object {
            $text = $_
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $text = $_.ToString() }
            Write-Host ([string]$text)
        }

    # Locate the summary JSON this invocation produced: newest matching file that is
    # different from the pre-invocation newest.
    $afterPath = Get-NewestSummaryStamp -suite $suite
    if ([string]::IsNullOrWhiteSpace($afterPath)) {
        throw ("No harness-repeat summary JSON found for suite '$suite' after arm '$arm'. " +
            "Expected a new file under $RepeatSummaryDir.")
    }
    if ($afterPath -eq $beforePath) {
        throw ("harness-repeat did not produce a NEW summary for suite '$suite' / arm '$arm' " +
            "(newest is still '$afterPath'). Aborting rather than reusing a stale result.")
    }

    $summaryRaw = Get-Content -Path $afterPath -Raw -Encoding UTF8
    $summary = $summaryRaw | ConvertFrom-Json
    return [pscustomobject]@{
        arm         = $arm
        suite       = $suite
        summaryPath = $afterPath
        summary     = $summary
    }
}

# ---------------------------------------------------------------------------
# Formatting helpers.
# ---------------------------------------------------------------------------
function Format-RatePercent {
    param([double]$rate)
    return ('{0:0.0}%' -f ($rate * 100.0))
}

# "rate±stddev" cell, e.g. "83.3%±10.5%". Both numbers come straight from the
# repeat JSON's overall block (item-weighted rate; stddev over complete runs).
function Format-Cell {
    param($overall)
    $rate = [double]$overall.mean_pass_rate
    $stddev = [double]$overall.stddev_pass_rate
    return ('{0}±{1}' -f (Format-RatePercent -rate $rate), (Format-RatePercent -rate $stddev))
}

# Count partial runs recorded in a summary's runs[] array.
function Get-PartialRunCount {
    param($summary)
    $count = 0
    if ($null -eq $summary) { return 0 }
    if (-not ($summary.PSObject.Properties.Name -contains 'runs')) { return 0 }
    foreach ($run in @($summary.runs)) {
        if ($run.PSObject.Properties.Name -contains 'partial' -and $run.partial) {
            $count = $count + 1
        }
    }
    return $count
}

# ---------------------------------------------------------------------------
# Main.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "========================================================================" -ForegroundColor Cyan
Write-Host "MODEL INTAKE RIG" -ForegroundColor Cyan
Write-Host ("model:      {0}" -f $ModelId) -ForegroundColor Cyan
Write-Host ("gatekeeper: {0}" -f $GatekeeperModelId) -ForegroundColor Cyan
Write-Host ("suites:     {0}" -f ($Suites -join ', ')) -ForegroundColor Cyan
Write-Host ("arms:       {0}" -f ($Arms -join ', ')) -ForegroundColor Cyan
Write-Host ("repeats:    {0}" -f $Repeats) -ForegroundColor Cyan
Write-Host "========================================================================" -ForegroundColor Cyan

if (-not (Test-Path $RepeatScript)) {
    throw "harness-repeat script not found at $RepeatScript."
}

# Validate arms early so we fail before touching LM Studio.
foreach ($arm in $Arms) {
    [void](Get-ArmEnv -arm $arm)
}

# 1) Discover + patch settings into a temp dir; point ST_SETTINGS_PATH at it.
$templatePath = Resolve-SettingsTemplate -explicit $SettingsTemplate
Write-Host ("Settings template: {0}" -f $templatePath) -ForegroundColor DarkGray

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('st-model-intake-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$patchedSettingsPath = Join-Path $tempRoot 'settings.json'
New-PatchedSettings -templatePath $templatePath -modelId $ModelId -gatekeeperModelId $GatekeeperModelId -destPath $patchedSettingsPath
$env:ST_SETTINGS_PATH = $patchedSettingsPath
Write-Host ("Patched settings: {0}" -f $patchedSettingsPath) -ForegroundColor DarkGray
Write-Host ("ST_SETTINGS_PATH -> {0}" -f $patchedSettingsPath) -ForegroundColor DarkGray

# 2) Ensure LM Studio has the model loaded.
Assert-LmsAvailable
Ensure-ModelLoaded -modelId $ModelId

# 3) Run the matrix: arm x suite.
# results[suite][arm] = the collected pscustomobject from Invoke-ArmSuite.
$results = @{}
foreach ($suite in $Suites) {
    $results[$suite] = @{}
}

foreach ($arm in $Arms) {
    foreach ($suite in $Suites) {
        $collected = Invoke-ArmSuite -arm $arm -suite $suite -k $Repeats
        $results[$suite][$arm] = $collected
    }
}

# 4) Build the scorecard object.
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$sanitizedModel = ($ModelId -replace '[^A-Za-z0-9._-]', '_')
$outDir = Join-Path $RepoRoot ('artifacts/model-intake/{0}-{1}' -f $stamp, $sanitizedModel)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$scoreSuites = New-Object System.Collections.ArrayList
foreach ($suite in $Suites) {
    $suiteItemCount = Get-SuiteItemCount -suite $suite
    $armCards = New-Object System.Collections.ArrayList
    foreach ($arm in $Arms) {
        $collected = $results[$suite][$arm]
        $summary = $collected.summary
        $overall = $summary.overall

        $perItem = New-Object System.Collections.ArrayList
        foreach ($item in @($summary.per_item)) {
            [void]$perItem.Add([pscustomobject]@{
                id         = $item.id
                passes     = $item.passes
                runs       = $item.runs
                mean_score = $item.mean_score
            })
        }

        $partialCount = Get-PartialRunCount -summary $summary

        [void]$armCards.Add([pscustomobject]@{
            arm                = $arm
            env                = (Get-ArmEnvDescription -arm $arm)
            repeats            = $summary.repeats
            mean_pass_rate     = $overall.mean_pass_rate
            stddev_pass_rate   = $overall.stddev_pass_rate
            aggregation        = $overall.aggregation
            total_passes       = $overall.total_passes
            total_item_results = $overall.total_item_results
            stddev_runs_used   = $overall.stddev_runs_used
            partial_run_count  = $partialCount
            per_item           = @($perItem.ToArray())
            summary_path       = $collected.summaryPath
        })
    }
    [void]$scoreSuites.Add([pscustomobject]@{
        suite      = $suite
        item_count = $suiteItemCount
        arms       = @($armCards.ToArray())
    })
}

# 5) Recommendation per suite.
# Rule: pick the arm with the highest item-weighted mean pass-rate, BUT prefer
# baseline on ties within 1 stddev - scaffolding must earn its latency. "Within 1
# stddev" uses the winning arm's stddev as the tolerance band.
$recommendations = New-Object System.Collections.ArrayList
foreach ($suiteCard in $scoreSuites) {
    $arms = @($suiteCard.arms)

    # Best arm by raw rate.
    $best = $null
    foreach ($armCard in $arms) {
        if ($null -eq $best) { $best = $armCard; continue }
        if ([double]$armCard.mean_pass_rate -gt [double]$best.mean_pass_rate) { $best = $armCard }
    }

    # Baseline card (if baseline was one of the arms tested).
    $baselineCard = $null
    foreach ($armCard in $arms) {
        if ($armCard.arm -eq 'baseline') { $baselineCard = $armCard }
    }

    $chosen = $best
    $reason = ('highest item-weighted pass-rate ({0})' -f (Format-RatePercent -rate ([double]$best.mean_pass_rate)))

    if ($null -ne $baselineCard -and $best.arm -ne 'baseline') {
        # Tolerance band = winning arm's stddev. If baseline is within that band of
        # the winner, prefer baseline (cheaper, lower latency, no extra sampling).
        $band = [double]$best.stddev_pass_rate
        $gap = [double]$best.mean_pass_rate - [double]$baselineCard.mean_pass_rate
        if ($gap -le $band) {
            $chosen = $baselineCard
            $reason = ('baseline is within 1 stddev of the best arm ({0}); gap {1} <= band {2}. ' +
                'Scaffolding must earn its latency, so baseline wins the tie.') -f `
                $best.arm, (Format-RatePercent -rate $gap), (Format-RatePercent -rate $band)
        }
        else {
            $reason = ('{0} beats baseline by {1}, more than its {2} stddev band' -f `
                $best.arm, (Format-RatePercent -rate $gap), (Format-RatePercent -rate $band))
        }
    }

    # Flag any arm on this suite that had partial runs.
    $flagged = New-Object System.Collections.ArrayList
    foreach ($armCard in $arms) {
        if ([int]$armCard.partial_run_count -gt 0) {
            [void]$flagged.Add([pscustomobject]@{
                arm               = $armCard.arm
                partial_run_count = $armCard.partial_run_count
                repeats           = $armCard.repeats
            })
        }
    }

    [void]$recommendations.Add([pscustomobject]@{
        suite            = $suiteCard.suite
        recommended_arm  = $chosen.arm
        env              = $chosen.env
        reason           = $reason
        chosen_rate      = $chosen.mean_pass_rate
        chosen_stddev    = $chosen.stddev_pass_rate
        best_arm         = $best.arm
        best_rate        = $best.mean_pass_rate
        partials_flagged = @($flagged.ToArray())
    })
}

$scorecard = [pscustomobject]@{
    generated_utc     = (Get-Date).ToUniversalTime().ToString('O')
    model_id          = $ModelId
    gatekeeper_id     = $GatekeeperModelId
    repeats           = $Repeats
    arms              = @($Arms)
    suites_requested  = @($Suites)
    settings_template = $templatePath
    patched_settings  = $patchedSettingsPath
    honesty_note      = ('All numbers item-weighted over K complete runs; suites are closed-book ' +
        'compute/reasoning probes unless labeled open-book. Partial-run counts are surfaced per cell.')
    suites            = @($scoreSuites.ToArray())
    recommendations   = @($recommendations.ToArray())
}

# 6) Write scorecard.json (UTF-8 no BOM, to match harness-repeat's convention).
$scorecardPath = Join-Path $outDir 'scorecard.json'
$scorecardJson = $scorecard | ConvertTo-Json -Depth 12
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($scorecardPath, $scorecardJson, $utf8NoBom)

# 7) Write report.md - compact table (rows=suites, cols=arms), recommendations.
$md = New-Object System.Collections.ArrayList
[void]$md.Add('# Model intake report')
[void]$md.Add('')
[void]$md.Add(('- Model: `{0}`' -f $ModelId))
[void]$md.Add(('- Gatekeeper: `{0}`' -f $GatekeeperModelId))
[void]$md.Add(('- Repeats (K): {0}' -f $Repeats))
[void]$md.Add(('- Arms: {0}' -f ($Arms -join ', ')))
[void]$md.Add(('- Generated (UTC): {0}' -f $scorecard.generated_utc))
[void]$md.Add('')
[void]$md.Add(('> All numbers item-weighted over K complete runs; suites are closed-book ' +
    'compute/reasoning probes unless labeled open-book.'))
[void]$md.Add('')
[void]$md.Add('Each cell is `pass-rate±stddev` (item-weighted rate; stddev over complete runs). ' +
    'A `(P:n)` suffix flags n partial run(s) folded into that cell - treat those numbers with suspicion.')
[void]$md.Add('')

# Results table.
$headerCells = New-Object System.Collections.ArrayList
[void]$headerCells.Add('suite (items)')
foreach ($arm in $Arms) { [void]$headerCells.Add($arm) }
[void]$md.Add('| ' + (($headerCells.ToArray()) -join ' | ') + ' |')

$sepCells = New-Object System.Collections.ArrayList
[void]$sepCells.Add('---')
foreach ($arm in $Arms) { [void]$sepCells.Add('---') }
[void]$md.Add('| ' + (($sepCells.ToArray()) -join ' | ') + ' |')

foreach ($suiteCard in $scoreSuites) {
    $rowCells = New-Object System.Collections.ArrayList
    [void]$rowCells.Add(('{0} ({1})' -f $suiteCard.suite, $suiteCard.item_count))
    foreach ($arm in $Arms) {
        $armCard = $null
        foreach ($c in @($suiteCard.arms)) { if ($c.arm -eq $arm) { $armCard = $c } }
        if ($null -eq $armCard) {
            [void]$rowCells.Add('n/a')
            continue
        }
        $overall = [pscustomobject]@{
            mean_pass_rate   = $armCard.mean_pass_rate
            stddev_pass_rate = $armCard.stddev_pass_rate
        }
        $cell = Format-Cell -overall $overall
        if ([int]$armCard.partial_run_count -gt 0) {
            $cell = ('{0} (P:{1})' -f $cell, $armCard.partial_run_count)
        }
        [void]$rowCells.Add($cell)
    }
    [void]$md.Add('| ' + (($rowCells.ToArray()) -join ' | ') + ' |')
}

[void]$md.Add('')
[void]$md.Add('## Recommended config')
[void]$md.Add('')
[void]$md.Add('Rule: highest item-weighted pass-rate wins, but baseline wins ties within 1 stddev - ' +
    'scaffolding must earn its extra latency.')
[void]$md.Add('')

foreach ($rec in @($recommendations.ToArray())) {
    [void]$md.Add(('### {0}' -f $rec.suite))
    [void]$md.Add('')
    [void]$md.Add(('- Recommended arm: **{0}**' -f $rec.recommended_arm))
    [void]$md.Add(('- Rate: {0}±{1} (K={2})' -f `
        (Format-RatePercent -rate ([double]$rec.chosen_rate)), `
        (Format-RatePercent -rate ([double]$rec.chosen_stddev)), `
        $Repeats))
    [void]$md.Add(('- Reason: {0}' -f $rec.reason))
    [void]$md.Add(('- Env vars to set: `{0}`' -f $rec.env))
    if (@($rec.partials_flagged).Count -gt 0) {
        $flagParts = New-Object System.Collections.ArrayList
        foreach ($f in @($rec.partials_flagged)) {
            [void]$flagParts.Add(('{0}: {1}/{2} run(s) partial' -f $f.arm, $f.partial_run_count, $f.repeats))
        }
        [void]$md.Add(('- WARNING - partial runs on this suite: {0}' -f (($flagParts.ToArray()) -join '; ')))
    }
    [void]$md.Add('')
}

$reportPath = Join-Path $outDir 'report.md'
$mdText = ($md.ToArray()) -join "`r`n"
$mdText | Out-File -FilePath $reportPath -Encoding utf8

Write-Host ""
Write-Host "========================================================================" -ForegroundColor Green
Write-Host ("Scorecard: {0}" -f $scorecardPath) -ForegroundColor Green
Write-Host ("Report:    {0}" -f $reportPath) -ForegroundColor Green
Write-Host "========================================================================" -ForegroundColor Green

exit 0
