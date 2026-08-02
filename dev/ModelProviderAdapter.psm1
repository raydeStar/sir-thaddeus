#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][AllowNull()]$Value
    )

    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    }
    else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    }
}

function ConvertTo-OpenAiBaseUrl {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$BaseUrl)

    $normalized = $BaseUrl.Trim().TrimEnd('/')
    if ($normalized.EndsWith('/v1', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(0, $normalized.Length - 3).TrimEnd('/')
    }

    $uri = $null
    if (-not [Uri]::TryCreate($normalized, [UriKind]::Absolute, [ref]$uri) -or
        $null -eq $uri -or
        ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https') -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw "BaseUrl '$BaseUrl' must be an absolute HTTP(S) URL without a query or fragment."
    }

    return $normalized
}

function Resolve-RequiredFile {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Label)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label '$Path' does not exist or is not a file."
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-SafeNativeValue {
    param([Parameter(Mandatory = $true)][string]$Value, [Parameter(Mandatory = $true)][string]$Label)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Contains('"') -or $Value.Contains("`r") -or $Value.Contains("`n")) {
        throw "$Label must be non-empty and cannot contain quotes or newlines."
    }
}

function Quote-NativeValue {
    param([Parameter(Mandatory = $true)][string]$Value)
    Assert-SafeNativeValue -Value $Value -Label 'Native argument'
    return '"' + $Value + '"'
}

