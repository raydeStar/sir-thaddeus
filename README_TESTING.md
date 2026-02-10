# Testing

## One-time setup

```powershell
.\dev\bootstrap.ps1
```

Validates that the .NET SDK is installed, creates the `artifacts/` output
folder, and runs `dotnet restore` against the solution.

## Run unit tests (fast loop)

```powershell
.\dev\test.ps1
```

Builds in Debug, runs all tests, and writes a TRX report to
`./artifacts/test/`.

## Run a focused subset

```powershell
.\dev\test.ps1 -Filter "FullyQualifiedName~SirThaddeus.Tests.AgentOrchestratorTests"
```

Any valid `dotnet test --filter` expression works here.

## Run all tests (slower, Release build)

```powershell
.\dev\test_all.ps1
```

Restores packages, builds in Release, then runs the full suite.

## Outputs

- TRX results are written to `./artifacts/test/`
- Each run produces a timestamped `.trx` file (e.g. `test-20260208-151200.trx`)

## Pinned SDK

The repo pins the .NET SDK version via `global.json` at the repo root.
If you get SDK mismatch errors, install the version listed there.
