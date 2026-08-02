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
    $adapterModule = Get-Module ModelProviderAdapter
    $nullablePropertyObject = [pscustomobject]@{ seed = $true }
    & $adapterModule {
        param($Object)
        Set-JsonProperty -Object $Object -Name nullable -Value $null
    } $nullablePropertyObject
    Assert-True ($nullablePropertyObject.PSObject.Properties.Name -contains 'nullable') 'Null telemetry property was not added.'
    Assert-True ($null -eq $nullablePropertyObject.nullable) 'Null telemetry property did not remain null.'
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

    $qualificationProfile = [pscustomobject]@{
        profile_id = 'synthetic-profile'
        profile_path = 'C:\profiles\synthetic.json'
        profile_sha256 = 'profile-sha'
        model_id = 'vendor/profile-model'
        selected_backend = 'external'
        context_window_tokens = 12288
        max_output_tokens = 640
        temperature = 0.25
        sources = @([pscustomobject]@{ id = 'official'; uri = 'https://example.invalid/card'; revision = 'r1' })
        recommended_settings = [pscustomobject]@{ context_window_tokens = 12288; generation = [pscustomobject]@{ temperature = 0.25 } }
        applied_settings = @([pscustomobject]@{ name = 'temperature'; value = 0.25 })
        unsupported_settings = @([pscustomobject]@{ name = 'top_k'; value = 40; reason = 'unsupported' })
        overrides = @()
        override_reason = $null
    }
    $profilePlan = New-ModelProviderPlan `
        -Backend external `
        -ProviderName compatible `
        -BaseUrl 'http://127.0.0.1:9000' `
        -ModelId 'vendor/profile-model' `
        -ContextWindowTokens 12288 `
        -QualificationProfile $qualificationProfile
    Assert-Equal 'synthetic-profile' $profilePlan.qualification_profile.profile_id 'Provider plan lost profile identity.'
    Assert-Equal 'profile-sha' $profilePlan.qualification_profile.sha256 'Provider plan lost profile hash.'
    Assert-Equal 0.25 ([double]$profilePlan.generation.temperature) 'Provider plan lost researched temperature.'
    Assert-Equal 640 ([int]$profilePlan.generation.max_output_tokens) 'Provider plan lost qualification output limit.'
    Assert-Equal 1 @($profilePlan.qualification_profile.unsupported_settings).Count 'Provider plan hid unsupported settings.'
    Assert-Throws {
        New-ModelProviderPlan -Backend external -BaseUrl 'http://127.0.0.1:9000' `
            -ModelId 'different/model' -QualificationProfile $qualificationProfile
    } 'Provider plan accepted a mismatched profile identity.'

    $lmStudio = New-ModelProviderPlan `
        -Backend lmstudio `
        -ModelId 'loaded/model' `
        -ContextWindowTokens 16384 `
        -GpuOffload max `
        -Parallel 1
    Assert-Equal 'http://127.0.0.1:1234' $lmStudio.base_url 'LM Studio default endpoint changed.'
    Assert-Equal 'http://127.0.0.1:1234/api/v1/models' $lmStudio.native_models_endpoint 'LM Studio native loaded-instance endpoint changed.'
    Assert-Equal 'lmstudio' $lmStudio.provider 'LM Studio provider identity changed.'
    Assert-Equal 16384 $lmStudio.context_window_tokens 'LM Studio context load control was not recorded.'
    Assert-Equal 'max' $lmStudio.gpu_offload 'LM Studio GPU load control was not recorded.'
    Assert-Equal 1 $lmStudio.parallel 'LM Studio parallel load control was not recorded.'
    Assert-True $lmStudio.requires_fresh_load 'Explicit LM Studio controls must require a fresh attributable load.'
    Assert-True (@($lmStudio.arguments) -contains '--context-length') 'LM Studio context was not included in the exact load arguments.'
    Assert-True (@($lmStudio.arguments) -contains '--gpu') 'LM Studio GPU control was not included in the exact load arguments.'
    Assert-True (@($lmStudio.arguments) -contains '--parallel') 'LM Studio concurrency was not included in the exact load arguments.'

    $inventory = @'
{
  "models": [
    {
      "key": "unrelated/model",
      "loaded_instances": []
    },
    {
      "key": "loaded/model",
      "quantization": { "name": "Q4_K_M", "bits_per_weight": 4 },
      "size_bytes": 123456,
      "format": "gguf",
      "loaded_instances": [
        {
          "id": "loaded/model",
          "config": { "context_length": 16384, "parallel": 1 }
        }
      ]
    }
  ]
}
'@ | ConvertFrom-Json
    $loadedInstances = @(& $adapterModule {
        param($InputInventory)
        ConvertTo-LmStudioLoadedInstances -Inventory $InputInventory
    } $inventory)
    Assert-Equal 1 $loadedInstances.Count 'Native inventory did not isolate actual loaded instances.'
    Assert-Equal 'loaded/model' $loadedInstances[0].instance_id 'Native inventory lost the loaded instance identifier.'
    Assert-Equal 16384 ([int]$loadedInstances[0].config.context_length) 'Native inventory lost effective context.'
    Assert-Equal 1 ([int]$loadedInstances[0].config.parallel) 'Native inventory lost effective parallelism.'

    $matchingObservation = [pscustomobject]@{
        target_loaded_count = 1
        target_instances = @($loadedInstances[0])
    }
    $validatedObservation = & $adapterModule {
        param($Plan, $Observation)
        Assert-LmStudioProviderObservation -ProviderPlan $Plan -Observation $Observation
    } $lmStudio $matchingObservation
    Assert-Equal 1 $validatedObservation.target_loaded_count 'Exact LM Studio load configuration was not accepted.'

    $wrongContextInventory = $inventory | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $wrongContextInventory.models[1].loaded_instances[0].config.context_length = 8192
    $wrongContextInstances = @(& $adapterModule {
        param($InputInventory)
        ConvertTo-LmStudioLoadedInstances -Inventory $InputInventory
    } $wrongContextInventory)
    Assert-Throws {
        & $adapterModule {
            param($Plan, $Observation)
            Assert-LmStudioProviderObservation -ProviderPlan $Plan -Observation $Observation
        } $lmStudio ([pscustomobject]@{ target_loaded_count = 1; target_instances = @($wrongContextInstances[0]) })
    } 'LM Studio context mismatch was accepted.'

    $validSentinelResponse = [pscustomobject]@{
        model = 'loaded/model'
        choices = @([pscustomobject]@{
            finish_reason = 'stop'
            message = [pscustomobject]@{ content = 'PROVIDER_SENTINEL_OK' }
        })
        usage = [pscustomobject]@{ prompt_tokens = 10; completion_tokens = 3; total_tokens = 13 }
    }
    $sentinelShape = & $adapterModule {
        param($Body)
        ConvertFrom-ProviderSentinelResponse -ResponseBody $Body
    } $validSentinelResponse
    Assert-Equal 20 $sentinelShape.content_chars 'Provider sentinel response content was not validated.'
    Assert-Equal 13 $sentinelShape.total_tokens 'Provider sentinel usage was not preserved.'
    Assert-Throws {
        & $adapterModule {
            ConvertFrom-ProviderSentinelResponse -ResponseBody ([pscustomobject]@{ choices = @() })
        }
    } 'An empty provider sentinel response was accepted.'

    $validToolSentinelResponse = @'
{
  "model": "loaded/model",
  "choices": [
    {
      "finish_reason": "tool_calls",
      "message": {
        "role": "assistant",
        "content": null,
        "tool_calls": [
          {
            "id": "call-1",
            "type": "function",
            "function": {
              "name": "report_provider_sentinel",
              "arguments": "{\"value\":\"PROVIDER_SENTINEL_OK\"}"
            }
          }
        ]
      }
    }
  ]
}
'@ | ConvertFrom-Json
    $toolSentinelShape = & $adapterModule {
        param($Body)
        ConvertFrom-ProviderToolSentinelResponse -ResponseBody $Body
    } $validToolSentinelResponse
    Assert-Equal 1 $toolSentinelShape.tool_call_count 'Provider tool sentinel call count was not validated.'
    Assert-Equal 'report_provider_sentinel' $toolSentinelShape.tool_name 'Provider tool sentinel name was not validated.'
    Assert-True $toolSentinelShape.exact_value_verified 'Provider tool sentinel argument value was not validated.'
    $wrongToolSentinelResponse = $validToolSentinelResponse | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $wrongToolSentinelResponse.choices[0].message.tool_calls[0].function.arguments = '{"value":"WRONG"}'
    Assert-Throws {
        & $adapterModule {
            param($Body)
            ConvertFrom-ProviderToolSentinelResponse -ResponseBody $Body
        } $wrongToolSentinelResponse
    } 'An incorrect provider tool sentinel argument was accepted.'

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

    $profileSettingsPath = Join-Path $tempRoot 'profile-settings.json'
    New-ModelIntakeSettings `
        -TemplatePath (Join-Path $repoRoot 'SirThaddeus.Settings.template.json') `
        -ProviderPlan $profilePlan `
        -GatekeeperModelId 'vendor/profile-model' `
        -DestinationPath $profileSettingsPath
    $profileSettings = Get-Content -LiteralPath $profileSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal 0.25 ([double]$profileSettings.llm.temperature) 'Profile temperature was not applied to intake settings.'
    Assert-Equal 640 ([int]$profileSettings.llm.maxTokens) 'Profile output limit was not applied to intake settings.'
    Assert-Equal 12288 ([int]$profileSettings.llm.contextWindowTokens) 'Profile context was not applied to intake settings.'

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

    function global:lms {
        Write-Error 'No models are currently loaded.' -ErrorAction Continue
        $global:LASTEXITCODE = 1
    }
    $emptyStateLoaded = & $adapterModule { Test-LmStudioModelLoaded -ModelId 'absent/model' }
    Assert-True (-not $emptyStateLoaded) 'The ordinary LM Studio empty state was treated as a lifecycle failure.'

    $global:adapterLmsArguments = @()
    function global:lms {
        $global:adapterLmsArguments = @($args)
        Write-Error 'Model unloaded.' -ErrorAction Continue
        $global:LASTEXITCODE = 0
    }
    & $adapterModule {
        Set-Item -Path Function:\Get-LmStudioProviderObservation -Value {
            param($ProviderPlan)
            return [pscustomobject]@{
                endpoint = $ProviderPlan.native_models_endpoint
                observed_utc = 'test'
                response_sha256 = 'test'
                total_loaded_count = 0
                target_loaded_count = 0
                target_instances = @()
            }
        }
    }
    $cleanupSession = [pscustomobject]@{
        process = $null
        loaded_model_id = 'owned/model'
        provider_plan = $lmStudio
        cleanup_verified = $false
    }
    Stop-ModelProvider -ProviderSession $cleanupSession
    Assert-Equal 'unload' $global:adapterLmsArguments[0] 'LM Studio cleanup did not use the exact unload command.'
    Assert-Equal 'owned/model' $global:adapterLmsArguments[1] 'LM Studio cleanup targeted the wrong model identifier.'
    Assert-True $cleanupSession.cleanup_verified 'LM Studio cleanup was not marked verified.'

    Write-Host "PASS model provider adapter ($assertions assertions)" -ForegroundColor Green
}
finally {
    Remove-Item Function:\global:lms -Force -ErrorAction SilentlyContinue
    Remove-Module ModelProviderAdapter -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