function Test-SameEndpointAuthority {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    $leftUri = $null
    $rightUri = $null
    if (-not [Uri]::TryCreate($Left, [UriKind]::Absolute, [ref]$leftUri) -or
        -not [Uri]::TryCreate($Right, [UriKind]::Absolute, [ref]$rightUri) -or
        $null -eq $leftUri -or $null -eq $rightUri) {
        return $false
    }

    $leftHost = $leftUri.Host.ToLowerInvariant()
    $rightHost = $rightUri.Host.ToLowerInvariant()
    if ($leftHost -in @('localhost', '127.0.0.1', '::1')) { $leftHost = 'loopback' }
    if ($rightHost -in @('localhost', '127.0.0.1', '::1')) { $rightHost = 'loopback' }

    return $leftUri.Scheme -eq $rightUri.Scheme -and
        $leftHost -eq $rightHost -and
        $leftUri.Port -eq $rightUri.Port
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Invoke-BoundedJsonRequest {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('GET', 'POST')][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        $Body,
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 10,
        [ValidateRange(1024, 16777216)][int]$MaxResponseBytes = 4194304
    )

    $request = [Net.HttpWebRequest]::Create($Uri)
    $request.Method = $Method
    $request.Timeout = $TimeoutSeconds * 1000
    $request.ReadWriteTimeout = $TimeoutSeconds * 1000
    $request.UserAgent = 'SirThaddeus-ModelIntake/1.0'
    if ($Method -eq 'POST') {
        $json = $Body | ConvertTo-Json -Depth 16 -Compress
        $requestBytes = [Text.Encoding]::UTF8.GetBytes($json)
        $request.ContentType = 'application/json'
        $request.ContentLength = $requestBytes.Length
        $requestStream = $request.GetRequestStream()
        try {
            $requestStream.Write($requestBytes, 0, $requestBytes.Length)
        }
        finally {
            $requestStream.Dispose()
        }
    }

    $response = $null
    $responseStream = $null
    $memory = $null
    try {
        $response = [Net.HttpWebResponse]$request.GetResponse()
        if ($response.ContentLength -gt $MaxResponseBytes) {
            throw "Provider response from '$Uri' declared $($response.ContentLength) bytes, above the $MaxResponseBytes-byte limit."
        }

        $responseStream = $response.GetResponseStream()
        $memory = New-Object IO.MemoryStream
        $buffer = New-Object byte[] 8192
        $total = 0
        while (($read = $responseStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $total += $read
            if ($total -gt $MaxResponseBytes) {
                throw "Provider response from '$Uri' exceeded the $MaxResponseBytes-byte limit."
            }
            $memory.Write($buffer, 0, $read)
        }

        $responseBytes = $memory.ToArray()
        $responseText = [Text.Encoding]::UTF8.GetString($responseBytes)
        if ([string]::IsNullOrWhiteSpace($responseText)) {
            throw "Provider response from '$Uri' was empty."
        }
        try {
            $responseBody = $responseText | ConvertFrom-Json
        }
        catch {
            throw "Provider response from '$Uri' was not valid JSON: $($_.Exception.Message)"
        }

        return [pscustomobject]@{
            status_code = [int]$response.StatusCode
            body = $responseBody
            body_bytes = $responseBytes.Length
            body_sha256 = Get-Sha256Hex -Bytes $responseBytes
        }
    }
    catch {
        throw "Provider request '$Method $Uri' failed: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $memory) { $memory.Dispose() }
        if ($null -ne $responseStream) { $responseStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
    }
}

function ConvertTo-LmStudioLoadedInstances {
    param([Parameter(Mandatory = $true)]$Inventory)

    if (-not ($Inventory.PSObject.Properties.Name -contains 'models') -or $null -eq $Inventory.models) {
        throw 'LM Studio native inventory did not contain a models array.'
    }

    $instances = New-Object Collections.ArrayList
    foreach ($model in @($Inventory.models)) {
        if ($null -eq $model -or -not ($model.PSObject.Properties.Name -contains 'loaded_instances')) { continue }
        foreach ($instance in @($model.loaded_instances)) {
            if ($null -eq $instance) { continue }
            [void]$instances.Add([pscustomobject]@{
                model_key = [string]$model.key
                instance_id = [string]$instance.id
                config = $instance.config
                quantization = $model.quantization
                size_bytes = $model.size_bytes
                format = [string]$model.format
            })
        }
    }
    return $instances.ToArray()
}

function Get-LmStudioProviderObservation {
    param([Parameter(Mandatory = $true)]$ProviderPlan)

    $endpoint = "$($ProviderPlan.base_url)/api/v1/models"
    $response = Invoke-BoundedJsonRequest -Method GET -Uri $endpoint -TimeoutSeconds 10
    $allInstances = @(ConvertTo-LmStudioLoadedInstances -Inventory $response.body)
    $targetInstances = @($allInstances | Where-Object {
        [string]::Equals([string]$_.model_key, [string]$ProviderPlan.model_id, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals([string]$_.instance_id, [string]$ProviderPlan.model_id, [StringComparison]::OrdinalIgnoreCase)
    })

    return [pscustomobject]@{
        endpoint = $endpoint
        observed_utc = (Get-Date).ToUniversalTime().ToString('O')
        response_sha256 = $response.body_sha256
        total_loaded_count = $allInstances.Count
        target_loaded_count = $targetInstances.Count
        target_instances = @($targetInstances)
    }
}

function Assert-LmStudioProviderObservation {
    param(
        [Parameter(Mandatory = $true)]$ProviderPlan,
        [Parameter(Mandatory = $true)]$Observation
    )

    if ([int]$Observation.target_loaded_count -ne 1) {
        throw "LM Studio must expose exactly one loaded instance for '$($ProviderPlan.model_id)'; observed $($Observation.target_loaded_count)."
    }
    $instance = @($Observation.target_instances)[0]
    if ($null -eq $instance.config) {
        throw "LM Studio loaded instance '$($instance.instance_id)' did not expose its effective configuration."
    }
    if ([int]$ProviderPlan.context_window_tokens -gt 0) {
        if (-not ($instance.config.PSObject.Properties.Name -contains 'context_length') -or
            [int]$instance.config.context_length -ne [int]$ProviderPlan.context_window_tokens) {
            $observedContext = if ($instance.config.PSObject.Properties.Name -contains 'context_length') { [string]$instance.config.context_length } else { '<missing>' }
            throw "LM Studio loaded '$($ProviderPlan.model_id)' with context $observedContext; expected $($ProviderPlan.context_window_tokens)."
        }
    }
    if ([int]$ProviderPlan.parallel -gt 0) {
        if (-not ($instance.config.PSObject.Properties.Name -contains 'parallel') -or
            [int]$instance.config.parallel -ne [int]$ProviderPlan.parallel) {
            $observedParallel = if ($instance.config.PSObject.Properties.Name -contains 'parallel') { [string]$instance.config.parallel } else { '<missing>' }
            throw "LM Studio loaded '$($ProviderPlan.model_id)' with parallel $observedParallel; expected $($ProviderPlan.parallel)."
        }
    }
    return $Observation
}

function Get-OpenAiModelIds {
    param([Parameter(Mandatory = $true)]$ProviderPlan)

    $response = Invoke-BoundedJsonRequest -Method GET -Uri ([string]$ProviderPlan.models_endpoint) -TimeoutSeconds 10
    if (-not ($response.body.PSObject.Properties.Name -contains 'data') -or $null -eq $response.body.data) {
        throw "Provider endpoint '$($ProviderPlan.models_endpoint)' did not return an OpenAI-compatible data array."
    }
    return @($response.body.data | ForEach-Object { [string]$_.id })
}

function New-ModelProviderPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('lmstudio', 'llamacpp', 'external')]
        [string]$Backend,

        [Parameter(Mandatory = $true)]
        [string]$ModelId,

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

        [switch]$HashModel
    )

    Assert-SafeNativeValue -Value $ModelId -Label 'ModelId'
    $normalizedBackend = $Backend.ToLowerInvariant()
    $effectiveProvider = if ([string]::IsNullOrWhiteSpace($ProviderName)) { $normalizedBackend } else { $ProviderName.Trim() }
    Assert-SafeNativeValue -Value $effectiveProvider -Label 'ProviderName'

    $managedProcess = $false
    $executablePath = $null
    $executableSha256 = $null
    $resolvedModelPath = $null
    $modelSha256 = $null
    $arguments = @()
    $requiresFreshLoad = $false

    switch ($normalizedBackend) {
        'lmstudio' {
            if ([string]::IsNullOrWhiteSpace($BaseUrl)) { $BaseUrl = 'http://127.0.0.1:1234' }
            $arguments = @('load', (Quote-NativeValue -Value $ModelId), '-y', '--identifier', (Quote-NativeValue -Value $ModelId))
            if ($ContextWindowTokens -gt 0) {
                $arguments += @('--context-length', [string]$ContextWindowTokens)
                $requiresFreshLoad = $true
            }
            if (-not [string]::IsNullOrWhiteSpace($GpuOffload) -and $GpuOffload -ne 'auto') {
                $arguments += @('--gpu', $GpuOffload)
                $requiresFreshLoad = $true
            }
            if ($Parallel -gt 0) {
                $arguments += @('--parallel', [string]$Parallel)
                $requiresFreshLoad = $true
            }
            $lmsCommand = Get-Command lms -ErrorAction SilentlyContinue
            if ($null -ne $lmsCommand -and (Test-Path -LiteralPath $lmsCommand.Source -PathType Leaf)) {
                $executablePath = $lmsCommand.Source
                $executableSha256 = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        'external' {
            if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
                throw '-BaseUrl is required for the external backend.'
            }
        }
        'llamacpp' {
            $managedProcess = $true
            $executablePath = Resolve-RequiredFile -Path $LlamaServerPath -Label 'LlamaServerPath'
            $executableSha256 = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
            $resolvedModelPath = Resolve-RequiredFile -Path $ModelPath -Label 'ModelPath'
            if ($Port -eq 0) { $Port = 18080 }
            if ($ContextWindowTokens -eq 0) { $ContextWindowTokens = 16384 }
            if ($Parallel -eq 0) { $Parallel = 1 }
            $BaseUrl = "http://127.0.0.1:$Port"
            if ($HashModel) {
                $modelSha256 = (Get-FileHash -LiteralPath $resolvedModelPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            $arguments = @(
                '-m', (Quote-NativeValue -Value $resolvedModelPath),
                '--alias', (Quote-NativeValue -Value $ModelId),
                '--host', '127.0.0.1',
                '--port', [string]$Port,
                '-c', [string]$ContextWindowTokens,
                '--gpu-layers', $(
                    if ($GpuOffload -eq 'off') { '0' }
                    elseif ($GpuOffload -eq 'max') { 'all' }
                    else { 'auto' }
                ),
                '--parallel', [string]$Parallel,
                '--flash-attn', 'auto',
                '--jinja',
                '--metrics'
            )
        }
    }

    $normalizedBaseUrl = ConvertTo-OpenAiBaseUrl -BaseUrl $BaseUrl
    return [pscustomobject]@{
        schema_version = 1
        created_utc = (Get-Date).ToUniversalTime().ToString('O')
        backend = $normalizedBackend
        provider = $effectiveProvider
        base_url = $normalizedBaseUrl
        models_endpoint = "$normalizedBaseUrl/v1/models"
        native_models_endpoint = if ($normalizedBackend -eq 'lmstudio') { "$normalizedBaseUrl/api/v1/models" } else { $null }
        model_id = $ModelId
        managed_process = $managedProcess
        executable_path = $executablePath
        executable_sha256 = $executableSha256
        model_path = $resolvedModelPath
        model_sha256 = $modelSha256
        context_window_tokens = $ContextWindowTokens
        gpu_offload = if ([string]::IsNullOrWhiteSpace($GpuOffload)) { 'auto' } else { $GpuOffload }
        parallel = $Parallel
        requires_fresh_load = $requiresFreshLoad
        startup_timeout_seconds = $StartupTimeoutSeconds
        arguments = @($arguments)
    }
}

function New-ModelIntakeSettings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$TemplatePath,
        [Parameter(Mandatory = $true)]$ProviderPlan,
        [Parameter(Mandatory = $true)][string]$GatekeeperModelId,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $resolvedTemplate = Resolve-RequiredFile -Path $TemplatePath -Label 'SettingsTemplate'
    $settings = Get-Content -LiteralPath $resolvedTemplate -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not ($settings.PSObject.Properties.Name -contains 'llm') -or $null -eq $settings.llm) {
        $settings | Add-Member -NotePropertyName llm -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    if ($ProviderPlan.backend -eq 'llamacpp' -and
        ($settings.PSObject.Properties.Name -contains 'webSearch') -and
        $null -ne $settings.webSearch -and
        ($settings.webSearch.PSObject.Properties.Name -contains 'searxngBaseUrl') -and
        -not [string]::IsNullOrWhiteSpace([string]$settings.webSearch.searxngBaseUrl) -and
        (Test-SameEndpointAuthority -Left $ProviderPlan.base_url -Right ([string]$settings.webSearch.searxngBaseUrl))) {
        throw "The managed llama.cpp endpoint '$($ProviderPlan.base_url)' collides with webSearch.searxngBaseUrl '$($settings.webSearch.searxngBaseUrl)'. Choose another -Port."
    }

    Set-JsonProperty -Object $settings.llm -Name provider -Value $ProviderPlan.provider
    Set-JsonProperty -Object $settings.llm -Name baseUrl -Value $ProviderPlan.base_url
    Set-JsonProperty -Object $settings.llm -Name model -Value $ProviderPlan.model_id
    Set-JsonProperty -Object $settings.llm -Name gatekeeperBaseUrl -Value $ProviderPlan.base_url
    Set-JsonProperty -Object $settings.llm -Name gatekeeperModelId -Value $GatekeeperModelId
    Set-JsonProperty -Object $settings.llm -Name reusePrimaryModelForGatekeeperOnSharedEndpoint -Value $true
    Set-JsonProperty -Object $settings.llm -Name temperature -Value 0
    if ([int]$ProviderPlan.context_window_tokens -gt 0) {
        Set-JsonProperty -Object $settings.llm -Name contextWindowTokens -Value ([int]$ProviderPlan.context_window_tokens)
    }

    $destinationDirectory = Split-Path -Parent $DestinationPath
    if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($DestinationPath, ($settings | ConvertTo-Json -Depth 32), $utf8NoBom)
}

function Test-LmStudioModelLoaded {
    param([Parameter(Mandatory = $true)][string]$ModelId)

    $previousErrorActionPreference = $ErrorActionPreference
    $nativePreference = Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    $previousNativePreference = if ($null -ne $nativePreference) { $nativePreference.Value } else { $null }
    try {
        # `lms ps` reports the ordinary empty state on stderr with a non-zero
        # exit code. Capture it explicitly so strict PowerShell error handling
        # does not turn "nothing loaded" into a lifecycle failure.
        $ErrorActionPreference = 'Continue'
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $false }
        $lines = @(& lms ps 2>&1 | ForEach-Object { [string]$_ })
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $previousNativePreference }
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $output = $lines -join "`n"
    if ($exitCode -ne 0 -and -not $output.Contains('No models are currently loaded.')) {
        throw "LM Studio failed to list loaded models (exit $exitCode): $output"
    }
    return $output.Contains($ModelId)
}

function Initialize-LmStudioModel {
    param([Parameter(Mandatory = $true)]$ProviderPlan)

    if ($null -eq (Get-Command lms -ErrorAction SilentlyContinue)) {
        throw "LM Studio CLI 'lms' was not found on PATH. Start LM Studio and enable its CLI."
    }
    $before = Get-LmStudioProviderObservation -ProviderPlan $ProviderPlan
    if ([int]$before.target_loaded_count -gt 0) {
        if ($ProviderPlan.requires_fresh_load) {
            throw "Model '$($ProviderPlan.model_id)' is already loaded, so its context/GPU/parallel settings cannot be attributed. Unload it before an exact-control run."
        }
        Write-Host "Model '$($ProviderPlan.model_id)' is already loaded." -ForegroundColor Green
        return [pscustomobject]@{
            loaded_by_adapter = $false
            loaded_instance_id = $null
            observation = $before
        }
    }
    if ($ProviderPlan.requires_fresh_load -and [int]$before.total_loaded_count -gt 0) {
        throw "LM Studio has $($before.total_loaded_count) unrelated loaded instance(s). Exact-control intake will not add, evict, or obscure another model."
    }

    Write-Host "Loading '$($ProviderPlan.model_id)' through LM Studio with the recorded provider plan..." -ForegroundColor Yellow
    $previousErrorActionPreference = $ErrorActionPreference
    $nativePreference = Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    $previousNativePreference = if ($null -ne $nativePreference) { $nativePreference.Value } else { $null }
    try {
        $ErrorActionPreference = 'Continue'
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $false }
        & lms @($ProviderPlan.arguments) 2>&1 | ForEach-Object { Write-Host $_.ToString() }
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $previousNativePreference }
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "LM Studio failed to load '$($ProviderPlan.model_id)' reliably (exit $exitCode)."
    }
    $after = Assert-LmStudioProviderObservation `
        -ProviderPlan $ProviderPlan `
        -Observation (Get-LmStudioProviderObservation -ProviderPlan $ProviderPlan)
    return [pscustomobject]@{
        loaded_by_adapter = $true
        loaded_instance_id = [string]@($after.target_instances)[0].instance_id
        observation = $after
    }
}

