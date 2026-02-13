param(
    [string]$AuditPath = (Join-Path $env:LOCALAPPDATA "SirThaddeus\audit.jsonl"),
    [int]$RunsPerPhrase = 10,
    [string[]]$Phrases = @(
        "Hello there.",
        "What is six times seven?",
        "Before we continue, please summarize the latest update slowly and clearly so every word is easy to catch in a noisy room."
    ),
    [int]$TailLines = 200000
)

$ErrorActionPreference = "Stop"

if ($RunsPerPhrase -lt 1) {
    throw "RunsPerPhrase must be at least 1."
}

if ($null -eq $Phrases -or $Phrases.Count -eq 0) {
    throw "At least one phrase is required."
}

if (-not (Test-Path $AuditPath)) {
    throw "Audit log not found: $AuditPath"
}

function Parse-Timestamp($Value) {
    if ($null -eq $Value) { return $null }
    $raw = [string]$Value
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    try { return [DateTimeOffset]::Parse($raw) } catch { return $null }
}

function Parse-NonNegativeDouble($Value) {
    if ($null -eq $Value) { return $null }
    try {
        $num = [double]$Value
        if ([double]::IsNaN($num) -or $num -lt 0) { return $null }
        return $num
    }
    catch {
        return $null
    }
}

function Get-Median([double[]]$Values) {
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $count = $sorted.Count
    $mid = [int][Math]::Floor($count / 2)
    if (($count % 2) -eq 1) { return [double]$sorted[$mid] }
    return ([double]$sorted[$mid - 1] + [double]$sorted[$mid]) / 2.0
}

function Get-Percentile([double[]]$Values, [double]$Percent) {
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $count = $sorted.Count
    $rank = [int][Math]::Ceiling(($Percent / 100.0) * $count) - 1
    $index = [Math]::Max(0, [Math]::Min($rank, $count - 1))
    return [double]$sorted[$index]
}

function Format-Metric($Value) {
    if ($null -eq $Value) { return "n/a" }
    return [Math]::Round([double]$Value, 2)
}

$turnsBySession = @{}

function Get-OrCreateTurn([string]$SessionId) {
    if (-not $turnsBySession.ContainsKey($SessionId)) {
        $turnsBySession[$SessionId] = [ordered]@{
            session_id = $SessionId
            t_mic_down = $null
            t_first_audio_frame = $null
            t_mic_up = $null
            t_asr_start = $null
            t_asr_first_token = $null
            t_asr_final = $null
            t_agent_start = $null
            t_agent_final = $null
            t_tts_start = $null
            t_playback_start = $null
            audio_capture_duration_ms = $null
            asr_latency_ms = $null
            end_to_end_to_playback_start_ms = $null
        }
    }
    return $turnsBySession[$SessionId]
}

Write-Host "Manual voice-turn benchmark"
Write-Host "Audit log: $AuditPath"
Write-Host "Tail lines: $TailLines"
Write-Host ""

$lines = Get-Content -Path $AuditPath -Encoding UTF8 -Tail $TailLines
foreach ($line in $lines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    try {
        $evt = $line | ConvertFrom-Json
    }
    catch {
        continue
    }

    $action = [string]$evt.action
    $details = $evt.details
    if ($null -eq $details) { continue }

    if ($action -eq "VOICE_TURN_TIMING_SUMMARY") {
        $sessionId = [string]$details.sessionId
        if ([string]::IsNullOrWhiteSpace($sessionId)) { continue }
        if ($sessionId.StartsWith("preview-", [System.StringComparison]::OrdinalIgnoreCase)) { continue }

        $turn = Get-OrCreateTurn -SessionId $sessionId
        $turn.t_mic_down = Parse-Timestamp $details.t_mic_down
        $turn.t_first_audio_frame = Parse-Timestamp $details.t_first_audio_frame
        $turn.t_mic_up = Parse-Timestamp $details.t_mic_up
        $turn.t_asr_start = Parse-Timestamp $details.t_asr_start
        $turn.t_asr_first_token = Parse-Timestamp $details.t_asr_first_token
        $turn.t_asr_final = Parse-Timestamp $details.t_asr_final
        $turn.t_agent_start = Parse-Timestamp $details.t_agent_start
        $turn.t_agent_final = Parse-Timestamp $details.t_agent_final
        $turn.t_tts_start = Parse-Timestamp $details.t_tts_start
        $turn.t_playback_start = Parse-Timestamp $details.t_playback_start
        $turn.audio_capture_duration_ms = Parse-NonNegativeDouble $details.audio_capture_duration_ms
        $turn.asr_latency_ms = Parse-NonNegativeDouble $details.asr_latency_ms
        $turn.end_to_end_to_playback_start_ms = Parse-NonNegativeDouble $details.end_to_end_to_playback_start_ms
        continue
    }

    if ($action -eq "VOICE_STAGE_TIMESTAMP") {
        $sessionId = [string]$details.sessionId
        if ([string]::IsNullOrWhiteSpace($sessionId)) { continue }
        if ($sessionId.StartsWith("preview-", [System.StringComparison]::OrdinalIgnoreCase)) { continue }

        $stage = [string]$details.stage
        $timestamp = Parse-Timestamp $details.timestampUtc
        if ($null -eq $timestamp -or [string]::IsNullOrWhiteSpace($stage)) { continue }

        $turn = Get-OrCreateTurn -SessionId $sessionId
        switch ($stage) {
            "t_mic_down" { $turn.t_mic_down = $timestamp }
            "t_first_audio_frame" { $turn.t_first_audio_frame = $timestamp }
            "t_mic_up" { $turn.t_mic_up = $timestamp }
            "t_asr_start" { $turn.t_asr_start = $timestamp }
            "t_asr_first_token" { $turn.t_asr_first_token = $timestamp }
            "t_asr_final" { $turn.t_asr_final = $timestamp }
            "t_agent_start" { $turn.t_agent_start = $timestamp }
            "t_agent_final" { $turn.t_agent_final = $timestamp }
            "t_tts_start" { $turn.t_tts_start = $timestamp }
            "t_playback_start" { $turn.t_playback_start = $timestamp }
        }
    }
}

