#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$modulePath = Join-Path $PSScriptRoot 'ModelProviderAdapter.psm1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('st-provider-adapter-test-' + [guid]::NewGuid().ToString('N'))
$assertions = 0

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    $script:assertions++
    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:assertions++
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Message)
    $script:assertions++
    try {
        & $Action
    }
    catch {
        return
    }
    throw $Message
}

try {
    Import-Module $modulePath -Force
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $fakeServer = Join-Path $tempRoot 'llama-server.exe'
    $fakeModel = Join-Path $tempRoot 'model.gguf'
    [IO.File]::WriteAllBytes($fakeServer, [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes($fakeModel, [byte[]](4, 5, 6))

    $external = New-ModelProviderPlan `
        -Backend external `
        -ProviderName ollama `
        -BaseUrl 'http://127.0.0.1:11434/v1/' `
        -ModelId 'gemma3:4b'
    Assert-Equal 'external' $external.backend 'External backend identity changed.'
    Assert-Equal 'ollama' $external.provider 'External provider label changed.'
    Assert-Equal 'http://127.0.0.1:11434' $external.base_url 'OpenAI base URL was not normalized.'
    Assert-Equal 'http://127.0.0.1:11434/v1/models' $external.models_endpoint 'Models probe URL is incorrect.'
    Assert-True (-not $external.managed_process) 'External endpoints must never be process-managed.'

    $lmStudio = New-ModelProviderPlan `
        -Backend lmstudio `
        -ModelId 'loaded/model' `
        -ContextWindowTokens 16384 `
        -GpuOffload max `
        -Parallel 1
    Assert-Equal 'http://127.0.0.1:1234' $lmStudio.base_url 'LM Studio default endpoint changed.'
    Assert-Equal 'lmstudio' $lmStudio.provider 'LM Studio provider identity changed.'
    Assert-Equal 16384 $lmStudio.context_window_tokens 'LM Studio context load control was not recorded.'
    Assert-Equal 'max' $lmStudio.gpu_offload 'LM Studio GPU load control was not recorded.'
    Assert-Equal 1 $lmStudio.parallel 'LM Studio parallel load control was not recorded.'
    Assert-True $lmStudio.requires_fresh_load 'Explicit LM Studio controls must require a fresh attributable load.'
    Assert-True (@($lmStudio.arguments) -contains '--context-length') 'LM Studio context was not included in the exact load arguments.'
    Assert-True (@($lmStudio.arguments) -contains '--gpu') 'LM Studio GPU control was not included in the exact load arguments.'
    Assert-True (@($lmStudio.arguments) -contains '--parallel') 'LM Studio concurrency was not included in the exact load arguments.'

    $defaultLlama = New-ModelProviderPlan `
        -Backend llamacpp `
        -ModelId 'research/model' `
        -LlamaServerPath $fakeServer `
        -ModelPath $fakeModel
    Assert-Equal 'http://127.0.0.1:18080' $defaultLlama.base_url 'Default llama.cpp endpoint must avoid the SearXNG default port.'

    $llama = New-ModelProviderPlan `
        -Backend llamacpp `
        -ModelId 'research/model' `
        -LlamaServerPath $fakeServer `
        -ModelPath $fakeModel `
        -Port 18080 `
        -ContextWindowTokens 8192 `
        -GpuOffload max `
        -Parallel 1 `
        -StartupTimeoutSeconds 3 `
        -HashModel
    Assert-True $llama.managed_process 'llama.cpp must own exactly the process it starts.'
    Assert-Equal '039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81' $llama.executable_sha256 'Server executable SHA-256 was not recorded exactly.'
    Assert-Equal 'http://127.0.0.1:18080' $llama.base_url 'llama.cpp loopback endpoint is incorrect.'
    Assert-Equal 8192 $llama.context_window_tokens 'llama.cpp context was not preserved.'
    Assert-Equal 'max' $llama.gpu_offload 'llama.cpp GPU load control was not preserved.'
    Assert-Equal 1 $llama.parallel 'llama.cpp concurrency was not preserved.'
    Assert-Equal '787c798e39a5bc1910355bae6d0cd87a36b2e10fd0202a83e3bb6b005da83472' $llama.model_sha256 'Model SHA-256 was not recorded exactly.'
    Assert-True (@($llama.arguments) -contains '--jinja') 'llama.cpp tool-aware Jinja mode is required.'
    Assert-True (@($llama.arguments) -contains '--metrics') 'llama.cpp metrics must be enabled for research observability.'
    Assert-True (@($llama.arguments) -contains '--gpu-layers') 'llama.cpp GPU control was not included in the exact arguments.'
    Assert-True (@($llama.arguments) -contains '--flash-attn') 'llama.cpp flash-attention mode was not frozen.'

    $settingsPath = Join-Path $tempRoot 'settings.json'
    New-ModelIntakeSettings `
        -TemplatePath (Join-Path $repoRoot 'SirThaddeus.Settings.template.json') `
        -ProviderPlan $llama `
        -GatekeeperModelId 'research/model' `
        -DestinationPath $settingsPath
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal 'llamacpp' $settings.llm.provider 'Patched settings did not preserve provider identity.'
    Assert-Equal 'http://127.0.0.1:18080' $settings.llm.baseUrl 'Patched settings did not preserve the endpoint.'
    Assert-Equal 'research/model' $settings.llm.model 'Patched settings did not preserve the model alias.'
    Assert-Equal 0 ([double]$settings.llm.temperature) 'Model intake must retain deterministic temperature zero.'
    Assert-Equal 8192 ([int]$settings.llm.contextWindowTokens) 'Runtime and provider contexts must match.'
    Assert-True ([bool]$settings.llm.reusePrimaryModelForGatekeeperOnSharedEndpoint) 'A shared intake endpoint must reuse its loaded model.'

    $collidingLlama = New-ModelProviderPlan `
        -Backend llamacpp `
        -ModelId 'research/model' `
        -LlamaServerPath $fakeServer `
        -ModelPath $fakeModel `
        -Port 8080
    Assert-Throws {
        New-ModelIntakeSettings `
            -TemplatePath (Join-Path $repoRoot 'SirThaddeus.Settings.template.json') `
            -ProviderPlan $collidingLlama `
            -GatekeeperModelId 'research/model' `
            -DestinationPath (Join-Path $tempRoot 'colliding-settings.json')
    } 'A llama.cpp endpoint collision with SearXNG was accepted.'

    Assert-Throws { New-ModelProviderPlan -Backend external -BaseUrl 'file:///tmp/model' -ModelId model } 'Non-HTTP provider URL was accepted.'
    Assert-Throws { New-ModelProviderPlan -Backend external -BaseUrl 'http://localhost:1234/?secret=x' -ModelId model } 'Provider URL query was accepted.'
    Assert-Throws { New-ModelProviderPlan -Backend llamacpp -ModelId model -LlamaServerPath $fakeServer -ModelPath (Join-Path $tempRoot 'missing.gguf') } 'Missing GGUF was accepted.'

    $global:adapterLmsArguments = @()
    function global:lms {
        $global:adapterLmsArguments = @($args)
        $global:LASTEXITCODE = 0
    }
    Stop-ModelProvider -ProviderSession ([pscustomobject]@{
        process = $null
        loaded_model_id = 'owned/model'
    })
    Assert-Equal 'unload' $global:adapterLmsArguments[0] 'LM Studio cleanup did not use the exact unload command.'
    Assert-Equal 'owned/model' $global:adapterLmsArguments[1] 'LM Studio cleanup targeted the wrong model identifier.'

    Write-Host "PASS model provider adapter ($assertions assertions)" -ForegroundColor Green
}
finally {
    Remove-Item Function:\global:lms -Force -ErrorAction SilentlyContinue
    Remove-Module ModelProviderAdapter -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