function Remove-LmStudioModel {
    param(
        [Parameter(Mandatory = $true)]$ProviderPlan,
        [Parameter(Mandatory = $true)][string]$InstanceId
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $nativePreference = Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    $previousNativePreference = if ($null -ne $nativePreference) { $nativePreference.Value } else { $null }
    try {
        # The CLI can print a successful unload on stderr. The native exit code,
        # not the PowerShell stream chosen by the CLI, is authoritative.
        $ErrorActionPreference = 'Continue'
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $false }
        & lms unload $InstanceId 2>&1 | ForEach-Object { Write-Host $_.ToString() }
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $previousNativePreference }
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "LM Studio failed to unload adapter-owned model '$InstanceId' (exit $exitCode)."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $observation = Get-LmStudioProviderObservation -ProviderPlan $ProviderPlan
        if ([int]$observation.target_loaded_count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "LM Studio still reports adapter-owned model '$InstanceId' loaded after cleanup."
}

function ConvertFrom-ProviderSentinelResponse {
    param([Parameter(Mandatory = $true)]$ResponseBody)

    if (-not ($ResponseBody.PSObject.Properties.Name -contains 'choices')) {
        throw 'Provider sentinel response did not contain choices.'
    }
    $choices = @($ResponseBody.choices)
    if ($choices.Count -lt 1 -or $null -eq $choices[0].message) {
        throw 'Provider sentinel response did not contain an assistant message.'
    }
    $message = $choices[0].message
    $content = if ($message.PSObject.Properties.Name -contains 'content') { [string]$message.content } else { '' }
    $toolCalls = if ($message.PSObject.Properties.Name -contains 'tool_calls') { @($message.tool_calls).Count } else { 0 }
    if ([string]::IsNullOrWhiteSpace($content) -and $toolCalls -eq 0) {
        throw 'Provider sentinel assistant message contained neither text nor tool calls.'
    }

    $usage = if ($ResponseBody.PSObject.Properties.Name -contains 'usage') { $ResponseBody.usage } else { $null }
    return [pscustomobject]@{
        response_model = if ($ResponseBody.PSObject.Properties.Name -contains 'model') { [string]$ResponseBody.model } else { $null }
        finish_reason = if ($choices[0].PSObject.Properties.Name -contains 'finish_reason') { [string]$choices[0].finish_reason } else { $null }
        content_chars = $content.Length
        content_sha256 = if ($content.Length -gt 0) { Get-Sha256Hex -Bytes ([Text.Encoding]::UTF8.GetBytes($content)) } else { $null }
        tool_call_count = $toolCalls
        prompt_tokens = if ($null -ne $usage -and $usage.PSObject.Properties.Name -contains 'prompt_tokens') { [int]$usage.prompt_tokens } else { $null }
        completion_tokens = if ($null -ne $usage -and $usage.PSObject.Properties.Name -contains 'completion_tokens') { [int]$usage.completion_tokens } else { $null }
        total_tokens = if ($null -ne $usage -and $usage.PSObject.Properties.Name -contains 'total_tokens') { [int]$usage.total_tokens } else { $null }
    }
}

function ConvertFrom-ProviderToolSentinelResponse {
    param([Parameter(Mandatory = $true)]$ResponseBody)

    if (-not ($ResponseBody.PSObject.Properties.Name -contains 'choices')) {
        throw 'Provider tool sentinel response did not contain choices.'
    }
    $choices = @($ResponseBody.choices)
    if ($choices.Count -ne 1 -or $null -eq $choices[0].message) {
        throw "Provider tool sentinel expected one assistant choice; observed $($choices.Count)."
    }
    $message = $choices[0].message
    $toolCalls = @()
    if ($message.PSObject.Properties.Name -contains 'tool_calls') {
        $toolCalls = @($message.tool_calls)
    }
    if ($toolCalls.Count -ne 1) {
        throw "Provider tool sentinel expected exactly one tool call; observed $($toolCalls.Count)."
    }
    $toolCall = $toolCalls[0]
    if ($null -eq $toolCall.function -or [string]$toolCall.function.name -ne 'report_provider_sentinel') {
        $observedName = if ($null -ne $toolCall.function) { [string]$toolCall.function.name } else { '<missing>' }
        throw "Provider tool sentinel called '$observedName'; expected 'report_provider_sentinel'."
    }
    try {
        $arguments = [string]$toolCall.function.arguments | ConvertFrom-Json
    }
    catch {
        throw "Provider tool sentinel arguments were not valid JSON: $($_.Exception.Message)"
    }
    if ($null -eq $arguments -or -not ($arguments.PSObject.Properties.Name -contains 'value') -or
        [string]$arguments.value -ne 'PROVIDER_SENTINEL_OK') {
        throw "Provider tool sentinel did not preserve the exact required value."
    }
    $argumentBytes = [Text.Encoding]::UTF8.GetBytes([string]$toolCall.function.arguments)
    return [pscustomobject]@{
        response_model = if ($ResponseBody.PSObject.Properties.Name -contains 'model') { [string]$ResponseBody.model } else { $null }
        finish_reason = if ($choices[0].PSObject.Properties.Name -contains 'finish_reason') { [string]$choices[0].finish_reason } else { $null }
        tool_call_count = 1
        tool_name = [string]$toolCall.function.name
        arguments_sha256 = Get-Sha256Hex -Bytes $argumentBytes
        exact_value_verified = $true
    }
}

function Invoke-ModelProviderSentinel {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$ProviderPlan,
        $ProviderSession,
        [ValidateSet('chat', 'tool')][string]$Mode = 'chat',
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 60
    )

    if ($null -ne $ProviderSession) {
        Set-JsonProperty -Object $ProviderSession -Name sentinel_request_attempted -Value $false
        Set-JsonProperty -Object $ProviderSession -Name sentinel_response_observation -Value $null
    }

    if ($Mode -eq 'tool') {
        $requestBody = [ordered]@{
            model = [string]$ProviderPlan.model_id
            messages = @([ordered]@{
                role = 'user'
                content = 'Call report_provider_sentinel with value PROVIDER_SENTINEL_OK. Do not answer in text.'
            })
            tools = @([ordered]@{
                type = 'function'
                function = [ordered]@{
                    name = 'report_provider_sentinel'
                    description = 'Records the provider protocol sentinel value.'
                    parameters = [ordered]@{
                        type = 'object'
                        properties = [ordered]@{
                            value = [ordered]@{ type = 'string' }
                        }
                        required = @('value')
                        additionalProperties = $false
                    }
                }
            })
            tool_choice = 'required'
            temperature = 0
            max_tokens = 64
            stream = $false
        }
    }
    else {
        $requestBody = [ordered]@{
            model = [string]$ProviderPlan.model_id
            messages = @([ordered]@{ role = 'user'; content = 'Reply with exactly PROVIDER_SENTINEL_OK.' })
            temperature = 0
            max_tokens = 16
            stream = $false
        }
    }
    $requestBytes = [Text.Encoding]::UTF8.GetBytes(($requestBody | ConvertTo-Json -Depth 8 -Compress))
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ($null -ne $ProviderSession) { $ProviderSession.sentinel_request_attempted = $true }
        $response = Invoke-BoundedJsonRequest `
            -Method POST `
            -Uri "$($ProviderPlan.base_url)/v1/chat/completions" `
            -Body $requestBody `
            -TimeoutSeconds $TimeoutSeconds
    }
    finally {
        $timer.Stop()
    }
    if ($null -ne $ProviderSession) {
        $ProviderSession.sentinel_response_observation = [pscustomobject]@{
            status_code = [int]$response.status_code
            body_bytes = [int]$response.body_bytes
            body_sha256 = [string]$response.body_sha256
            elapsed_ms = [Math]::Round($timer.Elapsed.TotalMilliseconds, 3)
        }
    }
    $shape = if ($Mode -eq 'tool') {
        ConvertFrom-ProviderToolSentinelResponse -ResponseBody $response.body
    }
    else {
        ConvertFrom-ProviderSentinelResponse -ResponseBody $response.body
    }

    $postRequestObservation = $null
    if ($ProviderPlan.backend -eq 'lmstudio') {
        $postRequestObservation = Assert-LmStudioProviderObservation `
            -ProviderPlan $ProviderPlan `
            -Observation (Get-LmStudioProviderObservation -ProviderPlan $ProviderPlan)
    }
    elseif ($ProviderPlan.backend -eq 'llamacpp') {
        if ($null -eq $ProviderSession -or $null -eq $ProviderSession.process -or $ProviderSession.process.HasExited) {
            throw 'Managed llama.cpp process was not alive after the provider sentinel.'
        }
    }

    return [pscustomobject]@{
        schema_version = 1
        status = 'passed'
        completed_utc = (Get-Date).ToUniversalTime().ToString('O')
        backend = [string]$ProviderPlan.backend
        provider = [string]$ProviderPlan.provider
        mode = $Mode
        base_url = [string]$ProviderPlan.base_url
        model_id = [string]$ProviderPlan.model_id
        request_sha256 = Get-Sha256Hex -Bytes $requestBytes
        response_status_code = [int]$response.status_code
        response_body_bytes = [int]$response.body_bytes
        response_body_sha256 = [string]$response.body_sha256
        elapsed_ms = [Math]::Round($timer.Elapsed.TotalMilliseconds, 3)
        response = $shape
        post_request_observation = $postRequestObservation
    }
}