$turns = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $turnsBySession.GetEnumerator()) {
    $turn = $entry.Value
    if ($null -eq $turn.t_mic_down) {
        continue
    }

    if ($null -eq $turn.audio_capture_duration_ms -and $null -ne $turn.t_mic_up) {
        $turn.audio_capture_duration_ms = [Math]::Round(($turn.t_mic_up - $turn.t_mic_down).TotalMilliseconds, 2)
    }
    if ($null -eq $turn.asr_latency_ms -and $null -ne $turn.t_asr_start -and $null -ne $turn.t_asr_final) {
        $turn.asr_latency_ms = [Math]::Round(($turn.t_asr_final - $turn.t_asr_start).TotalMilliseconds, 2)
    }
    if ($null -eq $turn.end_to_end_to_playback_start_ms -and $null -ne $turn.t_playback_start) {
        $turn.end_to_end_to_playback_start_ms = [Math]::Round(($turn.t_playback_start - $turn.t_mic_down).TotalMilliseconds, 2)
    }

    $turns.Add([pscustomobject]$turn) | Out-Null
}

$ordered = @($turns | Sort-Object t_mic_down)
$required = $RunsPerPhrase * $Phrases.Count
if ($ordered.Count -lt $required) {
    throw "Not enough completed voice turns in audit log. Need $required, found $($ordered.Count)."
}

$selected = @($ordered | Select-Object -Last $required)
Write-Host ("Using latest {0} turns ({1} phrases x {2} runs) ordered by t_mic_down." -f $required, $Phrases.Count, $RunsPerPhrase)
Write-Host ""

for ($phraseIndex = 0; $phraseIndex -lt $Phrases.Count; $phraseIndex++) {
    $phrase = $Phrases[$phraseIndex]
    $slice = @(
        $selected |
            Select-Object -Skip ($phraseIndex * $RunsPerPhrase) -First $RunsPerPhrase
    )

    $asr = @($slice | ForEach-Object { Parse-NonNegativeDouble $_.asr_latency_ms } | Where-Object { $null -ne $_ })
    $e2e = @($slice | ForEach-Object { Parse-NonNegativeDouble $_.end_to_end_to_playback_start_ms } | Where-Object { $null -ne $_ })

    $asrMedian = Get-Median -Values $asr
    $asrP95 = Get-Percentile -Values $asr -Percent 95
    $e2eMedian = Get-Median -Values $e2e
    $e2eP95 = Get-Percentile -Values $e2e -Percent 95

    Write-Host ("- Phrase {0}: {1}" -f ($phraseIndex + 1), $phrase)
    Write-Host ("  ASR latency ms: runs={0} median={1} p95={2}" -f $asr.Count, (Format-Metric $asrMedian), (Format-Metric $asrP95))
    Write-Host ("  End-to-end->playback-start ms: runs={0} median={1} p95={2}" -f $e2e.Count, (Format-Metric $e2eMedian), (Format-Metric $e2eP95))
}

$allAsr = @($selected | ForEach-Object { Parse-NonNegativeDouble $_.asr_latency_ms } | Where-Object { $null -ne $_ })
$allE2e = @($selected | ForEach-Object { Parse-NonNegativeDouble $_.end_to_end_to_playback_start_ms } | Where-Object { $null -ne $_ })

Write-Host ""
Write-Host ("Overall ASR latency ms: runs={0} median={1} p95={2}" -f $allAsr.Count, (Format-Metric (Get-Median $allAsr)), (Format-Metric (Get-Percentile $allAsr 95)))
Write-Host ("Overall End-to-end->playback-start ms: runs={0} median={1} p95={2}" -f $allE2e.Count, (Format-Metric (Get-Median $allE2e)), (Format-Metric (Get-Percentile $allE2e 95)))
