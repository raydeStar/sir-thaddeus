#requires -Version 5.1

param(
    [int]$Port = 5391,
    [string]$Prompt = "Can you get me details on GitHub pricing?",
    [int]$TimeoutSec = 120,
    [switch]$ExpectRetry,
    [switch]$ExpectRetrySkipped,
    [string]$ExpectedRetrySkipReason,
    [switch]$AssertRetrySkipMetadata,
    [switch]$AssertRunCompletedRetryGate,
    [switch]$ForceToolBudgetZero,
    [switch]$AllowChecklistMissing
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Set-OrAddProperty {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
        return
    }

    $Object.$Name = $Value
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$settingsPath = Join-Path $env:LOCALAPPDATA "SirThaddeus\settings.json"
if (-not (Test-Path $settingsPath)) {
    throw "settings.json not found at $settingsPath"
}

$settingsBackupPath = "$settingsPath.workflow-e2e.bak"
Copy-Item -Path $settingsPath -Destination $settingsBackupPath -Force

$runtimeProcess = $null
$runtimeLogPath = Join-Path $repoRoot "artifacts\test-results\workflow-e2e-runtime.out.log"
$runtimeErrPath = Join-Path $repoRoot "artifacts\test-results\workflow-e2e-runtime.err.log"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $runtimeLogPath) | Out-Null