function Test-ModelProviderEndpoint {
    param([Parameter(Mandatory = $true)]$ProviderPlan)
    $response = $null
    try {
        # Provider responses are untrusted. Read only the status/headers rather
        # than buffering an arbitrary model-list body into the test process.
        $request = [System.Net.HttpWebRequest]::Create([string]$ProviderPlan.models_endpoint)
        $request.Method = 'GET'
        $request.Timeout = 5000
        $request.ReadWriteTimeout = 5000
        $request.UserAgent = 'SirThaddeus-ModelIntake/1.0'
        $response = [System.Net.HttpWebResponse]$request.GetResponse()
        $statusCode = [int]$response.StatusCode
        return ($statusCode -ge 200 -and $statusCode -lt 300)
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
    }
}

function Wait-ModelProviderEndpoint {
    param(
        [Parameter(Mandatory = $true)]$ProviderPlan,
        [System.Diagnostics.Process]$Process
    )

    $deadline = [DateTime]::UtcNow.AddSeconds([int]$ProviderPlan.startup_timeout_seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -ne $Process -and $Process.HasExited) {
            throw "Provider process $($Process.Id) exited with code $($Process.ExitCode) before $($ProviderPlan.models_endpoint) became ready."
        }
        if (Test-ModelProviderEndpoint -ProviderPlan $ProviderPlan) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Provider endpoint '$($ProviderPlan.models_endpoint)' was not ready within $($ProviderPlan.startup_timeout_seconds) seconds."
}

