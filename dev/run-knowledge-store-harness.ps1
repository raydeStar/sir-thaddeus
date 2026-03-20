#requires -Version 5.1

param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$HarnessArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifactsRoot = Join-Path $repoRoot 'artifacts\harness-localapp\knowledge-store'
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

try {
    $env:ST_SETTINGS_PATH = $patchedSettingsPath
    $env:ST_AUDIT_PATH = $auditPath
    $env:ST_KNOWLEDGE_STORE_HARNESS_ACTIVE = 'true'

    Set-Location $repoRoot

    $defaultArgs = @('--suite', 'knowledge-store', '--max-iters', '1', '--judge', 'none')
    & (Join-Path $repoRoot 'dev\harness.ps1') @defaultArgs @HarnessArgs
    exit $LASTEXITCODE
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

    Remove-Item Env:ST_KNOWLEDGE_STORE_HARNESS_ACTIVE -ErrorAction SilentlyContinue
}