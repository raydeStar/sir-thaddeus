#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'ModelQualificationProfile.psm1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('st-model-profile-test-' + [guid]::NewGuid().ToString('N'))
$assertions = 0

function Assert-Equal { param($Expected, $Actual, [string]$Message); $script:assertions++; if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." } }
function Assert-True { param([bool]$Condition, [string]$Message); $script:assertions++; if (-not $Condition) { throw $Message } }
function Assert-Throws { param([scriptblock]$Action, [string]$Message); $script:assertions++; try { & $Action } catch { return }; throw $Message }

function Write-Profile {
    param([string]$Path, [string]$ProfileId, [string]$ModelId, [string]$Backend, [double]$Temperature, [string]$ExtraName)
    $profile = [ordered]@{
        schema_version = 1
        profile_id = $ProfileId
        model = [ordered]@{ id = $ModelId; family = 'synthetic-family' }
        sources = @([ordered]@{
            id = 'official-model-card'
            uri = 'https://example.invalid/models/card'
            revision = 'revision-1'
            retrieved_utc = '2026-08-02T00:00:00Z'
        })
        runtime_support = @([ordered]@{ backend = $Backend; source_id = 'official-model-card' })
        recommendations = [ordered]@{
            source_ids = @('official-model-card')
            context_window_tokens = 8192
            generation = [ordered]@{ temperature = $Temperature; $ExtraName = 42 }
        }
        qualification = [ordered]@{
            source_ids = @('official-model-card')
            context_window_tokens = 8192
            max_output_tokens = 512
            generation = [ordered]@{ temperature = $Temperature; $ExtraName = 42 }
        }
    }
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, ($profile | ConvertTo-Json -Depth 10), $utf8NoBom)
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Import-Module $modulePath -Force

    $firstPath = Join-Path $tempRoot 'first.json'
    Write-Profile $firstPath 'synthetic-a' 'vendor/model-a' 'llamacpp' 0.1 'top_k'
    $first = Import-ModelQualificationProfile -Path $firstPath -Backend llamacpp
    Assert-Equal 'vendor/model-a' $first.model_id 'Profile compiler changed model identity.'
    Assert-Equal 0.1 ([double]$first.temperature) 'Profile compiler changed researched temperature.'
    Assert-Equal 8192 ([int]$first.context_window_tokens) 'Profile compiler changed qualification context.'
    Assert-Equal 3 @($first.applied_settings).Count 'Applied setting list is incomplete.'
    Assert-Equal 1 @($first.unsupported_settings).Count 'Unsupported generation setting was not reported.'
    Assert-Equal 'top_k' $first.unsupported_settings[0].name 'Unsupported setting identity changed.'
    Assert-Equal 0 @($first.overrides).Count 'Matching researched settings were marked as overrides.'

    $secondPath = Join-Path $tempRoot 'second.json'
    Write-Profile $secondPath 'synthetic-b' 'another/model-b' 'external' 0.7 'frequency_penalty'
    $second = Import-ModelQualificationProfile -Path $secondPath -Backend external
    Assert-Equal 'another/model-b' $second.model_id 'The same compiler did not accept an unrelated model.'
    Assert-Equal 'external' $second.selected_backend 'Backend support selection changed.'
    Assert-True ($first.profile_sha256 -ne $second.profile_sha256) 'Distinct profiles produced the same artifact identity.'

    Assert-Throws { Import-ModelQualificationProfile -Path $firstPath -Backend external } 'Undocumented backend support was accepted.'
    $overridePath = Join-Path $tempRoot 'override.json'
    Write-Profile $overridePath 'synthetic-override' 'vendor/model-override' 'llamacpp' 0.1 'top_k'
    $overrideProfile = Get-Content $overridePath -Raw | ConvertFrom-Json
    $overrideProfile.qualification.generation.temperature = 0.0
    $overrideProfile | ConvertTo-Json -Depth 10 | Set-Content $overridePath -Encoding UTF8
    Assert-Throws { Import-ModelQualificationProfile -Path $overridePath -Backend llamacpp } 'Unexplained recommendation override was accepted.'
    $overrideProfile.qualification | Add-Member -NotePropertyName override_reason -NotePropertyValue 'Frozen cross-model control.' -Force
    $overrideProfile | ConvertTo-Json -Depth 10 | Set-Content $overridePath -Encoding UTF8
    $compiledOverride = Import-ModelQualificationProfile -Path $overridePath -Backend llamacpp
    Assert-Equal 1 @($compiledOverride.overrides).Count 'Explained recommendation override was not recorded.'
    Assert-Equal 'Frozen cross-model control.' $compiledOverride.override_reason 'Override rationale was lost.'
    Write-Profile (Join-Path $tempRoot 'duplicate.json') 'synthetic-c' 'vendor/model-c' 'llamacpp' 0.2 'top_k'
    $duplicate = Get-Content (Join-Path $tempRoot 'duplicate.json') -Raw | ConvertFrom-Json
    $duplicate.sources = @($duplicate.sources[0], $duplicate.sources[0])
    $duplicate | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $tempRoot 'duplicate.json') -Encoding UTF8
    Assert-Throws { Import-ModelQualificationProfile -Path (Join-Path $tempRoot 'duplicate.json') -Backend llamacpp } 'Duplicate provenance ids were accepted.'

    $fakeServer = Join-Path $tempRoot 'llama-server.exe'
    $fakeModel = Join-Path $tempRoot 'model.gguf'
    [IO.File]::WriteAllBytes($fakeServer, [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes($fakeModel, [byte[]](4, 5, 6))
    $intakeArtifacts = Join-Path $tempRoot 'intake-artifacts'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'model-intake.ps1') `
        -ProfilePath $firstPath `
        -ArtifactRoot $intakeArtifacts `
        -Backend llamacpp `
        -LlamaServerPath $fakeServer `
        -ModelPath $fakeModel `
        -SettingsTemplate (Join-Path $PSScriptRoot '..\SirThaddeus.Settings.template.json') `
        -PlanOnly
    if ($LASTEXITCODE -ne 0) { throw "Profile-backed plan-only intake failed with exit code $LASTEXITCODE." }
    $providerPlanPath = Get-ChildItem -LiteralPath $intakeArtifacts -Recurse -Filter provider-plan.json -File |
        Select-Object -ExpandProperty FullName -First 1
    Assert-True (-not [string]::IsNullOrWhiteSpace($providerPlanPath)) 'Plan-only intake did not write a provider plan.'
    $providerPlan = Get-Content -LiteralPath $providerPlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal 'vendor/model-a' $providerPlan.model_id 'Plan-only intake changed profile model identity.'
    Assert-Equal $first.profile_sha256 $providerPlan.qualification_profile.sha256 'Plan-only intake lost profile provenance.'
    Assert-Equal 0.1 ([double]$providerPlan.generation.temperature) 'Plan-only intake did not apply profile temperature.'
    Assert-Equal 1 @($providerPlan.qualification_profile.unsupported_settings).Count 'Plan-only intake hid unsupported settings.'
    Assert-True ($null -eq $providerPlan.process_id) 'Plan-only intake unexpectedly recorded a provider process.'
    Assert-True (-not [bool]$providerPlan.ownership_verified) 'Plan-only intake unexpectedly claimed provider ownership.'

    Write-Host "PASS model qualification profile ($assertions assertions)" -ForegroundColor Green
}
finally {
    Remove-Module ModelQualificationProfile -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
