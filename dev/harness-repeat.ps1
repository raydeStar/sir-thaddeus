#requires -Version 5.1
<#
.SYNOPSIS
    Runs a harness suite K times and aggregates per-item pass counts and mean scores.

.DESCRIPTION
    The model under test is nondeterministic even at temperature 0, so a single
    harness run tells us almost nothing. This wrapper runs dev/harness.ps1 K times,
    reads each item's iter-01/score.json from the newest artifacts directory produced
    by each run, and aggregates:
      - per-item pass count (e.g. "probe_py_collatz_steps 4/5") and mean score
      - overall mean pass-rate across runs and its standard deviation
    Results are printed as an aligned table and written to a machine-readable JSON
    summary under artifacts/harness-repeat/.

    This script only AGGREGATES harness output. It embeds no expected answers.

.EXAMPLE
    dev/harness-repeat.ps1 -Suite python-probe -Repeats 5
.EXAMPLE
    dev/harness-repeat.ps1 -Suite python-probe -Repeats 5 -SkipBuild
    # Only when the Debug harness and headless-runtime assemblies are already current.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Suite,

    [int]$Repeats = 5,

    [switch]$SkipBuild,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraHarnessArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$HarnessProject = Join-Path $RepoRoot "tools/SirThaddeus.Harness/SirThaddeus.Harness.csproj"
$HarnessAssembly = Join-Path $RepoRoot "tools/SirThaddeus.Harness/bin/Debug/net10.0/SirThaddeus.Harness.dll"
$RuntimeProject = Join-Path $RepoRoot "apps/headless-runtime/SirThaddeus.HeadlessRuntime/SirThaddeus.HeadlessRuntime.csproj"
$RuntimeAssembly = Join-Path $RepoRoot "apps/headless-runtime/SirThaddeus.HeadlessRuntime/bin/Debug/net10.0/SirThaddeus.HeadlessRuntime.dll"

function Invoke-CheckedBuild {
    param([string]$project, [string]$label)
    Write-Host ("Preparing {0}..." -f $label) -ForegroundColor DarkGray
    & dotnet build $project -c Debug
    if ($LASTEXITCODE -ne 0) {
        throw ("{0} build failed with exit code {1}." -f $label, $LASTEXITCODE)
    }
}

$buildStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
if (-not $SkipBuild) {
    # Build once for the whole campaign. The old path entered `dotnet run` and
    # then rebuilt the headless runtime inside every repeat process.
    Invoke-CheckedBuild -project $HarnessProject -label 'harness'
    Invoke-CheckedBuild -project $RuntimeProject -label 'headless runtime'
}
$buildStopwatch.Stop()

if (-not (Test-Path $HarnessAssembly) -or -not (Test-Path $RuntimeAssembly)) {
    throw 'Harness/runtime assemblies are missing. Run without -SkipBuild once to prepare them.'
}

# The runtime assembly was prepared above, so child harness processes may skip
# their defensive in-process build and launch the DLL directly.
$env:ST_HARNESS_SKIP_RUNTIME_BUILD = '1'

$HarnessRoot = Join-Path $RepoRoot "artifacts/harness"

# Base args every iteration uses. Extra args (if any) are appended.
$baseArgs = @('run', '--suite', $Suite, '--judge', 'none', '--max-iters', '1')
if ($null -ne $ExtraHarnessArgs -and $ExtraHarnessArgs.Count -gt 0) {
    $baseArgs = $baseArgs + $ExtraHarnessArgs
}

# Track the newest existing harness dir so we can detect the one each run creates.
function Get-NewestSuiteDir {
    param([string]$root, [string]$suite)
    if (-not (Test-Path $root)) { return $null }
    $candidates = Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending
    foreach ($c in $candidates) {
        $suiteDir = Join-Path $c.FullName $suite
        if (Test-Path $suiteDir) {
            return [pscustomobject]@{ TimestampDir = $c.FullName; SuiteDir = $suiteDir }
        }
    }
    return $null
}

# Parse [PASS]/[FAIL] lines from captured stdout as a fallback source of truth.
# Example line: [PASS] python-probe/probe_py_collatz_steps score=0.90 min=0.50
function Parse-HarnessStdout {
    param([string[]]$lines, [string]$suite)
    $results = @{}
    if ($null -eq $lines) { return $results }
    $pattern = '^\[(PASS|FAIL)\]\s+' + [regex]::Escape($suite) + '/(\S+)\s+score=([0-9.]+)'
    foreach ($line in $lines) {
        $m = [regex]::Match($line, $pattern)
        if ($m.Success) {
            $verdict = $m.Groups[1].Value
            $id = $m.Groups[2].Value
            $score = [double]$m.Groups[3].Value
            $isPass = $false
            if ($verdict -eq 'PASS') { $isPass = $true }
            $results[$id] = [pscustomobject]@{ Passed = $isPass; Score = $score }
        }
    }
    return $results
}

