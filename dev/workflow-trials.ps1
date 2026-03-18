#requires -Version 5.1
<#
.SYNOPSIS
    Sir Thaddeus Trials — scored E2E workflow test suite.
    Launches headless runtime once, runs 7 trial prompts, scores each across
    observable telemetry dimensions, and outputs a scorecard.

.DESCRIPTION
    Each trial is scored 0-100 across these dimensions:
      - Pipeline:    Did checklist/narration/confidence/audit fire correctly?
      - Tools:       Were tool calls made (or correctly omitted)?
      - Retry:       Did retry gate / ladder behave sensibly?
      - Confidence:  Was confidence band reported and reasonable?
      - Completeness: Did run.completed carry all required fields?
    Individual dimension scores are weighted into a trial score,
    then all trial scores roll up into an overall suite score.

.NOTES
    Depends on a running tools-enabled headless runtime, OR will build+launch one.
    Settings are temporarily patched to enable all workflow features.
#>

param(
    [int]$Port = 5391,
    [int]$TimeoutSec = 180,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Helpers ───────────────────────────────────────────────────────────────────

function Set-OrAddProperty {
    param(
        [Parameter(Mandatory)][object]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][object]$Value
    )
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    } else {
        $Object.$Name = $Value
    }
}

function Get-SafeString {
    param([object]$Value, [string]$Default = "")
    if ($null -eq $Value) { return $Default }
    $s = [string]$Value
    if ([string]::IsNullOrWhiteSpace($s)) { return $Default }
    return $s
}

function Write-Hdr {
    param([string]$Text, [ConsoleColor]$Color = 'Cyan')
    Write-Host ""
    Write-Host ("=" * 72) -ForegroundColor $Color
    Write-Host "  $Text" -ForegroundColor $Color
    Write-Host ("=" * 72) -ForegroundColor $Color
}

function Write-Dim {
    param([string]$Label, [int]$Score, [int]$MaxPoints, [string]$Detail = "")
    $pct = if ($MaxPoints -gt 0) { [math]::Round(($Score / $MaxPoints) * 100) } else { 0 }
    $color = if ($pct -ge 80) { 'Green' } elseif ($pct -ge 50) { 'Yellow' } else { 'Red' }
    $bar = ("{0}/{1}" -f $Score, $MaxPoints).PadRight(8)
    $info = if ($Detail) { " ($Detail)" } else { "" }
    Write-Host ("    {0,-18} {1}  {2}%{3}" -f $Label, $bar, $pct, $info) -ForegroundColor $color
}

