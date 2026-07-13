[CmdletBinding()]
param(
    [string]$LmStudioBaseUrl = "http://127.0.0.1:1234/v1",
    [string]$ExpectedModel = "",
    [string]$BenchmarkRunnerRoot = "",
    [string]$PythonPath = ""
)

$ErrorActionPreference = "Stop"
$failed = $false

Write-Host "Benchmark prerequisite check"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    Write-Host "[FAIL] dotnet is unavailable."
    $failed = $true
} else {
    Write-Host "[ OK ] dotnet: $(& dotnet --version)"
}

$runner = if ([string]::IsNullOrWhiteSpace($BenchmarkRunnerRoot)) {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\local-benchmark-runner"))
} else {
    [IO.Path]::GetFullPath($BenchmarkRunnerRoot)
}
$pythonCandidates = @(
    $PythonPath,
    (Join-Path $runner ".venv312\Scripts\python.exe"),
    (Join-Path $runner ".venv\Scripts\python.exe"),
    (Get-Command python.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
)
$workingPython = $null
foreach ($candidate in $pythonCandidates) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
    if (-not (Test-Path -LiteralPath $candidate)) { continue }
    try {
        $version = & $candidate --version 2>&1
        if ($LASTEXITCODE -eq 0) {
            $workingPython = $candidate
            Write-Host "[ OK ] Python: $candidate ($version)"
            break
        }
    } catch {
        # Try the next interpreter. The old .venv can survive while its base
        # Windows Store interpreter has disappeared.
    }
}
if ($null -eq $workingPython) {
    Write-Host "[FAIL] No working Python interpreter was found."
    $failed = $true
}

try {
    $models = Invoke-RestMethod -Uri "$($LmStudioBaseUrl.TrimEnd('/'))/models" -TimeoutSec 4
    $ids = @($models.data | ForEach-Object { $_.id })
    if ([string]::IsNullOrWhiteSpace($ExpectedModel)) {
        Write-Host "[ OK ] LM Studio available. Models: $($ids -join ', ')"
    } elseif ($ids -contains $ExpectedModel) {
        Write-Host "[ OK ] LM Studio model loaded: $ExpectedModel"
    } else {
        Write-Host "[FAIL] LM Studio responded, but $ExpectedModel is not loaded. Found: $($ids -join ', ')"
        $failed = $true
    }
} catch {
    Write-Host "[FAIL] LM Studio is unavailable at $LmStudioBaseUrl."
    $failed = $true
}

$docker = Get-Command docker -ErrorAction SilentlyContinue
if ($null -eq $docker) {
    Write-Host "[WARN] Docker CLI is unavailable. The 56-case LM Studio lane does not require Docker; overnight HF runs do."
} else {
    try {
        $containers = @(& docker ps --format "{{.Names}}" 2>$null)
        if ($LASTEXITCODE -eq 0) {
            Write-Host "[ OK ] Docker engine available. Running containers: $($containers -join ', ')"
        } else {
            Write-Host "[WARN] Docker engine is not responding. Mini LM Studio comparisons can still run."
        }
    } catch {
        Write-Host "[WARN] Docker engine is not responding. Mini LM Studio comparisons can still run."
    }
}

if ($failed) { exit 1 }
Write-Host "Ready for the mini-MMLU comparison."
