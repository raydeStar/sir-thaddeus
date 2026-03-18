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
`./artifacts/test-results/`. On unfiltered runs it also executes the
`screen-observe` harness suite.

## Run a focused subset

```powershell
.\dev\test.ps1 -Filter "FullyQualifiedName~SirThaddeus.Tests.AgentOrchestratorTests"
```

Any valid `dotnet test --filter` expression works here.

Filtered runs skip the screen-observe harness so the fast loop stays fast.

## Run all tests (slower, Release build)

```powershell
.\dev\test_all.ps1
```

Restores packages, builds in Release, then runs the full suite.
This includes the `screen-observe` harness suite.

## Production preflight (before release)

```powershell
.\dev\preflight.ps1
```

Runs bootstrap + full Release test suite as a single gate before packaging.

## Outputs

- TRX results are written to `./artifacts/test-results/`
- Each run produces a timestamped `.trx` file (e.g. `test-20260208-151200.trx`)

## Run headless integration tests

Use the harness as the single conversation-level integration test path. It always
drives the real headless runtime.

Run everything:

```powershell
./dev/harness.ps1 --all --judge none
```

Run one category/suite:

```powershell
./dev/harness.ps1 --suite web-search --judge none
```

`--category` is an alias for `--suite`:

```powershell
./dev/harness.ps1 --category reasoning --judge none
```

Run one specific test id:

```powershell
./dev/harness.ps1 --test smoke_casual_no_tools --judge none
```

If a test id exists in more than one suite, pair it with `--suite`.

Examples:

```powershell
./dev/harness.ps1 --suite smoke --test smoke_casual_no_tools --judge none
./dev/harness.ps1 run --suite personality --max-iters 1 --judge none
```

Recommended policy:

- Keep `--max-iters 1` for PR runs.
- Use `--judge none` for PR runs; reserve judge modes for nightly.
- Use `--all` before merges when you want one full headless pass.

## Screen awareness validation

Fast automated gate:

```powershell
./dev/test.ps1
```

This now covers:

- the standard .NET test suite
- the Windows-only `SirThaddeus.Windows.Tests` helper tests
- the `screen-observe` harness suite

Target just the screen-observe harness:

```powershell
./dev/harness.ps1 --suites-root ./artifacts/harness-suites --suite screen-observe --max-iters 1 --judge none
```

Target the browser-aware screen suite:

```powershell
./dev/harness.ps1 --suites-root ./artifacts/harness-suites --suite screen-observe-browser --max-iters 1 --judge none
```

This suite is intended for targeted local validation when you want to verify
browser URL reading and page-summary behavior without making the default CI gate
depend on a browser being open.

Best manual desktop test on Windows:

1. Open a real native app such as File Explorer, Notepad, or Settings.
2. Ask: `What can you see on my screen right now?`
3. Confirm the reply mentions the active window and visible UI text rather than OCR-like word soup.
4. Open a browser on a public page and repeat the same prompt.
5. Confirm the reply includes structured on-screen context and, for browsers, fetched page content when the address bar is readable.
6. Try an unsupported or visually noisy surface, then confirm the reply clearly labels the OCR fallback.

Best browser-specific manual test:

1. Open Edge or Chrome to a public page with obvious content, such as a Wikipedia article.
2. Keep the browser as the foreground window with the address bar visible.
3. Ask: `What can you see on my screen right now?`
4. Ask: `If I'm looking at a browser page right now, summarize that page.`
5. Confirm the response identifies the site or page title and summarizes page content rather than just listing OCR text.
6. Repeat once on a localhost page or authenticated app to see the graceful behavior when HTTP fetch cannot provide useful content.

Recommended manual matrix:

- Edge or Chrome on a normal public page
- File Explorer in a folder with recognizable filenames
- Notepad with a few lines of text
- Windows Settings
- An app with weak accessibility support to confirm graceful OCR fallback

## Optional pre-push hook

Enable the repo-managed git hooks to run tests before pushes:

```powershell
git config core.hooksPath .githooks
```

The configured pre-push hook runs `.\dev\test.ps1` and blocks pushes when the test gate fails.

## Pinned SDK

The repo pins the .NET SDK version via `global.json` at the repo root.
If you get SDK mismatch errors, install the version listed there.