# Read per-item results from a suite dir's score.json files.
function Read-ScoreJson {
    param([string]$suiteDir)
    $results = @{}
    if (-not (Test-Path $suiteDir)) { return $results }
    $itemDirs = Get-ChildItem -Path $suiteDir -Directory -ErrorAction SilentlyContinue
    foreach ($itemDir in $itemDirs) {
        $scorePath = Join-Path $itemDir.FullName "iter-01/score.json"
        if (-not (Test-Path $scorePath)) { continue }
        try {
            $json = Get-Content -Path $scorePath -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            continue
        }
        $id = $itemDir.Name
        if ($json.PSObject.Properties.Name -contains 'testId' -and $json.testId) {
            $id = [string]$json.testId
        }
        $isPass = $false
        if ($json.PSObject.Properties.Name -contains 'passed') {
            $isPass = [bool]$json.passed
        }
        elseif ($json.PSObject.Properties.Name -contains 'status') {
            if ($json.status -eq 'pass') { $isPass = $true }
        }
        $score = 0.0
        if ($json.PSObject.Properties.Name -contains 'final_score' -and $null -ne $json.final_score) {
            $score = [double]$json.final_score
        }
        elseif ($json.PSObject.Properties.Name -contains 'overallScore' -and $null -ne $json.overallScore) {
            $score = [double]$json.overallScore
        }
        $results[$id] = [pscustomobject]@{ Passed = $isPass; Score = $score }
    }
    return $results
}

Write-Host ""
Write-Host "harness-repeat: suite=$Suite repeats=$Repeats" -ForegroundColor Cyan
Write-Host "base args: $($baseArgs -join ' ')" -ForegroundColor DarkGray
Write-Host ""

# aggregates[id] = @{ Passes = int; ScoreSum = double; Runs = int }
$aggregates = @{}
$runDirs = New-Object System.Collections.ArrayList
$perRunPassRates = New-Object System.Collections.ArrayList
$completeRunPassRates = New-Object System.Collections.ArrayList
$runsDetail = New-Object System.Collections.ArrayList
$totalPasses = 0
$totalItemResults = 0

# Expected item count = suite YAML count. A run that scores fewer items died
# mid-suite (runtime crash, LM Studio hiccup); it must be flagged loudly or a
# partial run silently biases the aggregate.
$suiteDefDir = Join-Path $RepoRoot ("tools/SirThaddeus.Harness/Suites/{0}" -f $Suite)
$expectedItems = 0
if (Test-Path $suiteDefDir) {
    $expectedItems = @(Get-ChildItem -Path $suiteDefDir -Filter *.yaml -File -ErrorAction SilentlyContinue).Count
}

# Sandbox canary: a trivial python_eval container must respond fast before a
# run may count. On 2026-07-03 the Docker daemon wedged mid-campaign and every
# python_eval call silently timed out for five hours of "measurement" — the
# instrument itself must be verified, per run, or a dead tool poisons the data.
function Test-SandboxCanary {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $out = ""
    try {
        $out = ("print(6*7)" | & docker run --rm -i --init --network none python:3.11-slim python -I -q - 2>&1 | Out-String).Trim()
    }
    catch { $out = "" }
    $sw.Stop()
    $ok = ($out -eq "42") -and ($sw.Elapsed.TotalSeconds -lt 10)
    return [pscustomobject]@{ Ok = $ok; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2); Output = $out }
}

$suiteUsesPython = $false
if (Test-Path $suiteDefDir) {
    $suiteUsesPython = @(Get-ChildItem -Path $suiteDefDir -Filter *.yaml -File |
        Where-Object { (Get-Content $_.FullName -Raw) -match 'python_eval' }).Count -gt 0
}

