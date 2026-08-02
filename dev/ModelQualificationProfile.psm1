#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RequiredProfileProperty {
    param($Object, [string]$Name, [string]$Path)

    if ($null -eq $Object -or -not ($Object.PSObject.Properties.Name -contains $Name)) {
        throw "Model profile is missing '$Path.$Name'."
    }
    $value = $Object.$Name
    if ($null -eq $value -or ($value -is [string] -and [string]::IsNullOrWhiteSpace($value))) {
        throw "Model profile field '$Path.$Name' is empty."
    }
    return $value
}

function ConvertTo-NormalizedCapabilityToken {
    param([string]$Value, [string]$Label)

    $normalized = $Value.Trim().ToLowerInvariant() -replace '-', '_'
    if ($normalized -notmatch '^[a-z0-9][a-z0-9._/]*$') {
        throw "Model profile $Label '$Value' contains unsupported characters."
    }
    return $normalized
}

function Import-ModelQualificationProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]
        [ValidateSet('lmstudio', 'llamacpp', 'external')]
        [string]$Backend
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Model profile '$Path' does not exist."
    }
    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    try {
        $profile = Get-Content -LiteralPath $resolvedPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "Model profile '$resolvedPath' is not valid JSON: $($_.Exception.Message)"
    }

    $schemaVersion = [int](Get-RequiredProfileProperty $profile 'schema_version' '$')
    if ($schemaVersion -ne 1) { throw "Unsupported model profile schema_version '$schemaVersion'." }
    $profileId = ConvertTo-NormalizedCapabilityToken `
        ([string](Get-RequiredProfileProperty $profile 'profile_id' '$')) 'profile_id'
    $model = Get-RequiredProfileProperty $profile 'model' '$'
    $modelId = [string](Get-RequiredProfileProperty $model 'id' '$.model')
    $sources = @((Get-RequiredProfileProperty $profile 'sources' '$'))
    if ($sources.Count -eq 0) { throw 'Model profile must contain at least one provenance source.' }

    $sourceIds = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $normalizedSources = foreach ($source in $sources) {
        $sourceId = ConvertTo-NormalizedCapabilityToken `
            ([string](Get-RequiredProfileProperty $source 'id' '$.sources[]')) 'source id'
        if (-not $sourceIds.Add($sourceId)) { throw "Duplicate model profile source id '$sourceId'." }
        $uriText = [string](Get-RequiredProfileProperty $source 'uri' '$.sources[]')
        $uri = $null
        if (-not [Uri]::TryCreate($uriText, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
            throw "Model profile source '$sourceId' must use an absolute HTTPS URI."
        }
        $revision = [string](Get-RequiredProfileProperty $source 'revision' '$.sources[]')
        $retrieved = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse(
            [string](Get-RequiredProfileProperty $source 'retrieved_utc' '$.sources[]'),
            [ref]$retrieved)) {
            throw "Model profile source '$sourceId' has an invalid retrieved_utc value."
        }
        [pscustomobject]@{
            id = $sourceId
            uri = $uri.AbsoluteUri
            revision = $revision
            retrieved_utc = $retrieved.ToUniversalTime().ToString('O')
        }
    }

    $runtimeSupport = @((Get-RequiredProfileProperty $profile 'runtime_support' '$'))
    $supportedBackends = foreach ($entry in $runtimeSupport) {
        $entryBackend = ConvertTo-NormalizedCapabilityToken `
            ([string](Get-RequiredProfileProperty $entry 'backend' '$.runtime_support[]')) 'runtime backend'
        $sourceId = ConvertTo-NormalizedCapabilityToken `
            ([string](Get-RequiredProfileProperty $entry 'source_id' '$.runtime_support[]')) 'runtime source_id'
        if (-not $sourceIds.Contains($sourceId)) {
            throw "Runtime support for '$entryBackend' references unknown source '$sourceId'."
        }
        [pscustomobject]@{ backend = $entryBackend; source_id = $sourceId }
    }
    if (-not ($supportedBackends.backend -contains $Backend)) {
        throw "Model profile '$profileId' does not document runtime support for backend '$Backend'."
    }

    $recommendations = Get-RequiredProfileProperty $profile 'recommendations' '$'
    $recommendationSourceIds = @((Get-RequiredProfileProperty $recommendations 'source_ids' '$.recommendations'))
    foreach ($sourceIdValue in $recommendationSourceIds) {
        $sourceId = ConvertTo-NormalizedCapabilityToken ([string]$sourceIdValue) 'recommendation source_id'
        if (-not $sourceIds.Contains($sourceId)) {
            throw "Recommendations reference unknown source '$sourceId'."
        }
    }
    $recommendedContext = [int](Get-RequiredProfileProperty $recommendations 'context_window_tokens' '$.recommendations')
    $recommendedGeneration = Get-RequiredProfileProperty $recommendations 'generation' '$.recommendations'
    $recommendedTemperature = [double](Get-RequiredProfileProperty $recommendedGeneration 'temperature' '$.recommendations.generation')
    if ($recommendedContext -lt 256 -or $recommendedContext -gt 1048576) {
        throw 'recommendations.context_window_tokens must be between 256 and 1048576.'
    }
    if ($recommendedTemperature -lt 0 -or $recommendedTemperature -gt 2) {
        throw 'recommendations.generation.temperature must be between 0 and 2.'
    }

    $qualification = Get-RequiredProfileProperty $profile 'qualification' '$'
    $qualificationSourceIds = @((Get-RequiredProfileProperty $qualification 'source_ids' '$.qualification'))
    foreach ($sourceIdValue in $qualificationSourceIds) {
        $sourceId = ConvertTo-NormalizedCapabilityToken ([string]$sourceIdValue) 'qualification source_id'
        if (-not $sourceIds.Contains($sourceId)) {
            throw "Qualification references unknown source '$sourceId'."
        }
    }
    $contextWindow = [int](Get-RequiredProfileProperty $qualification 'context_window_tokens' '$.qualification')
    $maxOutput = [int](Get-RequiredProfileProperty $qualification 'max_output_tokens' '$.qualification')
    if ($contextWindow -lt 256 -or $contextWindow -gt 1048576) {
        throw 'qualification.context_window_tokens must be between 256 and 1048576.'
    }
    if ($maxOutput -lt 1 -or $maxOutput -gt 1048576) {
        throw 'qualification.max_output_tokens must be between 1 and 1048576.'
    }
    $generation = Get-RequiredProfileProperty $qualification 'generation' '$.qualification'
    $temperature = [double](Get-RequiredProfileProperty $generation 'temperature' '$.qualification.generation')
    if ($temperature -lt 0 -or $temperature -gt 2) {
        throw 'qualification.generation.temperature must be between 0 and 2.'
    }
    foreach ($property in $recommendedGeneration.PSObject.Properties) {
        if (-not ($generation.PSObject.Properties.Name -contains $property.Name)) {
            throw "Qualification generation omits researched setting '$($property.Name)'."
        }
    }

    $overrides = New-Object Collections.ArrayList
    if ($contextWindow -ne $recommendedContext) {
        [void]$overrides.Add([pscustomobject]@{
            name = 'context_window_tokens'; recommended = $recommendedContext; selected = $contextWindow
        })
    }
    foreach ($property in $recommendedGeneration.PSObject.Properties) {
        $selectedValue = $generation.($property.Name)
        $recommendedJson = $property.Value | ConvertTo-Json -Compress -Depth 8
        $selectedJson = $selectedValue | ConvertTo-Json -Compress -Depth 8
        if ($recommendedJson -ne $selectedJson) {
            [void]$overrides.Add([pscustomobject]@{
                name = "generation.$($property.Name)"; recommended = $property.Value; selected = $selectedValue
            })
        }
    }
    $overrideReason = if ($qualification.PSObject.Properties.Name -contains 'override_reason') {
        [string]$qualification.override_reason
    } else { '' }
    if ($overrides.Count -gt 0 -and [string]::IsNullOrWhiteSpace($overrideReason)) {
        throw 'Qualification differs from researched recommendations without qualification.override_reason.'
    }

    $applied = @(
        [pscustomobject]@{ name = 'context_window_tokens'; value = $contextWindow },
        [pscustomobject]@{ name = 'max_output_tokens'; value = $maxOutput },
        [pscustomobject]@{ name = 'temperature'; value = $temperature }
    )
    $unsupported = foreach ($property in $generation.PSObject.Properties) {
        if ($property.Name -ne 'temperature') {
            [pscustomobject]@{
                name = $property.Name
                value = $property.Value
                reason = 'Sir Thaddeus runtime settings do not currently expose this generation control.'
            }
        }
    }

    return [pscustomobject]@{
        schema_version = 1
        profile_id = $profileId
        profile_path = $resolvedPath
        profile_sha256 = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        model_id = $modelId
        model_family = if ($model.PSObject.Properties.Name -contains 'family') { [string]$model.family } else { $null }
        selected_backend = $Backend
        context_window_tokens = $contextWindow
        max_output_tokens = $maxOutput
        temperature = $temperature
        sources = @($normalizedSources)
        runtime_support = @($supportedBackends)
        recommended_settings = [pscustomobject]@{
            context_window_tokens = $recommendedContext
            generation = $recommendedGeneration
        }
        applied_settings = @($applied)
        unsupported_settings = @($unsupported)
        overrides = @($overrides.ToArray())
        override_reason = if ([string]::IsNullOrWhiteSpace($overrideReason)) { $null } else { $overrideReason }
    }
}

Export-ModuleMember -Function Import-ModelQualificationProfile
