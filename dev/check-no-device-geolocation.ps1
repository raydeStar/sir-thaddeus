#requires -Version 5.1

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".."))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$forbiddenPatterns = @(
    'Windows\.Devices\.Geolocation',
    'GeoCoordinateWatcher',
    'navigator\.geolocation',
    'getCurrentPosition',
    'watchPosition'
)

$allowedExtensions = @('.cs', '.xaml', '.js', '.jsx', '.ts', '.tsx', '.py')
$sourceRoots = @(
    (Join-Path $RepoRoot 'apps'),
    (Join-Path $RepoRoot 'packages')
)

$violations = New-Object System.Collections.Generic.List[string]

foreach ($root in $sourceRoots) {
    if (-not (Test-Path $root)) { continue }

    Get-ChildItem -Path $root -Recurse -File | ForEach-Object {
        $file = $_
        $ext = $file.Extension.ToLowerInvariant()
        if ($allowedExtensions -notcontains $ext) { return }

        if ($file.FullName -match '[\\/](bin|obj|artifacts|\.git|node_modules|dist|coverage)[\\/]') { return }

        $content = Get-Content -Path $file.FullName -Raw
        foreach ($pattern in $forbiddenPatterns) {
            if ($content -imatch $pattern) {
                $relative = Resolve-Path -Relative $file.FullName
                $violations.Add("$relative :: $pattern")
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "Forbidden device geolocation references found:" -ForegroundColor Red
    $violations | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "No forbidden device geolocation references found." -ForegroundColor Green
exit 0