for ($i = 1; $i -le $Repeats; $i++) {
    Write-Host "=== Run $i / $Repeats ===" -ForegroundColor Yellow

    if ($suiteUsesPython) {
        $canary = Test-SandboxCanary
        if (-not $canary.Ok) {
            Write-Host ("ABORT: sandbox canary FAILED before run {0} (took {1}s, output '{2}'). The python sandbox is dead or wedged - measuring now would produce garbage. Restart Docker Desktop and retry." -f $i, $canary.Seconds, $canary.Output) -ForegroundColor Red
            exit 2
        }
        Write-Host ("  sandbox canary ok ({0}s)" -f $canary.Seconds) -ForegroundColor DarkGray
    }

    $before = Get-NewestSuiteDir -root $HarnessRoot -suite $Suite
    $beforeStamp = ''
    if ($null -ne $before) { $beforeStamp = $before.TimestampDir }

    # Invoke the harness. Capture stdout for the fallback parser while echoing it.
    $stdoutLines = New-Object System.Collections.ArrayList
    $runStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet exec $HarnessAssembly @baseArgs 2>&1 | ForEach-Object {
        $text = $_
        if ($_ -is [System.Management.Automation.ErrorRecord]) { $text = $_.ToString() }
        Write-Host $text
        [void]$stdoutLines.Add([string]$text)
    }
    $harnessExitCode = $LASTEXITCODE
    $runStopwatch.Stop()
    if ($harnessExitCode -ne 0) {
        Write-Host ("  harness exited with code {0}; collecting any completed results." -f $harnessExitCode) -ForegroundColor DarkYellow
    }

    # Find the suite dir this run produced (newest, and newer than the pre-run one).
    $after = Get-NewestSuiteDir -root $HarnessRoot -suite $Suite
    $runResults = @{}
    $usedFallback = $false

    if ($null -ne $after -and $after.TimestampDir -ne $beforeStamp) {
        [void]$runDirs.Add($after.SuiteDir)
        $runResults = Read-ScoreJson -suiteDir $after.SuiteDir
    }

    # Fall back to stdout parsing if score.json yielded nothing usable.
    if ($runResults.Count -eq 0) {
        $usedFallback = $true
        $runResults = Parse-HarnessStdout -lines $stdoutLines.ToArray() -suite $Suite
        if ($null -ne $after) {
            $existing = $false
            foreach ($rd in $runDirs) { if ($rd -eq $after.SuiteDir) { $existing = $true } }
            if (-not $existing -and $after.TimestampDir -ne $beforeStamp) { [void]$runDirs.Add($after.SuiteDir) }
        }
    }

    if ($runResults.Count -eq 0) {
        Write-Host "  WARNING: run $i produced no readable results (score.json and stdout both empty)." -ForegroundColor Red
        [void]$perRunPassRates.Add(0.0)
        continue
    }

    if ($usedFallback) {
        Write-Host "  (note: used stdout [PASS]/[FAIL] fallback for run $i)" -ForegroundColor DarkYellow
    }

    $runPassCount = 0
    foreach ($id in $runResults.Keys) {
        $r = $runResults[$id]
        if (-not $aggregates.ContainsKey($id)) {
            $aggregates[$id] = [pscustomobject]@{ Passes = 0; ScoreSum = 0.0; Runs = 0 }
        }
        $agg = $aggregates[$id]
        $agg.Runs = $agg.Runs + 1
        $agg.ScoreSum = $agg.ScoreSum + $r.Score
        if ($r.Passed) {
            $agg.Passes = $agg.Passes + 1
            $runPassCount = $runPassCount + 1
        }
    }

    $runItemCount = $runResults.Count
    $runPassRate = 0.0
    if ($runItemCount -gt 0) { $runPassRate = [double]$runPassCount / [double]$runItemCount }
    [void]$perRunPassRates.Add($runPassRate)
    $totalPasses = $totalPasses + $runPassCount
    $totalItemResults = $totalItemResults + $runItemCount

    $isPartial = $false
    if ($expectedItems -gt 0 -and $runItemCount -lt $expectedItems) {
        $isPartial = $true
        Write-Host ("  WARNING: run {0} is PARTIAL - scored {1}/{2} expected items. The harness died mid-suite; treat this run's numbers with suspicion." -f $i, $runItemCount, $expectedItems) -ForegroundColor Red
    }
    else {
        # Only complete runs contribute to run-to-run variance: a partial run's
        # rate is over a different item subset and would distort the spread.
        [void]$completeRunPassRates.Add($runPassRate)
    }
    [void]$runsDetail.Add([pscustomobject]@{
        run       = $i
        items     = $runItemCount
        expected  = $expectedItems
        partial   = $isPartial
        pass_rate = [math]::Round($runPassRate, 4)
        duration_seconds = [math]::Round($runStopwatch.Elapsed.TotalSeconds, 2)
    })

    Write-Host ("  run {0}: {1}/{2} passed (pass-rate {3:P0})" -f $i, $runPassCount, $runItemCount, $runPassRate) -ForegroundColor Green
    Write-Host ""
}

$partialRuns = @($runsDetail | Where-Object { $_.partial })
if ($partialRuns.Count -gt 0) {
    Write-Host ("NOTE: {0} of {1} run(s) were PARTIAL (harness died mid-suite). Aggregate numbers below include them; per-item runs counters show true coverage." -f $partialRuns.Count, $Repeats) -ForegroundColor Red
}

