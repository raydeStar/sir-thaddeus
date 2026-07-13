[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$RawRun,
    [Parameter(Mandatory)] [string]$CurrentRun,
    [Parameter(Mandatory)] [string]$CandidateRun,
    [ValidateRange(0, 1000)] [int]$MinimumLift = 2,
    [ValidateRange(0, 1000)] [int]$MaxInvalid = 1
)

$ErrorActionPreference = "Stop"

function Read-Run([string]$Path) {
    $resolved = Resolve-Path -LiteralPath $Path
    return Get-Content -Raw -LiteralPath $resolved | ConvertFrom-Json
}

function Measure-Run($Run, [string]$Name) {
    $cases = @($Run.cases)
    $correct = @($cases | Where-Object passed).Count
    $valid = @($cases | Where-Object is_valid).Count
    $duration = ([datetime]$Run.ended_at - [datetime]$Run.started_at).TotalSeconds
    [pscustomobject]@{
        Name = $Name
        Correct = $correct
        Total = $cases.Count
        Accuracy = if ($cases.Count) { $correct / $cases.Count } else { 0 }
        Valid = $valid
        Invalid = $cases.Count - $valid
        Seconds = [math]::Round($duration, 1)
    }
}

function Compare-Runs($Left, $Right, [string]$Label) {
    $leftById = @{}
    @($Left.cases) | ForEach-Object { $leftById[$_.test_id] = $_ }
    $both = $leftOnly = $rightOnly = $neither = 0
    foreach ($rightCase in @($Right.cases)) {
        $leftCase = $leftById[$rightCase.test_id]
        if ($null -eq $leftCase) { throw "Missing paired case $($rightCase.test_id) for $Label" }
        if ($leftCase.passed -and $rightCase.passed) { $both++ }
        elseif ($leftCase.passed) { $leftOnly++ }
        elseif ($rightCase.passed) { $rightOnly++ }
        else { $neither++ }
    }
    [pscustomobject]@{
        Pair = $Label
        BothCorrect = $both
        LeftOnly = $leftOnly
        RightOnly = $rightOnly
        Neither = $neither
        NetRight = $rightOnly - $leftOnly
    }
}

$raw = Read-Run $RawRun
$current = Read-Run $CurrentRun
$candidate = Read-Run $CandidateRun
$summary = @(
    Measure-Run $raw "Raw"
    Measure-Run $current "Current Thaddeus"
    Measure-Run $candidate "Candidate"
)
$summary | Format-Table Name,Correct,Total,@{Label="Accuracy";Expression={"{0:P1}" -f $_.Accuracy}},Valid,Invalid,Seconds -AutoSize

@(
    Compare-Runs $raw $candidate "Raw -> Candidate"
    Compare-Runs $current $candidate "Current -> Candidate"
) | Format-Table -AutoSize

$rawCorrect = $summary[0].Correct
$currentCorrect = $summary[1].Correct
$candidateCorrect = $summary[2].Correct
$gate = $candidateCorrect -ge ([math]::Max($rawCorrect, $currentCorrect) + $MinimumLift) -and
        $summary[2].Invalid -le $MaxInvalid
Write-Host "Promotion gate: $gate (candidate must beat both controls by at least $MinimumLift correct case(s) and have <=$MaxInvalid invalid response(s))"
if (-not $gate) { exit 2 }
