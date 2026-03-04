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
`./artifacts/test-results/`.

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

## Production preflight (before release)

```powershell
.\dev\preflight.ps1
```

Runs bootstrap + full Release test suite as a single gate before packaging.

## Outputs

- TRX results are written to `./artifacts/test-results/`
- Each run produces a timestamped `.trx` file (e.g. `test-20260208-151200.trx`)

## Fast E2E Strategy (Lower Time + Tokens)

Use the harness in tiers so PR checks stay fast and cheap:

1. Tier 1 (every PR, fastest live check):

```powershell
dotnet tools/SirThaddeus.Harness/bin/Debug/net9.0/SirThaddeus.Harness.dll smoke --mode live --max-iters 1 --judge none
```

or use the wrapper that downgrades known external provider outages to `INCONCLUSIVE`:

```powershell
powershell -ExecutionPolicy Bypass -File ./dev/harness-pr-fast.ps1
```

2. Tier 2 (targeted feature checks):

```powershell
dotnet tools/SirThaddeus.Harness/bin/Debug/net9.0/SirThaddeus.Harness.dll run --suite stargate --mode live --max-iters 1 --judge none
```

3. Tier 3 (nightly / pre-release, expensive):

```powershell
./dev/harness_e2e.ps1
```

Recommended policy:

- Keep `--max-iters 1` for PR runs.
- Use `--judge none` for PR runs; reserve judge modes for nightly.
- Treat external-provider failures (geocode/search endpoint outages) as environment issues unless deterministic/local suites regress.

## Optional pre-push hook

Enable the repo-managed git hooks to run tests before pushes:

```powershell
git config core.hooksPath .githooks
```

The configured pre-push hook runs `.\dev\test.ps1` and blocks pushes when the test gate fails.

## Pinned SDK

The repo pins the .NET SDK version via `global.json` at the repo root.
If you get SDK mismatch errors, install the version listed there.
