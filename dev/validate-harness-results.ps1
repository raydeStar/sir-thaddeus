param(
    [string]$RunId = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

function Get-LatestRunId {
    param([string]$HarnessRoot)

    $runs = Get-ChildItem $HarnessRoot -Directory |
        Where-Object { $_.Name -match '^\d{8}_\d{6}$' } |
        Sort-Object Name -Descending

    if (-not $runs -or $runs.Count -eq 0) {
        throw "No harness run directories found under $HarnessRoot"
    }

    # Prefer latest comprehensive run with many score files.
    $ranked = foreach ($run in $runs | Select-Object -First 60) {
        $count = (Get-ChildItem $run.FullName -Recurse -Filter score.json -File -ErrorAction SilentlyContinue).Count
        [pscustomobject]@{ RunId = $run.Name; ScoreFiles = $count }
    }

    return ($ranked |
        Sort-Object -Property @{Expression='ScoreFiles';Descending=$true}, @{Expression='RunId';Descending=$true} |
        Select-Object -First 1).RunId
}

function Get-Preview {
    param([string]$FinalPath)

    if (-not (Test-Path $FinalPath)) {
        return "(no final.txt)"
    }

    $previewLines = Get-Content $FinalPath -TotalCount 4
    $preview = ($previewLines -join " ").Trim()
    if ($preview.Length -gt 260) {
        $preview = $preview.Substring(0, 260) + "..."
    }

    return $preview
}

function Get-ValidationFlags {
    param(
        [string]$Suite,
        [string]$Test,
        [string]$Preview,
        [bool]$HardPass,
        [double]$FinalScore,
        [double]$MinScore
    )

    $flags = New-Object System.Collections.Generic.List[string]
    $p = $Preview.ToLowerInvariant()

    if (-not $HardPass -or $FinalScore -lt $MinScore) {
        $flags.Add("SCORE_FAIL")
        return $flags
    }

    $suspiciousPhrases = @(
        "search findings were inconclusive",
        "web search returned no usable",
        "based on general knowledge",
        "cannot verify live",
        "i can't use a live web search",
        "couldn't get enough verified listings",
        "i attempted file_read on that path",
        "i am unable to pull live news",
        "can't access",
        "cannot access"
    )

    foreach ($phrase in $suspiciousPhrases) {
        if ($p.Contains($phrase)) {
            $flags.Add("SUSPECT_FALLBACK_TEXT")
            break
        }
    }

    if ($suite -eq "existence" -and $test -eq "existence_existing_iphone15" -and $p.Contains("likely does not exist")) {
        $flags.Add("FACT_RISK_IPHONE15")
    }

    if ($suite -eq "quality" -and ($test -eq "quality_error_tone" -or $test -eq "quality_cross_contamination_guard") -and $p.Contains("that path")) {
        $flags.Add("QUALITY_PATH_PLACEHOLDER")
    }

    if ($suite -eq "web-search" -and ($test -like "web_local_business_*") -and $p.Contains("based on general knowledge")) {
        $flags.Add("LOCAL_BUSINESS_NOT_GROUNDED")
    }

    return $flags
}

function ConvertTo-RubricThreshold {
    param([double]$MinScore)

    if ($MinScore -le 0) {
        return 0.85
    }

    if ($MinScore -gt 1) {
        return [Math]::Min([Math]::Max($MinScore / 10.0, 0.0), 1.0)
    }

    return [Math]::Min([Math]::Max($MinScore, 0.0), 1.0)
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$harnessRoot = Join-Path $repoRoot "artifacts/harness"

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = Get-LatestRunId -HarnessRoot $harnessRoot
}

$runRoot = Join-Path $harnessRoot $RunId
if (-not (Test-Path $runRoot)) {
    throw "Run not found: $runRoot"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "artifacts/harness_validation_$RunId.txt"
}

$scoreFiles = Get-ChildItem $runRoot -Recurse -Filter score.json -File | Sort-Object FullName
if (-not $scoreFiles -or $scoreFiles.Count -eq 0) {
    throw "No score.json files found in run: $RunId"
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Run: $RunId")
$lines.Add("Generated: $(Get-Date -Format o)")
$lines.Add("Tests: $($scoreFiles.Count)")
$lines.Add("")

$statusCounts = @{ PASS_OK = 0; PASS_REVIEW = 0; FAIL = 0 }

foreach ($scoreFile in $scoreFiles) {
    $iterDir = Split-Path $scoreFile.FullName -Parent
    $testDir = Split-Path $iterDir -Parent
    $suiteDir = Split-Path $testDir -Parent
    $suite = Split-Path $suiteDir -Leaf
    $test = Split-Path $testDir -Leaf

    $score = Get-Content $scoreFile.FullName -Raw | ConvertFrom-Json

    $minScore = 0.0
    $inputPath = Join-Path $iterDir "input.json"
    if (Test-Path $inputPath) {
        try {
            $input = Get-Content $inputPath -Raw | ConvertFrom-Json
            if ($null -ne $input.test.min_score) {
                $minScore = [double]$input.test.min_score
            }
        }
        catch { }
    }

    $finalPath = Join-Path $iterDir "final.txt"
    $preview = Get-Preview -FinalPath $finalPath

    $minScore = ConvertTo-RubricThreshold -MinScore $minScore

    $hardPass = if ($null -ne $score.hard_pass) { [bool]$score.hard_pass } else { ($score.hardGateFailures.Count -eq 0) }
    $finalScore = if ($null -ne $score.overallScore) { [double]$score.overallScore } else { [double]$score.final_score }
    $isFail = (-not $hardPass) -or ($finalScore -lt $minScore)

    $flags = Get-ValidationFlags -Suite $suite -Test $test -Preview $preview -HardPass $hardPass -FinalScore $finalScore -MinScore $minScore

    $status = "PASS_OK"
    if ($isFail) {
        $status = "FAIL"
    }
    elseif ($flags.Count -gt 0) {
        $status = "PASS_REVIEW"
    }

    $statusCounts[$status]++

    $lines.Add("[$status] $suite/$test | score=$finalScore | min=$minScore")
    if ($flags.Count -gt 0) {
        $lines.Add("  flags: $($flags -join ', ')")
    }
    $lines.Add("  preview: $preview")
    $hardFailures = if ($null -ne $score.hardGateFailures) { $score.hardGateFailures } else { $score.hard_failures }
    if (-not $hardPass -and $null -ne $hardFailures -and $hardFailures.Count -gt 0) {
        $lines.Add("  hard_failures: $($hardFailures -join '; ')")
    }
    $lines.Add("")
}

$header = @(
    "Summary:",
    "  PASS_OK: $($statusCounts.PASS_OK)",
    "  PASS_REVIEW: $($statusCounts.PASS_REVIEW)",
    "  FAIL: $($statusCounts.FAIL)",
    ""
)

$all = New-Object System.Collections.Generic.List[string]
$header | ForEach-Object { $all.Add($_) }
$lines | ForEach-Object { $all.Add($_) }

Set-Content -Path $OutputPath -Value $all -Encoding UTF8
Write-Output "Wrote validation report: $OutputPath"
