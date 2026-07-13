[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$BenchmarkRunnerRoot = "",
    [string]$PythonPath = "",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$BenchrunArgs
)

$ErrorActionPreference = "Stop"
$runner = if ([string]::IsNullOrWhiteSpace($BenchmarkRunnerRoot)) {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\local-benchmark-runner"))
} else {
    [IO.Path]::GetFullPath($BenchmarkRunnerRoot)
}
if (-not (Test-Path -LiteralPath (Join-Path $runner "src\benchrun\cli.py"))) {
    throw "Local benchmark runner was not found at '$runner'. Pass -BenchmarkRunnerRoot to override the sibling-repository default."
}

$pythonCandidates = @(
    $PythonPath,
    (Join-Path $runner ".venv312\Scripts\python.exe"),
    (Join-Path $runner ".venv\Scripts\python.exe"),
    (Get-Command python.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$python = $pythonCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($python)) {
    throw "No working Python interpreter was found. Pass -PythonPath or create a benchmark-runner virtual environment."
}

$previousPythonPath = $env:PYTHONPATH
$env:PYTHONPATH = Join-Path $runner "src"
Push-Location $runner
try {
    & $python -c "from benchrun.cli import app; app()" @BenchrunArgs
    exit $LASTEXITCODE
} finally {
    Pop-Location
    $env:PYTHONPATH = $previousPythonPath
}