function Show-Scorecard {
    param([object[]]$Board, [string]$ResultDir)

    Write-Hdr "SCORECARD -- Sir Thaddeus Trials" -Color White
    Write-Host ""
    Write-Host ("  {0,-38} {1,6} {2,6} {3,6} {4,8} {5,8} {6,6} {7,5}" -f "Trial", "Score", "%", "Grade", "Band", "Reason", "Tools", "Retry") -ForegroundColor White
    Write-Host ("  " + ("-" * 90)) -ForegroundColor DarkGray

    foreach ($row in $Board) {
        $lineColor = 'White'
        if ($row.Pct -ge 80) { $lineColor = 'Green' } elseif ($row.Pct -ge 50) { $lineColor = 'Yellow' } else { $lineColor = 'Red' }
        $retryFlag = if ($row.Retried) { "Yes" } else { "No" }
        $bandShort = if ([string]::IsNullOrWhiteSpace($row.Band)) { "n/a" } else { $row.Band }
        $reasonStr = [string]$row.Reason
        $reasonShort = if ($reasonStr.Length -gt 8) { $reasonStr.Substring(0, 8) } else { $reasonStr }
        Write-Host ("  {0,-38} {1,4}/{2,-3} {3,4}%  {4,5}  {5,8} {6,8} {7,5}  {8,5}" -f `
            $row.Name, $row.Total, $row.Max, $row.Pct, $row.Grade, $bandShort, $reasonShort, $row.Tools, $retryFlag) -ForegroundColor $lineColor
    }

    $suiteTotal = ($Board | Measure-Object -Property Total -Sum).Sum
    $suiteMax   = ($Board | Measure-Object -Property Max -Sum).Sum
    $suitePct   = if ($suiteMax -gt 0) { [math]::Round(($suiteTotal / $suiteMax) * 100) } else { 0 }
    $suiteGrade = if ($suitePct -ge 90) { "A" } elseif ($suitePct -ge 80) { "B" } elseif ($suitePct -ge 65) { "C" } elseif ($suitePct -ge 50) { "D" } else { "F" }

    Write-Host ""
    Write-Host ("  " + ("-" * 90)) -ForegroundColor DarkGray
    $suiteColor = if ($suitePct -ge 80) { 'Green' } elseif ($suitePct -ge 50) { 'Yellow' } else { 'Red' }
    Write-Host ("  {0,-38} {1,4}/{2,-3} {3,4}%  {4,5}" -f "OVERALL", $suiteTotal, $suiteMax, $suitePct, $suiteGrade) -ForegroundColor $suiteColor
    Write-Host ""

    # Dimension breakdown
    Write-Host "  Dimension Averages:" -ForegroundColor White
    $dims = @(
        @{ Key = "Pipeline"; Label = "Pipeline (25)"; MaxVal = 25 }
        @{ Key = "ToolsSc";  Label = "Tools (20)";    MaxVal = 20 }
        @{ Key = "RetrySc";  Label = "Retry (20)";    MaxVal = 20 }
        @{ Key = "ConfSc";   Label = "Confidence (20)"; MaxVal = 20 }
        @{ Key = "CompSc";   Label = "Completeness (15)"; MaxVal = 15 }
    )
    foreach ($d in $dims) {
        $avg = [math]::Round(($Board | Measure-Object -Property $d.Key -Average).Average, 1)
        $avgPct = [math]::Round(($avg / $d.MaxVal) * 100)
        $dColor = if ($avgPct -ge 80) { 'Green' } elseif ($avgPct -ge 50) { 'Yellow' } else { 'Red' }
        Write-Host ('    {0,-22} avg {1,5}/{2,-3} ({3}%)' -f $d.Label, $avg, $d.MaxVal, $avgPct) -ForegroundColor $dColor
    }
    Write-Host ""

    # Save JSON report
    $report = @{
        timestamp     = (Get-Date -Format "o")
        suiteScore    = $suiteTotal
        suiteMax      = $suiteMax
        suitePct      = $suitePct
        suiteGrade    = $suiteGrade
        trials        = $Board
    }
    $reportPath = Join-Path $ResultDir "workflow-trials-report.json"
    $report | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath -Encoding UTF8
    Write-Host "  Report saved to: $reportPath" -ForegroundColor DarkGray
    Write-Host ""

    return $suitePct
}

function Get-TotalToolCallCount {
    param([int]$Port)

    try {
        $auditRows = @(Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$Port/api/audit?take=1000" -TimeoutSec 10)
        if ($auditRows.Count -eq 0) { return 0 }
        return @($auditRows | Where-Object { $_.category -eq "MCP_TOOL_CALL_END" }).Count
    }
    catch {
        # Audit endpoint unavailable.
        return -1
    }
}

# ── Trial definitions ─────────────────────────────────────────────────────────
# Each trial declares:
#   Name, Prompt, and expected telemetry traits for scoring.

$trials = @(
    @{
        Name     = "1. Simple Lookup"
        Prompt   = "Can you find the hours for the closest Starbucks?"
        # Expectations:
        ExpectTools        = $true
        ExpectChecklist    = $true
        ExpectRetry        = $false   # should not need retry
        MinNarrationEvents = 2
        MaxNarrationEvents = 6
        AcceptableBands    = @("High", "Medium")
        MinAnswerLength    = 20
    },
    @{
        Name     = "2. Ambiguous Target"
        Prompt   = "What time does the bakery open?"
        ExpectTools        = $false  # may or may not use tools
        ExpectChecklist    = $true
        ExpectRetry        = $false
        MinNarrationEvents = 2
        MaxNarrationEvents = 6
        AcceptableBands    = @("High", "Medium", "Low")
        MinAnswerLength    = 20
    },
    @{
        Name     = "3. Conflicting Information"
        Prompt   = "Does GitHub Copilot charge extra for generating commit messages?"
        ExpectTools        = $true
        ExpectChecklist    = $true
        ExpectRetry        = $null   # retry is acceptable either way
        MinNarrationEvents = 2
        MaxNarrationEvents = 8
        AcceptableBands    = @("High", "Medium")
        MinAnswerLength    = 40
    },
    @{
        Name     = "4. Weak Info / Retry Ladder"
        Prompt   = "What's the exact rate limit for GitHub Copilot requests per minute?"
        ExpectTools        = $true
        ExpectChecklist    = $true
        ExpectRetry        = $null   # retry expected but not mandatory
        MinNarrationEvents = 2
        MaxNarrationEvents = 10
        AcceptableBands    = @("High", "Medium", "Low")
        MinAnswerLength    = 30
    },
    @{
        Name     = "5. Multi-Step Workflow"
        Prompt   = "Find me a PlayStation 5 in stock online and give me a direct purchase link."
        ExpectTools        = $true
        ExpectChecklist    = $true
        ExpectRetry        = $null
        MinNarrationEvents = 2
        MaxNarrationEvents = 10
        AcceptableBands    = @("High", "Medium", "Low")
        MinAnswerLength    = 30
    },
    @{
        Name     = "6. Contradiction + Reasoning"
        Prompt   = "I heard carrots improve night vision because of WWII tech. Is that true?"
        ExpectTools        = $true
        ExpectChecklist    = $true
        ExpectRetry        = $false
        MinNarrationEvents = 2
        MaxNarrationEvents = 6
        AcceptableBands    = @("High", "Medium")
        MinAnswerLength    = 50
    },
    @{
        Name     = "7. Partial Success / Graceful Degr."
        Prompt   = "Can you find the cheapest flight from Boise to Tokyo next month and verify it's still available?"
        ExpectTools        = $true
        ExpectChecklist    = $true
        ExpectRetry        = $null
        MinNarrationEvents = 2
        MaxNarrationEvents = 10
        AcceptableBands    = @("High", "Medium", "Low")
        MinAnswerLength    = 30
    }
)

# ── Setup ─────────────────────────────────────────────────────────────────────

$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$settingsPath       = Join-Path $env:LOCALAPPDATA "SirThaddeus\settings.json"
$settingsBackupPath = "$settingsPath.workflow-trials.bak"
$resultDir          = Join-Path $repoRoot "artifacts\test-results"
New-Item -ItemType Directory -Force -Path $resultDir | Out-Null

if (-not (Test-Path $settingsPath)) {
    throw "settings.json not found at $settingsPath"
}

# ── Process Cleanup Helpers ───────────────────────────────────────────────

function Stop-OrphanedRuntimeProcesses {
    <#
    .SYNOPSIS
        Kills any orphaned dotnet processes that are running the headless runtime
        on the target port. Called on startup and teardown to prevent zombies.
    #>
    param([int]$TargetPort)

    # Kill any process listening on our port.
    $listeners = @(Get-NetTCPConnection -LocalPort $TargetPort -State Listen -ErrorAction SilentlyContinue)
    foreach ($conn in $listeners) {
        $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
        if ($null -ne $proc -and -not $proc.HasExited) {
            Write-Host "  Cleanup: killing orphaned process $($proc.ProcessName) (PID $($proc.Id)) on port $TargetPort" -ForegroundColor Yellow
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }

    # Kill any dotnet processes whose command line contains HeadlessRuntime.
    $dotnetProcs = @(Get-Process -Name dotnet -ErrorAction SilentlyContinue)
    foreach ($proc in $dotnetProcs) {
        try {
            $cmdLine = (Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.Id)" -ErrorAction SilentlyContinue).CommandLine
            if ($null -ne $cmdLine -and $cmdLine -like "*HeadlessRuntime*") {
                Write-Host "  Cleanup: killing orphaned HeadlessRuntime process (PID $($proc.Id))" -ForegroundColor Yellow
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }
        } catch {
            # CimInstance may fail for protected processes — skip.
        }
    }
}

function Stop-RuntimeProcessTree {
    <#
    .SYNOPSIS
        Kills the launched runtime process and all its child processes
        (including the actual dotnet host spawned by 'dotnet run').
    #>
    param([System.Diagnostics.Process]$ParentProcess, [int]$TargetPort)

    if ($null -eq $ParentProcess) { return }

    # Kill child processes first (the actual runtime host).
    try {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($ParentProcess.Id)" -ErrorAction SilentlyContinue)
        foreach ($child in $children) {
            $childProc = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $childProc -and -not $childProc.HasExited) {
                Write-Host "  Teardown: killing child process $($childProc.ProcessName) (PID $($childProc.Id))" -ForegroundColor DarkGray
                Stop-Process -Id $childProc.Id -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {}

    # Kill the parent dotnet process.
    if (-not $ParentProcess.HasExited) {
        try {
            Write-Host "  Teardown: killing runtime process (PID $($ParentProcess.Id))" -ForegroundColor DarkGray
            Stop-Process -Id $ParentProcess.Id -Force -ErrorAction SilentlyContinue
        } catch {}
    }

    # Final safety sweep: anything still on the port.
    Stop-OrphanedRuntimeProcesses -TargetPort $TargetPort

    # Brief pause to let OS release port.
    Start-Sleep -Milliseconds 500
}

# ── Pre-flight: clean up any orphans from a previous crashed run ──────────
Write-Host "Pre-flight: checking for orphaned runtime processes..." -ForegroundColor DarkGray
Stop-OrphanedRuntimeProcesses -TargetPort $Port

Copy-Item -Path $settingsPath -Destination $settingsBackupPath -Force

$runtimeProcess = $null
$runtimeLogPath = Join-Path $resultDir "workflow-trials-runtime.out.log"
$runtimeErrPath = Join-Path $resultDir "workflow-trials-runtime.err.log"

try {
    # ── Patch settings ────────────────────────────────────────────────────
    $settings = Get-Content -Raw -Path $settingsPath | ConvertFrom-Json
    $wfProp   = $settings.PSObject.Properties['workflowFeatures']
    if ($null -eq $wfProp -or $null -eq $wfProp.Value) {
        $settings | Add-Member -MemberType NoteProperty -Name workflowFeatures -Value ([pscustomobject]@{})
    }
    Set-OrAddProperty -Object $settings.workflowFeatures -Name "retryGateTestOverrideReason"   -Value ""
    $settings | ConvertTo-Json -Depth 30 | Set-Content -Path $settingsPath -Encoding UTF8

    # ── Launch runtime ────────────────────────────────────────────────────
    Write-Hdr "Launching headless runtime on port $Port"
    $runtimeArgs = @(
        "run",
        "--project", "apps/headless-runtime/SirThaddeus.HeadlessRuntime/SirThaddeus.HeadlessRuntime.csproj",
        "--", "--server", "--port", "$Port", "--tools"
    )
    $runtimeProcess = Start-Process -FilePath "dotnet" `
        -ArgumentList $runtimeArgs `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $runtimeLogPath `
        -RedirectStandardError $runtimeErrPath `
        -PassThru

    # ── Wait for health ───────────────────────────────────────────────────
    $healthUrl = "http://127.0.0.1:$Port/api/health"
    $deadline  = (Get-Date).AddSeconds(60)
    $ready     = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $h = Invoke-RestMethod -Method Get -Uri $healthUrl -TimeoutSec 2
            if ($h.status -eq "ok") { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw "Runtime health check failed at $healthUrl" }
    Write-Host "Runtime is healthy." -ForegroundColor Green

    # ── Run trials ────────────────────────────────────────────────────────
    $scoreboard = @()

    foreach ($trial in $trials) {
        Write-Hdr ("TRIAL: " + $trial.Name) -Color Yellow

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $toolCallsBefore = Get-TotalToolCallCount -Port $Port

        # Clear conversation for isolation.
        try { Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$Port/api/session/clear" -TimeoutSec 5 | Out-Null } catch {}

        # Start run.
        $chatBody = @{
            prompt         = $trial.Prompt
            conversationId = "trial-" + [guid]::NewGuid().ToString("N").Substring(0,8)
            sessionId      = "trials"
        } | ConvertTo-Json -Depth 10

        $chatStart = $null
        try {
            $chatStart = Invoke-RestMethod -Method Post `
                -Uri "http://127.0.0.1:$Port/api/chat" `
                -ContentType "application/json" `
                -Body $chatBody `
                -TimeoutSec 30
        } catch {
            Write-Host "  FAILED to start run: $_" -ForegroundColor Red
            $scoreboard += @{ Name = $trial.Name; Total = 0; Max = 100; Details = "Run start failed"; Elapsed = $sw.Elapsed }
            continue
        }

        if ([string]::IsNullOrWhiteSpace($chatStart.runId)) {
            Write-Host "  FAILED: No runId returned" -ForegroundColor Red
            $scoreboard += @{ Name = $trial.Name; Total = 0; Max = 100; Details = "No runId"; Elapsed = $sw.Elapsed }
            continue
        }

        Write-Host "  Run started: $($chatStart.runId)" -ForegroundColor DarkCyan

        # Consume SSE event stream.
        $eventsUrl = "http://127.0.0.1:$Port/api/runs/$($chatStart.runId)/events"
        $envelopes = @()
        try {
            $stream = Invoke-WebRequest -Method Get -Uri $eventsUrl -TimeoutSec $TimeoutSec -UseBasicParsing
            foreach ($line in ($stream.Content -split "`r?`n")) {
                if ($line -like "data:*") {
                    $json = $line.Substring(5).Trim()
                    if (-not [string]::IsNullOrWhiteSpace($json)) {
                        $envelopes += ($json | ConvertFrom-Json)
                    }
                }
            }
        } catch {
            Write-Host "  FAILED to consume event stream: $_" -ForegroundColor Red
            $scoreboard += @{ Name = $trial.Name; Total = 0; Max = 100; Details = "Stream error"; Elapsed = $sw.Elapsed }
            continue
        }

        $sw.Stop()
        $toolCallsAfter = Get-TotalToolCallCount -Port $Port

        if ($envelopes.Count -eq 0) {
            Write-Host "  FAILED: Zero events captured" -ForegroundColor Red
            $scoreboard += @{ Name = $trial.Name; Total = 0; Max = 100; Details = "No events"; Elapsed = $sw.Elapsed }
            continue
        }

        # ── Parse events ──────────────────────────────────────────────────
        $eventTypes     = @($envelopes | ForEach-Object { $_.eventType })
        $completed      = $envelopes | Where-Object { $_.eventType -eq "run.completed" } | Select-Object -Last 1
        $progressEvents = @($envelopes | Where-Object { $_.eventType -eq "progress.event" })
        $retryStarted   = $progressEvents | Where-Object { $_.payload.eventType -eq "retry.started" } | Select-Object -First 1
        $retrySkipped   = $progressEvents | Where-Object { $_.payload.eventType -eq "retry.skipped" } | Select-Object -First 1
        $toolRequested  = @($envelopes | Where-Object { $_.eventType -eq "tool.requested" })
        $toolCountReliable = ($toolCallsBefore -ge 0 -and $toolCallsAfter -ge 0 -and $toolCallsAfter -ge $toolCallsBefore)
        $toolCallCount = if ($toolCountReliable) { [int]($toolCallsAfter - $toolCallsBefore) } else { [int]$toolRequested.Count }
        $checklistEvts  = @($eventTypes | Where-Object { $_ -eq "checklist.updated" })
        $narrationEvts  = @($eventTypes | Where-Object { $_ -eq "narration.updated" })

        $completionReason = Get-SafeString $completed.payload.completionReason
        $confidenceBand   = Get-SafeString $completed.payload.confidenceBand
        $finalText        = Get-SafeString $completed.payload.finalText
        $retryGateAllowed = $completed.payload.retryGateAllowed
        $retryGateReason  = Get-SafeString $completed.payload.retryGateReason
        $toolCallsFromCompleted = 0
        try { $toolCallsFromCompleted = [int]$completed.payload.toolCallsUsed } catch { $toolCallsFromCompleted = 0 }

        if ($toolCallsFromCompleted -gt 0) {
            $toolCallCount = $toolCallsFromCompleted
            $toolCountSource = "run.completed"
        } else {
            $toolCountSource = if ($toolCountReliable) { "audit" } else { "fallback" }
        }

        # Quick telemetry summary.
        Write-Host "  Events: $($envelopes.Count)  |  ToolCalls: $toolCallCount ($toolCountSource)  |  ToolRequests: $($toolRequested.Count)  |  Narrations: $($narrationEvts.Count)  |  Checklist: $($checklistEvts.Count)  |  Elapsed: $([math]::Round($sw.Elapsed.TotalSeconds,1))s" -ForegroundColor DarkGray

        # ── Score: Pipeline (0-25) ────────────────────────────────────────
        $pipelineScore = 0; $pipelineMax = 25; $pipelineNotes = @()

        # run.completed present?
        if ($null -ne $completed) {
            $pipelineScore += 5
        } else {
            $pipelineNotes += "missing run.completed"
        }

        # completionReason present?
        if (-not [string]::IsNullOrWhiteSpace($completionReason)) {
            $pipelineScore += 5
        } else { $pipelineNotes += "missing completionReason" }

        # narration events in expected range?
        if ($narrationEvts.Count -ge $trial.MinNarrationEvents -and $narrationEvts.Count -le $trial.MaxNarrationEvents) {
            $pipelineScore += 5
        } elseif ($narrationEvts.Count -gt 0) {
            $pipelineScore += 2
            $pipelineNotes += "narration count $($narrationEvts.Count) outside ideal [$($trial.MinNarrationEvents)..$($trial.MaxNarrationEvents)]"
        } else { $pipelineNotes += "zero narration events" }

        # checklist events?
        if ($trial.ExpectChecklist -and $checklistEvts.Count -gt 0) {
            $pipelineScore += 5
        } elseif (-not $trial.ExpectChecklist) {
            $pipelineScore += 5
        } else {
            # No checklist but expected — partial credit if task.started fired.
            $taskStarted = $progressEvents | Where-Object { $_.payload.eventType -eq "task.started" } | Select-Object -First 1
            if ($null -ne $taskStarted) { $pipelineScore += 2; $pipelineNotes += "no checklist.updated but task.started present" }
            else { $pipelineNotes += "no checklist events" }
        }

        # answer text present and meets min length?
        if (-not [string]::IsNullOrWhiteSpace($finalText) -and $finalText.Length -ge $trial.MinAnswerLength) {
            $pipelineScore += 5
        } elseif (-not [string]::IsNullOrWhiteSpace($finalText)) {
            $pipelineScore += 2
            $pipelineNotes += "answer short ($($finalText.Length) chars, expected >= $($trial.MinAnswerLength))"
        } else { $pipelineNotes += "empty answer" }

        Write-Dim "Pipeline" $pipelineScore $pipelineMax ($pipelineNotes -join "; ")

        # ── Score: Tools (0-20) ───────────────────────────────────────────
        $toolsScore = 0; $toolsMax = 20; $toolsNotes = @()

        if ($trial.ExpectTools -eq $true) {
            if ($toolCallCount -gt 0) {
                $toolsScore += 10
                # Bonus: reasonable tool count (not runaway).
                if ($toolCallCount -le 12) {
                    $toolsScore += 5
                } else {
                    $toolsScore += 2
                    $toolsNotes += "high tool count ($toolCallCount)"
                }
                # Bonus: telemetry consistency (permission prompts are optional).
                if ($toolRequested.Count -gt 0) { $toolsScore += 5 }
                else { $toolsScore += 2; $toolsNotes += "calls observed via audit (no permission prompts)" }
            } else {
                $toolsNotes += "expected tool calls but none observed"
            }
        } elseif ($trial.ExpectTools -eq $false) {
            # Tools are optional; don't penalise if used, but reward restraint.
            if ($toolCallCount -eq 0) {
                $toolsScore += 20
                $toolsNotes += "correctly avoided tools"
            } else {
                $toolsScore += 15
                $toolsNotes += "tools used (optional) - $toolCallCount"
            }
        } else {
            # null expectation: score proportionally.
            $toolsScore += [math]::Min(20, 10 + $toolCallCount * 2)
        }

        Write-Dim "Tools" $toolsScore $toolsMax ($toolsNotes -join "; ")

        # ── Score: Retry (0-20) ───────────────────────────────────────────
        $retryScore = 0; $retryMax = 20; $retryNotes = @()

        # Did retry gate fire at all? (should always fire when workflow enabled)
        if ($null -ne $retryGateReason -and $retryGateReason -ne "") {
            $retryScore += 5
        } else { $retryNotes += "no retryGateReason in run.completed" }

        if ($null -ne $retryGateAllowed) {
            $retryScore += 5
        } else { $retryNotes += "no retryGateAllowed" }

        if ($trial.ExpectRetry -eq $true) {
            # Retry was expected.
            if ($null -ne $retryStarted) {
                $retryScore += 10
                $retryNotes += "retry fired as expected"
            } else {
                $retryNotes += "retry expected but not observed"
            }
        } elseif ($trial.ExpectRetry -eq $false) {
            # Retry was NOT expected.
            if ($null -eq $retryStarted) {
                $retryScore += 10
                $retryNotes += "correctly skipped retry"
            } else {
                $retryScore += 5
                $retryNotes += "unnecessary retry fired"
            }
        } else {
            # Null = retry either way is fine. Score for gate decision being present.
            $retryScore += 10
            if ($null -ne $retryStarted) { $retryNotes += "retry fired" }
            elseif ($null -ne $retrySkipped) { $retryNotes += "retry skipped ($(Get-SafeString $retrySkipped.payload.metadata.reason 'unknown'))" }
        }

        Write-Dim "Retry" $retryScore $retryMax ($retryNotes -join "; ")

        # ── Score: Confidence (0-20) ──────────────────────────────────────
        $confScore = 0; $confMax = 20; $confNotes = @()

        if (-not [string]::IsNullOrWhiteSpace($confidenceBand)) {
            $confScore += 5
            if ($trial.AcceptableBands -contains $confidenceBand) {
                $confScore += 10
                $confNotes += "band=$confidenceBand (acceptable)"
            } else {
                $confScore += 3
                $confNotes += "band=$confidenceBand (outside ideal)"
            }
        } else { $confNotes += "no confidence band" }

        # retry.skipped or retry.started should carry confidence metadata.
        $anyRetryProgress = if ($null -ne $retrySkipped) { $retrySkipped } elseif ($null -ne $retryStarted) { $retryStarted } else { $null }
        if ($null -ne $anyRetryProgress -and $null -ne $anyRetryProgress.payload.metadata) {
            $scoreStr = Get-SafeString $anyRetryProgress.payload.metadata.confidenceScore
            $bandStr  = Get-SafeString $anyRetryProgress.payload.metadata.confidenceBand
            if ($scoreStr -ne "" -and $bandStr -ne "") {
                $confScore += 5
                $confNotes += "score=$scoreStr"
            } else {
                $confNotes += "partial confidence metadata"
            }
        }

        Write-Dim "Confidence" $confScore $confMax ($confNotes -join "; ")

        # ── Score: Completeness (0-15) ────────────────────────────────────
        $compScore = 0; $compMax = 15; $compNotes = @()

        # Audit snapshot present?
        try {
            $auditResp = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$Port/api/audit?take=200" -TimeoutSec 10
            $snapshots = @($auditResp | Where-Object { $_.category -eq "WORKFLOW_RUN_SNAPSHOT" })
            if ($snapshots.Count -gt 0) {
                $compScore += 5
            } else { $compNotes += "no audit snapshot" }
        } catch {
            $compNotes += "audit API error"
        }

        # task.started event with complexity?
        $taskStartedEvt = $progressEvents | Where-Object { $_.payload.eventType -eq "task.started" } | Select-Object -First 1
        if ($null -ne $taskStartedEvt -and (Get-SafeString $taskStartedEvt.payload.metadata.complexity) -ne "") {
            $compScore += 5
            $compNotes += "complexity=$(Get-SafeString $taskStartedEvt.payload.metadata.complexity)"
        } else { $compNotes += "no task.started complexity" }

        # No run.failed?
        $failedEvt = $envelopes | Where-Object { $_.eventType -eq "run.failed" } | Select-Object -First 1
        if ($null -eq $failedEvt) {
            $compScore += 5
        } else {
            $compNotes += "run.failed observed"
        }

        Write-Dim "Completeness" $compScore $compMax ($compNotes -join "; ")

        # ── Trial total ───────────────────────────────────────────────────
        $totalScore = $pipelineScore + $toolsScore + $retryScore + $confScore + $compScore
        $totalMax   = $pipelineMax + $toolsMax + $retryMax + $confMax + $compMax
        $pct        = [math]::Round(($totalScore / $totalMax) * 100)
        $grade      = if ($pct -ge 90) { "A" } elseif ($pct -ge 80) { "B" } elseif ($pct -ge 65) { "C" } elseif ($pct -ge 50) { "D" } else { "F" }

        Write-Host ""
        $gradeColor = if ($pct -ge 80) { 'Green' } elseif ($pct -ge 50) { 'Yellow' } else { 'Red' }
        Write-Host ("    TRIAL SCORE:  {0}/{1}  ({2}%)  Grade: {3}" -f $totalScore, $totalMax, $pct, $grade) -ForegroundColor $gradeColor
        Write-Host ("    Elapsed:      {0:N1}s" -f $sw.Elapsed.TotalSeconds)
        Write-Host ("    Answer:       {0}" -f $(if ($finalText.Length -gt 120) { $finalText.Substring(0, 120) + "..." } else { $finalText })) -ForegroundColor DarkGray

        $scoreboard += [pscustomobject]@{
            Name       = $trial.Name
            Total      = $totalScore
            Max        = $totalMax
            Pct        = $pct
            Grade      = $grade
            Elapsed    = $sw.Elapsed
            Band       = $confidenceBand
            Reason     = $completionReason
            Tools      = $toolCallCount
            Retried    = ($null -ne $retryStarted)
            AnswerLen  = $finalText.Length
            Pipeline   = $pipelineScore
            ToolsSc    = $toolsScore
            RetrySc    = $retryScore
            ConfSc     = $confScore
            CompSc     = $compScore
        }
    }

    # ── Suite scorecard ───────────────────────────────────────────────────
    $suitePct = Show-Scorecard -Board $scoreboard -ResultDir $resultDir

    # ── Exit code ─────────────────────────────────────────────────────────
    if ($suitePct -lt 50) {
        Write-Host "SUITE FAILED (below 50%)" -ForegroundColor Red
        exit 1
    }

    Write-Host "SUITE PASSED" -ForegroundColor Green
}
finally {
    Write-Host "`nTeardown: cleaning up runtime processes..." -ForegroundColor DarkGray
    Stop-RuntimeProcessTree -ParentProcess $runtimeProcess -TargetPort $Port
    if (Test-Path $settingsBackupPath) {
        Move-Item -Path $settingsBackupPath -Destination $settingsPath -Force
    }
    Write-Host "Teardown complete." -ForegroundColor DarkGray
}
