param(
    [string]$BaseUrl = "http://127.0.0.1:8001",
    [int]$Runs = 10,
    [string[]]$Phrases = @(
        "Hello there.",
        "What is six times seven?",
        "Before we continue, please summarize the latest update slowly and clearly so every word is easy to catch in a noisy room."
    ),
    [string]$TtsEngine = "",
    [string]$TtsModelId = "",
    [string]$TtsVoiceId = "",
    [string]$SttEngine = "",
    [string]$SttModelId = "",
    [string]$SttLanguage = "en"
)

$ErrorActionPreference = "Stop"
try { Add-Type -AssemblyName "System.Net.Http" -ErrorAction Stop } catch { }

if ($Runs -lt 1) {
    throw "Runs must be at least 1."
}

if ($null -eq $Phrases -or $Phrases.Count -eq 0) {
    throw "At least one phrase is required."
}

$root = $BaseUrl.TrimEnd("/")
$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(120)

function Get-Median([double[]]$Values) {
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $count = $sorted.Count
    $mid = [int][Math]::Floor($count / 2)
    if (($count % 2) -eq 1) {
        return [double]$sorted[$mid]
    }
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

function Invoke-TtsTest([string]$Phrase, [int]$PhraseIndex) {
    $body = @{
        text = $Phrase
        requestId = "bench-tts-$PhraseIndex-$([Guid]::NewGuid().ToString('N'))"
    }
    if (-not [string]::IsNullOrWhiteSpace($TtsEngine)) { $body.engine = $TtsEngine }
    if (-not [string]::IsNullOrWhiteSpace($TtsModelId)) { $body.modelId = $TtsModelId }
    if (-not [string]::IsNullOrWhiteSpace($TtsVoiceId)) { $body.voiceId = $TtsVoiceId }

    $json = $body | ConvertTo-Json -Depth 6 -Compress
    $response = Invoke-RestMethod -Method Post -Uri "$root/tts/test" -ContentType "application/json" -Body $json
    if ($null -eq $response -or [string]::IsNullOrWhiteSpace($response.audioBase64)) {
        throw "TTS test did not return audioBase64 for phrase index $PhraseIndex."
    }

    return [Convert]::FromBase64String([string]$response.audioBase64)
}

function Invoke-SttBench([byte[]]$AudioBytes, [int]$PhraseIndex, [int]$RunIndex) {
    $content = [System.Net.Http.MultipartFormDataContent]::new()
    $audioContent = [System.Net.Http.ByteArrayContent]::new($AudioBytes)
    $response = $null
    try {
        $audioContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("audio/wav")
        $content.Add($audioContent, "audio", "bench.wav")

        $requestId = "bench-stt-$PhraseIndex-$RunIndex-$([Guid]::NewGuid().ToString('N'))"
        $content.Add([System.Net.Http.StringContent]::new($requestId), "requestId")
        if (-not [string]::IsNullOrWhiteSpace($SttEngine)) { $content.Add([System.Net.Http.StringContent]::new($SttEngine), "engine") }
        if (-not [string]::IsNullOrWhiteSpace($SttModelId)) { $content.Add([System.Net.Http.StringContent]::new($SttModelId), "modelId") }
        if (-not [string]::IsNullOrWhiteSpace($SttLanguage)) { $content.Add([System.Net.Http.StringContent]::new($SttLanguage), "language") }

        $response = $http.PostAsync("$root/stt/bench", $content).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "STT bench failed ($([int]$response.StatusCode)): $body"
        }

        return $body | ConvertFrom-Json
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $audioContent.Dispose()
        $content.Dispose()
    }
}

try {
    Write-Host "ASR endpoint benchmark"
    Write-Host "Base URL: $root"
    Write-Host "Runs per phrase: $Runs"
    Write-Host ""

    $rows = [System.Collections.Generic.List[object]]::new()
    for ($phraseIndex = 0; $phraseIndex -lt $Phrases.Count; $phraseIndex++) {
        $phrase = $Phrases[$phraseIndex]
        Write-Host ("[{0}/{1}] Preparing audio for phrase: {2}" -f ($phraseIndex + 1), $Phrases.Count, $phrase)
        $audioBytes = Invoke-TtsTest -Phrase $phrase -PhraseIndex ($phraseIndex + 1)

        for ($run = 1; $run -le $Runs; $run++) {
            $result = Invoke-SttBench -AudioBytes $audioBytes -PhraseIndex ($phraseIndex + 1) -RunIndex $run
            $wallMs = [double]$result.wallMs
            $rows.Add([pscustomobject]@{
                Phrase = $phrase
                Run = $run
                WallMs = $wallMs
                AudioSeconds = [double]$result.audioSeconds
                Rtf = [double]$result.rtf
            }) | Out-Null
            Write-Host ("  run {0}/{1}: wallMs={2} rtf={3}" -f $run, $Runs, [Math]::Round($wallMs, 2), [Math]::Round([double]$result.rtf, 4))
        }
    }

    Write-Host ""
    Write-Host "Median + p95 ASR latency (wallMs)"
    Write-Host "---------------------------------"
    foreach ($group in ($rows | Group-Object Phrase)) {
        $values = @($group.Group | ForEach-Object { [double]$_.WallMs })
        $median = Get-Median -Values $values
        $p95 = Get-Percentile -Values $values -Percent 95
        Write-Host ("- {0}" -f $group.Name)
        Write-Host ("  runs={0} median_ms={1} p95_ms={2}" -f $values.Count, [Math]::Round([double]$median, 2), [Math]::Round([double]$p95, 2))
    }

    $allValues = @($rows | ForEach-Object { [double]$_.WallMs })
    $overallMedian = Get-Median -Values $allValues
    $overallP95 = Get-Percentile -Values $allValues -Percent 95
    Write-Host ""
    Write-Host ("Overall runs={0} median_ms={1} p95_ms={2}" -f $allValues.Count, [Math]::Round([double]$overallMedian, 2), [Math]::Round([double]$overallP95, 2))
}
finally {
    $http.Dispose()
}
