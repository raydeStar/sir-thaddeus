#requires -Version 5.1

param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$HarnessArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$ProjectPath = Join-Path $RepoRoot "tools/SirThaddeus.Harness/SirThaddeus.Harness.csproj"

if (-not (Test-Path $ProjectPath)) {
    Write-Host "Harness project not found at $ProjectPath" -ForegroundColor Red
    exit 1
}

function Test-KnowledgeStoreHarnessNeeded {
    param(
        [string[]]$Arguments
    )

    if ([string]::Equals($env:ST_KNOWLEDGE_STORE_HARNESS_ACTIVE, 'true', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $arg = $Arguments[$i]

        if ($arg -eq '--all') {
            return $true
        }

        if (($arg -eq '--suite' -or $arg -eq '--category') -and $i + 1 -lt $Arguments.Count) {
            if ([string]::Equals($Arguments[$i + 1], 'knowledge-store', [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        if ($arg -eq '--test' -and $i + 1 -lt $Arguments.Count) {
            if ($Arguments[$i + 1] -like 'knowledge_store_*') {
                return $true
            }
        }
    }

    return $false
}

function Invoke-WithKnowledgeStoreHarnessSettings {
    param(
        [scriptblock]$Action
    )

    $artifactsRoot = Join-Path $RepoRoot 'artifacts\harness-localapp\knowledge-store'
    $knowledgeRoot = Join-Path $artifactsRoot 'root'
    $patchedSettingsPath = Join-Path $artifactsRoot 'settings.json'
    $auditPath = Join-Path $artifactsRoot 'audit.jsonl'

    $sourceSettingsPath = if ($env:ST_SETTINGS_PATH) {
        $env:ST_SETTINGS_PATH
    }
    else {
        Join-Path $env:LOCALAPPDATA 'SirThaddeus\settings.json'
    }

    if (-not (Test-Path $sourceSettingsPath)) {
        throw "Settings file not found at $sourceSettingsPath. Start the app once or set ST_SETTINGS_PATH first."
    }

    if (Test-Path $knowledgeRoot) {
        Remove-Item -LiteralPath $knowledgeRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $knowledgeRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

    $settings = Get-Content -LiteralPath $sourceSettingsPath -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName knowledgeStore -NotePropertyValue ([pscustomobject]@{}) -Force
    $settings.knowledgeStore = [pscustomobject]@{
        enabled = $true
        roots = @(
            [pscustomobject]@{
                id = 'harness'
                displayName = 'Harness Knowledge Store'
                absolutePath = $knowledgeRoot
                accessLevel = 'KnowledgeReadWrite'
                allowIndexing = $true
                confirmWrites = $false
            }
        )
        maxFilesPerFolder = 200
        maxFolderDepth = 3
        maxRootSizeBytes = 52428800
        maxFileSizeBytes = 524288
    }
    $settings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $patchedSettingsPath -Encoding UTF8

    $oldSettingsPath = $env:ST_SETTINGS_PATH
    $oldAuditPath = $env:ST_AUDIT_PATH
    $oldHarnessActive = $env:ST_KNOWLEDGE_STORE_HARNESS_ACTIVE

    try {
        $env:ST_SETTINGS_PATH = $patchedSettingsPath
        $env:ST_AUDIT_PATH = $auditPath
        $env:ST_KNOWLEDGE_STORE_HARNESS_ACTIVE = 'true'
        & $Action
    }
    finally {
        if ($null -ne $oldSettingsPath) {
            $env:ST_SETTINGS_PATH = $oldSettingsPath
        }
        else {
            Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue
        }

        if ($null -ne $oldAuditPath) {
            $env:ST_AUDIT_PATH = $oldAuditPath
        }
        else {
            Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue
        }

        if ($null -ne $oldHarnessActive) {
            $env:ST_KNOWLEDGE_STORE_HARNESS_ACTIVE = $oldHarnessActive
        }
        else {
            Remove-Item Env:ST_KNOWLEDGE_STORE_HARNESS_ACTIVE -ErrorAction SilentlyContinue
        }
    }
}

$effectiveHarnessArgs = if ($HarnessArgs.Count -eq 0 -or $HarnessArgs[0].StartsWith('--')) {
    @('run') + $HarnessArgs
}
else {
    $HarnessArgs
}

$argsToRun = @('run', '--project', $ProjectPath, '--') + $effectiveHarnessArgs

if (Test-KnowledgeStoreHarnessNeeded -Arguments $effectiveHarnessArgs) {
    Invoke-WithKnowledgeStoreHarnessSettings {
        # Temporarily allow native command stderr (e.g. SearXNG status messages)
        $prevPref = $ErrorActionPreference
        $nativePrefVar = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
        $prevNativeStderrPref = if ($null -ne $nativePrefVar) { $nativePrefVar.Value } else { $null }
        $ErrorActionPreference = 'Continue'
        if ($null -ne $nativePrefVar) {
            $PSNativeCommandUseErrorActionPreference = $false
        }
        & dotnet @argsToRun 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                $_.ToString()
            }
            else {
                $_
            }
        }
        if ($null -ne $nativePrefVar) {
            $PSNativeCommandUseErrorActionPreference = $prevNativeStderrPref
        }
        $ErrorActionPreference = $prevPref
    }
}
else {
    $prevPref = $ErrorActionPreference
    $nativePrefVar = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    $prevNativeStderrPref = if ($null -ne $nativePrefVar) { $nativePrefVar.Value } else { $null }
    $ErrorActionPreference = 'Continue'
    if ($null -ne $nativePrefVar) {
        $PSNativeCommandUseErrorActionPreference = $false
    }
    & dotnet @argsToRun 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            $_.ToString()
        }
        else {
            $_
        }
    }
    if ($null -ne $nativePrefVar) {
        $PSNativeCommandUseErrorActionPreference = $prevNativeStderrPref
    }
    $ErrorActionPreference = $prevPref
}

exit $LASTEXITCODE