try {
    $settings = Get-Content -Raw -Path $settingsPath | ConvertFrom-Json
    $workflowFeaturesProp = $settings.PSObject.Properties['workflowFeatures']
    if ($null -eq $workflowFeaturesProp -or $null -eq $workflowFeaturesProp.Value) {
        $settings | Add-Member -MemberType NoteProperty -Name workflowFeatures -Value ([pscustomobject]@{})
    }

    Set-OrAddProperty -Object $settings.workflowFeatures -Name "checklistProgressUiEnabled" -Value $true
    Set-OrAddProperty -Object $settings.workflowFeatures -Name "confidenceScoringEnabled" -Value $true
    Set-OrAddProperty -Object $settings.workflowFeatures -Name "constrainedRetryEnabled" -Value $true
    Set-OrAddProperty -Object $settings.workflowFeatures -Name "taskRunAuditSnapshotsEnabled" -Value $true

    if ($ForceToolBudgetZero) {
        $toolBudgetsProp = $settings.PSObject.Properties['toolBudgets']
        if ($null -eq $toolBudgetsProp -or $null -eq $toolBudgetsProp.Value) {
            $settings | Add-Member -MemberType NoteProperty -Name toolBudgets -Value ([pscustomobject]@{})
        }

        Set-OrAddProperty -Object $settings.toolBudgets -Name "maxToolCallsPerTurn" -Value 0
    }

    $settings | ConvertTo-Json -Depth 30 | Set-Content -Path $settingsPath -Encoding UTF8

    Write-Host "Starting headless runtime on port $Port..." -ForegroundColor Cyan
    $runtimeArgs = @(
        "run",
        "--project", "apps/headless-runtime/SirThaddeus.HeadlessRuntime/SirThaddeus.HeadlessRuntime.csproj",
        "--",
        "--server",
        "--port", "$Port",
        "--tools"
    )

    $runtimeProcess = Start-Process -FilePath "dotnet" -ArgumentList $runtimeArgs -WorkingDirectory $repoRoot -RedirectStandardOutput $runtimeLogPath -RedirectStandardError $runtimeErrPath -PassThru

    $healthUrl = "http://127.0.0.1:$Port/api/health"
    $deadline = (Get-Date).AddSeconds(45)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $health = Invoke-RestMethod -Method Get -Uri $healthUrl -TimeoutSec 2
            if ($health.status -eq "ok") {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }

    if (-not $ready) {
        throw "Runtime health check failed at $healthUrl"
    }

    $chatBody = @{
        prompt = $Prompt
        conversationId = "workflow-e2e"
        sessionId = "workflow-e2e"
    } | ConvertTo-Json -Depth 10

    $chatStart = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$Port/api/chat" -ContentType "application/json" -Body $chatBody -TimeoutSec 30
    if ([string]::IsNullOrWhiteSpace($chatStart.runId)) {
        throw "Chat start did not return runId"
    }

    Write-Host "Run started: $($chatStart.runId)" -ForegroundColor Green

    $eventsUrl = "http://127.0.0.1:$Port/api/runs/$($chatStart.runId)/events"
    $stream = Invoke-WebRequest -Method Get -Uri $eventsUrl -TimeoutSec $TimeoutSec -UseBasicParsing
    $lines = ($stream.Content -split "`r?`n")

    $envelopes = @()
    foreach ($line in $lines) {
        if ($line -like "data:*") {
            $json = $line.Substring(5).Trim()
            if (-not [string]::IsNullOrWhiteSpace($json)) {
                $envelopes += ($json | ConvertFrom-Json)
            }
        }
    }

    if ($envelopes.Count -eq 0) {
        throw "No runtime events were captured"
    }

    $eventTypes = @($envelopes | ForEach-Object { $_.eventType })
    $requiredEventTypes = @("narration.updated", "run.completed")
    if (-not $AllowChecklistMissing) {
        $requiredEventTypes = @("checklist.updated") + $requiredEventTypes
    }
    foreach ($required in $requiredEventTypes) {
        if (-not ($eventTypes -contains $required)) {
            throw "Missing required event type: $required"
        }
    }

    $completed = $envelopes | Where-Object { $_.eventType -eq "run.completed" } | Select-Object -Last 1
    if ($null -eq $completed) {
        throw "run.completed event missing"
    }

    $completionReason = $completed.payload.completionReason
    $confidenceBand = $completed.payload.confidenceBand
    $finalText = $completed.payload.finalText
    $retryGateAllowed = $completed.payload.retryGateAllowed
    $retryGateReason = [string]$completed.payload.retryGateReason

    if ([string]::IsNullOrWhiteSpace($completionReason)) {
        throw "run.completed is missing completionReason"
    }

    if ([string]::IsNullOrWhiteSpace($confidenceBand)) {
        throw "run.completed is missing confidenceBand"
    }

    if ([string]::IsNullOrWhiteSpace($finalText)) {
        throw "run.completed is missing finalText"
    }

    $checklistCount = @($eventTypes | Where-Object { $_ -eq "checklist.updated" }).Count
    $narrationCount = @($eventTypes | Where-Object { $_ -eq "narration.updated" }).Count
    $progressEvents = @($envelopes | Where-Object { $_.eventType -eq "progress.event" })
    $retryStarted = $progressEvents | Where-Object { $_.payload.eventType -eq "retry.started" } | Select-Object -First 1
    $retrySkipped = $progressEvents | Where-Object { $_.payload.eventType -eq "retry.skipped" } | Select-Object -First 1

    if ($ExpectRetry -and $null -eq $retryStarted) {
        throw "Expected retry.started progress event but none was observed"
    }

    if ($ExpectRetrySkipped -and $null -eq $retrySkipped) {
        throw "Expected retry.skipped progress event but none was observed"
    }

    if ($ExpectRetrySkipped -and -not [string]::IsNullOrWhiteSpace($ExpectedRetrySkipReason)) {
        $actualRetrySkipReason = [string]$retrySkipped.payload.metadata.reason
        if (-not [string]::Equals($actualRetrySkipReason, $ExpectedRetrySkipReason, [StringComparison]::Ordinal)) {
            throw "Expected retry.skipped reason '$ExpectedRetrySkipReason' but got '$actualRetrySkipReason'"
        }
    }

    if ($AssertRetrySkipMetadata) {
        if ($null -eq $retrySkipped) {
            throw "Cannot assert retry.skipped metadata when retry.skipped event is missing"
        }

        $retrySkipMetadata = $retrySkipped.payload.metadata
        if ($null -eq $retrySkipMetadata) {
            throw "retry.skipped payload is missing metadata"
        }

        $remainingRetriesRaw = [string]$retrySkipMetadata.remainingRetries
        $remainingToolCallsRaw = [string]$retrySkipMetadata.remainingToolCalls
        $remainingTimeMsRaw = [string]$retrySkipMetadata.remainingTimeMs
        $confidenceBandRaw = [string]$retrySkipMetadata.confidenceBand
        $confidenceScoreRaw = [string]$retrySkipMetadata.confidenceScore

        if ([string]::IsNullOrWhiteSpace($remainingRetriesRaw)) {
            throw "retry.skipped metadata missing remainingRetries"
        }

        if ([string]::IsNullOrWhiteSpace($remainingToolCallsRaw)) {
            throw "retry.skipped metadata missing remainingToolCalls"
        }

        if ([string]::IsNullOrWhiteSpace($remainingTimeMsRaw)) {
            throw "retry.skipped metadata missing remainingTimeMs"
        }

        if ([string]::IsNullOrWhiteSpace($confidenceBandRaw)) {
            throw "retry.skipped metadata missing confidenceBand"
        }

        if ([string]::IsNullOrWhiteSpace($confidenceScoreRaw)) {
            throw "retry.skipped metadata missing confidenceScore"
        }

        $remainingRetries = 0
        if (-not [int]::TryParse($remainingRetriesRaw, [ref]$remainingRetries)) {
            throw "retry.skipped metadata remainingRetries is not an integer: '$remainingRetriesRaw'"
        }

        $remainingToolCalls = 0
        if (-not [int]::TryParse($remainingToolCallsRaw, [ref]$remainingToolCalls)) {
            throw "retry.skipped metadata remainingToolCalls is not an integer: '$remainingToolCallsRaw'"
        }

        $remainingTimeMs = 0
        if (-not [int]::TryParse($remainingTimeMsRaw, [ref]$remainingTimeMs)) {
            throw "retry.skipped metadata remainingTimeMs is not an integer: '$remainingTimeMsRaw'"
        }

        $confidenceScore = 0.0
        if (-not [double]::TryParse($confidenceScoreRaw, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$confidenceScore)) {
            throw "retry.skipped metadata confidenceScore is not a floating-point number: '$confidenceScoreRaw'"
        }

        if ($remainingRetries -lt 0 -or $remainingToolCalls -lt 0 -or $remainingTimeMs -lt 0) {
            throw "retry.skipped metadata contains negative budget values"
        }

        if ($confidenceScore -lt 0.0 -or $confidenceScore -gt 1.0) {
            throw "retry.skipped metadata confidenceScore is out of range [0,1]: $confidenceScore"
        }
    }

    if ($AssertRunCompletedRetryGate) {
        if ($null -eq $retryGateAllowed) {
            throw "run.completed is missing retryGateAllowed"
        }

        if ([string]::IsNullOrWhiteSpace($retryGateReason)) {
            throw "run.completed is missing retryGateReason"
        }

        if ($ExpectRetrySkipped -and -not [string]::IsNullOrWhiteSpace($ExpectedRetrySkipReason)) {
            if (-not [string]::Equals($retryGateReason, $ExpectedRetrySkipReason, [StringComparison]::Ordinal)) {
                throw "run.completed retryGateReason '$retryGateReason' did not match expected '$ExpectedRetrySkipReason'"
            }
        }
    }

    if ($ExpectRetry -and [string]::IsNullOrWhiteSpace($completionReason)) {
        throw "Retry scenario did not provide completionReason"
    }

    Write-Host "Workflow E2E passed." -ForegroundColor Green
    Write-Host "  completionReason: $completionReason"
    Write-Host "  confidenceBand:   $confidenceBand"
    Write-Host "  checklist events: $checklistCount"
    Write-Host "  narration events: $narrationCount"
    Write-Host "  retry observed:   $([bool]$retryStarted)"
    Write-Host "  retry skipped:    $([bool]$retrySkipped)"
    if ($null -ne $retrySkipped) {
        Write-Host "  retry skip reason: $([string]$retrySkipped.payload.metadata.reason)"
    }
    Write-Host "  retry gate allowed (run.completed): $retryGateAllowed"
    if (-not [string]::IsNullOrWhiteSpace($retryGateReason)) {
        Write-Host "  retry gate reason  (run.completed): $retryGateReason"
    }
    Write-Host "  runtime out log:  $runtimeLogPath"
    Write-Host "  runtime err log:  $runtimeErrPath"
}
finally {
    if ($runtimeProcess -and -not $runtimeProcess.HasExited) {
        try {
            Stop-Process -Id $runtimeProcess.Id -Force -ErrorAction SilentlyContinue
        }
        catch {
        }
    }

    if (Test-Path $settingsBackupPath) {
        Move-Item -Path $settingsBackupPath -Destination $settingsPath -Force
    }
}