# ---- Aggregate + report ----
$ids = $aggregates.Keys | Sort-Object

if ($aggregates.Count -eq 0) {
    Write-Host "No results collected across $Repeats runs. Nothing to aggregate." -ForegroundColor Red
    exit 1
}

# Headline mean pass-rate is ITEM-WEIGHTED (total passes / total item-results):
# a simple average of per-run rates would give a partial run's subset the same
# weight as a complete run and bias the aggregate. The run-to-run stddev is
# computed over COMPLETE runs only, for the same reason.
$meanPassRate = 0.0
if ($totalItemResults -gt 0) {
    $meanPassRate = [double]$totalPasses / [double]$totalItemResults
}
$rates = @($completeRunPassRates.ToArray())
if ($rates.Count -eq 0) {
    Write-Host "NOTE: no COMPLETE runs - stddev falls back to partial-run rates and is unreliable." -ForegroundColor Red
    $rates = @($perRunPassRates.ToArray())
}
$stddevPassRate = 0.0
if ($rates.Count -gt 0) {
    $ratesMean = 0.0
    foreach ($r in $rates) { $ratesMean = $ratesMean + $r }
    $ratesMean = $ratesMean / $rates.Count
    $varSum = 0.0
    foreach ($r in $rates) { $varSum = $varSum + [math]::Pow($r - $ratesMean, 2) }
    $stddevPassRate = [math]::Sqrt($varSum / $rates.Count)
}

# Column widths.
$idWidth = 4
foreach ($id in $ids) { if ($id.Length -gt $idWidth) { $idWidth = $id.Length } }

Write-Host ""
Write-Host "========================================================================" -ForegroundColor Cyan
Write-Host ("Aggregate over {0} run(s) - suite: {1}" -f $Repeats, $Suite) -ForegroundColor Cyan
Write-Host "========================================================================" -ForegroundColor Cyan

$header = ("{0}  {1,-7}  {2,-10}" -f ("item".PadRight($idWidth)), "passes", "mean_score")
Write-Host $header
Write-Host ("-" * $header.Length)

$perItem = New-Object System.Collections.ArrayList
foreach ($id in $ids) {
    $agg = $aggregates[$id]
    $meanScore = 0.0
    if ($agg.Runs -gt 0) { $meanScore = $agg.ScoreSum / $agg.Runs }
    $passStr = "{0}/{1}" -f $agg.Passes, $agg.Runs
    Write-Host ("{0}  {1,-7}  {2,-10:F3}" -f ($id.PadRight($idWidth)), $passStr, $meanScore)
    [void]$perItem.Add([pscustomobject]@{
        id         = $id
        passes     = $agg.Passes
        runs       = $agg.Runs
        mean_score = [math]::Round($meanScore, 4)
    })
}

Write-Host ("-" * $header.Length)
Write-Host ("mean pass-rate: {0:P1} (item-weighted, {1}/{2})   run-to-run stddev: {3:P1} (over {4} complete run(s))" -f $meanPassRate, $totalPasses, $totalItemResults, $stddevPassRate, $rates.Count) -ForegroundColor Green
Write-Host ""

# ---- Machine-readable JSON summary ----
$summaryDir = Join-Path $RepoRoot "artifacts/harness-repeat"
if (-not (Test-Path $summaryDir)) {
    New-Item -ItemType Directory -Path $summaryDir -Force | Out-Null
}
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$summaryPath = Join-Path $summaryDir ("{0}-{1}.json" -f $stamp, $Suite)

$summary = [pscustomobject]@{
    suite    = $Suite
    repeats  = $Repeats
    per_item = @($perItem.ToArray())
    overall  = [pscustomobject]@{
        mean_pass_rate     = [math]::Round($meanPassRate, 4)
        aggregation        = 'item_weighted'
        total_passes       = $totalPasses
        total_item_results = $totalItemResults
        stddev_pass_rate   = [math]::Round($stddevPassRate, 4)
        stddev_runs_used   = $rates.Count
        build_seconds      = [math]::Round($buildStopwatch.Elapsed.TotalSeconds, 2)
        campaign_seconds   = [math]::Round((($runsDetail | Measure-Object -Property duration_seconds -Sum).Sum), 2)
    }
    runs     = @($runsDetail.ToArray())
    run_dirs = @($runDirs.ToArray())
}

$json = $summary | ConvertTo-Json -Depth 6
# Write UTF-8 WITHOUT a BOM. PowerShell 5.1's `Out-File -Encoding utf8`
# emits a BOM, which trips strict JSON parsers (e.g. python's json.load).
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($summaryPath, $json, $utf8NoBom)
Write-Host ("JSON summary written to: {0}" -f $summaryPath) -ForegroundColor Cyan

exit 0