function Start-ModelProvider {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$ProviderPlan,
        [Parameter(Mandatory = $true)][string]$LogDirectory
    )

    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    if ($ProviderPlan.backend -eq 'lmstudio') {
        $initialization = Initialize-LmStudioModel -ProviderPlan $ProviderPlan
        try {
            Wait-ModelProviderEndpoint -ProviderPlan $ProviderPlan
            return [pscustomobject]@{
                process = $null
                stdout_path = $null
                stderr_path = $null
                loaded_model_id = if ($initialization.loaded_by_adapter) { $initialization.loaded_instance_id } else { $null }
                provider_plan = $ProviderPlan
                ownership_verified = [bool]$initialization.loaded_by_adapter
                provider_observation = $initialization.observation
                cleanup_verified = $false
            }
        }
        catch {
            if ($initialization.loaded_by_adapter) {
                Remove-LmStudioModel -ProviderPlan $ProviderPlan -InstanceId $initialization.loaded_instance_id
            }
            throw
        }
    }
    if ($ProviderPlan.backend -eq 'external') {
        Wait-ModelProviderEndpoint -ProviderPlan $ProviderPlan
        return [pscustomobject]@{ process = $null; stdout_path = $null; stderr_path = $null; loaded_model_id = $null }
    }

    if (Test-ModelProviderEndpoint -ProviderPlan $ProviderPlan) {
        throw "A provider is already responding at '$($ProviderPlan.models_endpoint)'. Choose another -Port so lifecycle ownership remains unambiguous."
    }

    $stdoutPath = Join-Path $LogDirectory 'llama-server.stdout.log'
    $stderrPath = Join-Path $LogDirectory 'llama-server.stderr.log'
    $process = Start-Process `
        -FilePath $ProviderPlan.executable_path `
        -ArgumentList @($ProviderPlan.arguments) `
        -WorkingDirectory (Split-Path -Parent $ProviderPlan.executable_path) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    try {
        Wait-ModelProviderEndpoint -ProviderPlan $ProviderPlan -Process $process
        $modelIds = @(Get-OpenAiModelIds -ProviderPlan $ProviderPlan)
        if (-not ($modelIds -contains [string]$ProviderPlan.model_id)) {
            throw "Managed llama.cpp endpoint did not advertise expected model alias '$($ProviderPlan.model_id)'."
        }
        return [pscustomobject]@{
            process = $process
            stdout_path = $stdoutPath
            stderr_path = $stderrPath
            loaded_model_id = $null
            provider_plan = $ProviderPlan
            ownership_verified = $true
            provider_observation = [pscustomobject]@{
                process_id = $process.Id
                model_ids = @($modelIds)
                verified_utc = (Get-Date).ToUniversalTime().ToString('O')
            }
            cleanup_verified = $false
        }
    }
    catch {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
        throw
    }
}

function Stop-ModelProvider {
    [CmdletBinding()]
    param($ProviderSession)

    if ($null -eq $ProviderSession) { return }
    if (-not [string]::IsNullOrWhiteSpace([string]$ProviderSession.loaded_model_id)) {
        Remove-LmStudioModel -ProviderPlan $ProviderSession.provider_plan -InstanceId $ProviderSession.loaded_model_id
        $ProviderSession.cleanup_verified = $true
    }
    if ($null -eq $ProviderSession.process) { return }
    $process = $ProviderSession.process
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
    $ProviderSession.cleanup_verified = $process.HasExited
}

Export-ModuleMember -Function @(
    'ConvertTo-OpenAiBaseUrl',
    'New-ModelProviderPlan',
    'New-ModelIntakeSettings',
    'Start-ModelProvider',
    'Stop-ModelProvider',
    'Invoke-ModelProviderSentinel',
    'Test-ModelProviderEndpoint',
    'Wait-ModelProviderEndpoint'
)
