#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Value
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

    switch ($normalizedBackend) {
        'lmstudio' {
            if ([string]::IsNullOrWhiteSpace($BaseUrl)) { $BaseUrl = 'http://127.0.0.1:1234' }
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
            if ($Port -eq 0) { $Port = 8080 }
            if ($ContextWindowTokens -eq 0) { $ContextWindowTokens = 16384 }
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
        model_id = $ModelId
        managed_process = $managedProcess
        executable_path = $executablePath
        executable_sha256 = $executableSha256
        model_path = $resolvedModelPath
        model_sha256 = $modelSha256
        context_window_tokens = $ContextWindowTokens
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
    $lines = & lms ps 2>&1 | ForEach-Object { [string]$_ }
    return (($lines -join "`n").Contains($ModelId))
}

function Initialize-LmStudioModel {
    param([Parameter(Mandatory = $true)][string]$ModelId)

    if ($null -eq (Get-Command lms -ErrorAction SilentlyContinue)) {
        throw "LM Studio CLI 'lms' was not found on PATH. Start LM Studio and enable its CLI."
    }
    if (Test-LmStudioModelLoaded -ModelId $ModelId) {
        Write-Host "Model '$ModelId' is already loaded." -ForegroundColor Green
        return
    }

    Write-Host "Loading '$ModelId' through LM Studio..." -ForegroundColor Yellow
    $previousErrorActionPreference = $ErrorActionPreference
    $nativePreference = Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    $previousNativePreference = if ($null -ne $nativePreference) { $nativePreference.Value } else { $null }
    try {
        $ErrorActionPreference = 'Continue'
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $false }
        & lms load $ModelId -y 2>&1 | ForEach-Object { Write-Host $_.ToString() }
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($null -ne $nativePreference) { $PSNativeCommandUseErrorActionPreference = $previousNativePreference }
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0 -or -not (Test-LmStudioModelLoaded -ModelId $ModelId)) {
        throw "LM Studio failed to load '$ModelId' reliably (exit $exitCode)."
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
        Initialize-LmStudioModel -ModelId $ProviderPlan.model_id
        Wait-ModelProviderEndpoint -ProviderPlan $ProviderPlan
        return [pscustomobject]@{ process = $null; stdout_path = $null; stderr_path = $null }
    }
    if ($ProviderPlan.backend -eq 'external') {
        Wait-ModelProviderEndpoint -ProviderPlan $ProviderPlan
        return [pscustomobject]@{ process = $null; stdout_path = $null; stderr_path = $null }
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
        return [pscustomobject]@{ process = $process; stdout_path = $stdoutPath; stderr_path = $stderrPath }
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

    if ($null -eq $ProviderSession -or $null -eq $ProviderSession.process) { return }
    $process = $ProviderSession.process
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
}

Export-ModuleMember -Function @(
    'ConvertTo-OpenAiBaseUrl',
    'New-ModelProviderPlan',
    'New-ModelIntakeSettings',
    'Start-ModelProvider',
    'Stop-ModelProvider',
    'Test-ModelProviderEndpoint',
    'Wait-ModelProviderEndpoint'
)
