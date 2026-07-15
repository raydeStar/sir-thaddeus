#requires -Version 5.1

param(
    [string]$Runtime = "win-x64",
    [string]$SearxngRef = "8b95b2058be41580270f1dc348847c3342ee129b",
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host "--------------------------------------------------------------"
    Write-Host "  $Title"
    Write-Host "--------------------------------------------------------------"
}

function Fail([string]$Message, [int]$Code = 1) {
    Write-Host "  FAIL: $Message" -ForegroundColor Red
    exit $Code
}

function Apply-WindowsCompatPatch([string]$SourceRoot) {
    $valkeyDbPath = Join-Path $SourceRoot "searx\valkeydb.py"
    if (-not (Test-Path $valkeyDbPath)) {
        Fail "Expected compatibility target not found: $valkeyDbPath"
    }

    $content = Get-Content $valkeyDbPath -Raw
    if ($content -match '(?m)^import pwd\r?$') {
        $importReplacement = @'
try:
    import pwd
except ImportError:
    pwd = None
'@
        $content = [regex]::Replace(
            $content,
            '(?m)^import pwd\r?$',
            $importReplacement)
    }

    $exceptionReplacement = @'
        if pwd is not None and hasattr(os, 'getuid'):
            _pw = pwd.getpwuid(os.getuid())
            logger.exception("[%s (%s)] can't connect valkey DB ...", _pw.pw_name, _pw.pw_uid)
        else:
            logger.exception("can't connect valkey DB ...")
'@
    $content = [regex]::Replace(
        $content,
        '(?m)^        _pw = pwd\.getpwuid\(os\.getuid\(\)\)\r?\n        logger\.exception\("\[%s \(%s\)\] can''t connect valkey DB \.\.\.\", _pw\.pw_name, _pw\.pw_uid\)\r?$',
        $exceptionReplacement)

    if ($content -match '(?m)^import pwd\r?$' -or
        $content -match '(?m)^        _pw = pwd\.getpwuid\(os\.getuid\(\)\)\r?$') {
        Fail "SearXNG Windows compatibility patch did not apply cleanly."
    }

    Set-Content -Path $valkeyDbPath -Value $content -Encoding UTF8
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$sourceExtractDir = Join-Path $repoRoot "artifacts\searxng\$Runtime\source"
$packageDir = Join-Path $repoRoot "artifacts\searxng\$Runtime\package"
$sourceArchivePath = Join-Path $repoRoot "artifacts\searxng\$Runtime\searxng-source.tar.gz"
$downloadUrl = "https://codeload.github.com/searxng/searxng/tar.gz/$SearxngRef"

$voiceRuntimeDir = Join-Path $repoRoot "apps\voice-backend\runtime\python"
$voicePythonExe = Join-Path $voiceRuntimeDir "python.exe"
if (-not (Test-Path $voicePythonExe)) {
    Fail "Bundled Python runtime not found at $voicePythonExe. Run ./dev/fetch-assets.ps1 first."
}

if ($Force) {
    foreach ($path in @($sourceExtractDir, $packageDir, $sourceArchivePath)) {
        if (Test-Path $path) {
            Remove-Item -Path $path -Recurse -Force
        }
    }
}

New-Item -ItemType Directory -Force -Path (Split-Path $sourceExtractDir -Parent) | Out-Null

Write-Section "Fetch Upstream Source"
$resolvedCommit = $SearxngRef
$sourcePayloadDir = Join-Path $sourceExtractDir "searxng-$resolvedCommit"

if ((-not $Force) -and (Test-Path (Join-Path $sourcePayloadDir "searx\webapp.py"))) {
    Write-Host "  Reusing cached upstream source from $sourcePayloadDir"
}
else {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $sourceArchivePath
    if (-not (Test-Path $sourceArchivePath)) {
        Fail "Failed to download SearXNG source archive from $downloadUrl."
    }

    $archiveEntries = @(tar -tf $sourceArchivePath)
    if ($LASTEXITCODE -ne 0 -or $archiveEntries.Count -eq 0) {
        Fail "Failed to inspect the downloaded SearXNG source archive." $LASTEXITCODE
    }

    $archiveRoot = ($archiveEntries | Select-Object -First 1).TrimEnd('/')
    if ([string]::IsNullOrWhiteSpace($archiveRoot)) {
        Fail "Could not determine the root directory in the downloaded SearXNG archive."
    }

    New-Item -ItemType Directory -Force -Path $sourceExtractDir | Out-Null
    $archivePaths = @(
        "$archiveRoot/searx",
        "$archiveRoot/searxng_extra",
        "$archiveRoot/setup.py",
        "$archiveRoot/requirements.txt",
        "$archiveRoot/requirements-dev.txt",
        "$archiveRoot/requirements-server.txt",
        "$archiveRoot/README.rst",
        "$archiveRoot/LICENSE",
        "$archiveRoot/babel.cfg"
    )

    tar -xf $sourceArchivePath -C $sourceExtractDir @archivePaths
    if ($LASTEXITCODE -ne 0) {
        Fail "Failed to extract the required SearXNG source files." $LASTEXITCODE
    }

    $sourcePayloadDir = Join-Path $sourceExtractDir $archiveRoot
}

$resolvedCommit = $SearxngRef
Write-Host "  Upstream commit: $resolvedCommit"
if (-not (Test-Path (Join-Path $sourcePayloadDir "searx\webapp.py"))) {
    Fail "Downloaded archive is missing the SearXNG source payload."
}

Apply-WindowsCompatPatch -SourceRoot $sourcePayloadDir

Write-Section "Assemble Package"
if (Test-Path $packageDir) {
    Remove-Item -Path $packageDir -Recurse -Force
}

$packagePythonDir = Join-Path $packageDir "runtime\python"
$packageSourceDir = Join-Path $packageDir "source\searxng-upstream"
$packageSitePackagesDir = Join-Path $packageDir "deps\site-packages"

New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
New-Item -ItemType Directory -Force -Path $packagePythonDir | Out-Null
New-Item -ItemType Directory -Force -Path $packageSourceDir | Out-Null
New-Item -ItemType Directory -Force -Path $packageSitePackagesDir | Out-Null
Copy-Item -Path (Join-Path $voiceRuntimeDir "*") -Destination $packagePythonDir -Recurse -Force
Copy-Item -Path (Join-Path $sourcePayloadDir "*") -Destination $packageSourceDir -Recurse -Force

foreach ($file in @(
    "apps\searxng\start-searxng.ps1",
    "apps\searxng\settings.template.yml",
    "apps\searxng\THIRD_PARTY_NOTICES.md"
)) {
    Copy-Item -Path (Join-Path $repoRoot $file) -Destination $packageDir -Force
}

$packagePythonExe = Join-Path $packagePythonDir "python.exe"

Write-Section "Install Python Dependencies"
$requirementsPath = Join-Path $packageSourceDir "requirements.txt"
$nativePreference = $null
$hasNativePreference = $false
try {
    $nativeVariable = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    if ($null -ne $nativeVariable) {
        $hasNativePreference = $true
        $nativePreference = $nativeVariable.Value
        $script:PSNativeCommandUseErrorActionPreference = $false
    }

    & $packagePythonExe -m pip install --disable-pip-version-check --no-warn-script-location --upgrade `
        --target $packageSitePackagesDir `
        -r $requirementsPath
}
finally {
    if ($hasNativePreference) {
        $script:PSNativeCommandUseErrorActionPreference = $nativePreference
    }
}

if ($LASTEXITCODE -ne 0) {
    Fail "pip install failed for SearXNG dependencies." $LASTEXITCODE
}

$upstreamInfo = [ordered]@{
    repository = "https://github.com/searxng/searxng.git"
    requestedRef = $SearxngRef
    resolvedCommit = $resolvedCommit
    builtAtUtc = (Get-Date).ToUniversalTime().ToString("o")
}
$upstreamInfo | ConvertTo-Json | Set-Content -Path (Join-Path $packageDir "upstream.json") -Encoding ASCII

$packageFileCount = @(Get-ChildItem -Path $packageDir -Recurse -File).Count

Write-Section "Done"
Write-Host "  Package root : $packageDir"
Write-Host "  Commit       : $resolvedCommit"
Write-Host "  File count   : $packageFileCount"
