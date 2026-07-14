# Testing

## Quick Start

If you just want the shortest path:

- First-time setup: `./dev/bootstrap.ps1`
- Fast local check before coding more: `./dev/test.ps1`
- One harness suite: `./dev/harness.ps1 --suite <name> --judge none`
- One harness test: `./dev/harness.ps1 --suite <name> --test <id> --judge none`
- Full pre-submit pass: `./dev/harness.ps1 --all --judge none`

Use this rule of thumb:

- `test.ps1` = normal code/test loop
- `harness.ps1` = real conversation-level validation
- `harness.ps1 stage ...` = fast pipeline diagnostics when you want to isolate routing or query behavior
- `preflight.ps1` = release gate

## Human Path

If you are a person skimming this file and want the practical path:

1. Run `./dev/bootstrap.ps1` once on a new machine.
2. Use `./dev/test.ps1` while coding.
3. If the change affects real assistant behavior, run a focused harness command.
4. Before submitting, run the narrowest relevant harness suite or `./dev/harness.ps1 --all --judge none` if you want one full pass.

Most common commands:

```powershell
./dev/test.ps1
./dev/verify-harness.ps1
./dev/harness.ps1 --suite quality --judge none
./dev/harness.ps1 --suite web-search --test web_local_business_deli --judge none
./dev/harness.ps1 stage --suite continuity --test local_business_followup_anchor
```

## AI / Agent Path

If you are using an AI workflow, use this loop:

1. Start with the narrowest trustworthy test.
2. Read artifacts before editing code.
3. Fix product behavior, not scoring.
4. Re-run the failing test, then the containing suite, then a broader pass.

The AI-specific harness rules live here:

- `.github/instructions/e2e-harness-rules.instructions.md`
- `.github/instructions/harness-iteration-framework.instructions.md`

Short version for AI-assisted work:

- Prefer `./dev/harness.ps1 --suite <name> --test <id> --judge none` over repeated full runs.
- Use stage suites under `tools/SirThaddeus.Harness/StageSuites/` for deterministic preprocess/classify/query regressions.
- Use `followup_anchor` in stage suite `context` when a vague follow-up needs a deterministic resolved topic.
- Do not modify harness scoring or suite expectations to make failures disappear.
- Harness scoring uses rubric profiles with a 0..1 `overallScore` and 0..4 metric scores. Legacy fixture `min_score` values such as `7` are normalized to `0.7`.

## One-time setup

```powershell
.\dev\bootstrap.ps1
```

Validates that the .NET SDK is installed, creates the `artifacts/` output
folder, and runs `dotnet restore` against the solution.

## Which Test Should I Run?

Use this when you are unsure:

- Changed normal C# code and want a quick check: `./dev/test.ps1`
- Need one xUnit subset: `./dev/test.ps1 -Filter "FullyQualifiedName~SomeTestName"`
- Need one real assistant behavior suite: `./dev/harness.ps1 --suite <name> --judge none`
- Need one real assistant behavior test: `./dev/harness.ps1 --suite <name> --test <id> --judge none`
- Need fast pipeline-only validation: `./dev/harness.ps1 stage --suite <name>`
- Need the whole conversation-level baseline: `./dev/harness.ps1 --all --judge none`
- Need the release gate: `./dev/preflight.ps1`

## Run unit tests (fast loop)

```powershell
.\dev\test.ps1
```

Builds in Debug, runs all tests, and writes a TRX report to
`./artifacts/test-results/`. On unfiltered runs it executes the
`screen-observe` harness suite when fixtures exist under
`./artifacts/harness-suites/screen-observe/`; otherwise it reports that harness
as skipped.

## Run a focused subset

```powershell
.\dev\test.ps1 -Filter "FullyQualifiedName~PipelineBacked"
```

Any valid `dotnet test --filter` expression works here.

Filtered runs skip the screen-observe harness so the fast loop stays fast.

## Run all tests (slower, Release build)

```powershell
.\dev\test_all.ps1
```

Restores packages, builds in Release, then runs the full suite.
This includes the `screen-observe` harness suite when its fixtures are present.

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

For repeated model measurements, use the campaign wrapper:

```powershell
./dev/harness-repeat.ps1 -Suite python-probe -Repeats 5
```

It prepares the harness and headless runtime once, then launches the compiled
assemblies for each isolated repeat. `-SkipBuild` is available when a parent
campaign such as `model-intake.ps1` has already prepared both Debug assemblies.
Do not use it after source changes unless you have rebuilt first.

If a test id exists in more than one suite, pair it with `--suite`.

Examples:

```powershell
./dev/harness.ps1 --suite smoke --test smoke_casual_no_tools --judge none
./dev/harness.ps1 run --suite personality --max-iters 1 --judge none
```

## Run stage suites (fast deterministic pipeline checks)

Use stage suites when you want to validate routing/query behavior without a full
headless conversation loop.

Run every stage suite:

```powershell
./dev/harness.ps1 stage --all
```

Run one stage suite:

```powershell
./dev/harness.ps1 stage --suite continuity
```

If you target a specific stage like `preprocess`, `classify`, or `query`, the
selected stage tests must define checks for that stage. The harness now fails
closed instead of silently passing unmatched specs.

Run one stage test:

```powershell
./dev/harness.ps1 stage --suite continuity --test local_business_followup_anchor
```

Target only query checks:

```powershell
./dev/harness.ps1 stage query --suite continuity --test local_business_followup_anchor
```

Stage suites live under `tools/SirThaddeus.Harness/StageSuites/` by default.
They support a `context` block for fabricated state such as:

- `assistant_context`
- `followup_anchor`
- `user_city`
- `has_recent_search_results`
- `has_recent_rationale`

Use `followup_anchor` for vague follow-up regressions when you need a deterministic
resolved entity instead of relying only on assistant text parsing.

Recommended policy:

- Keep `--max-iters 1` for PR runs.
- Use `--judge none` for PR runs; reserve judge modes for nightly.
- Use `--all` before merges when you want one full headless pass.

### Harness rubric reports

Each harness iteration writes `score.json` with:

- `passed`, `status`, `overallScore`, and normalized `threshold`
- `profile`
- per-metric `scores` from 0..4
- `hardGateFailures`
- deterministic check results
- `strengths`, `problems`, and `requiredFixes`
- latency and token counts when available

Run-level `summary.json` and `summary.md` include failing tests sorted by
severity, top recurring failure reasons, average score by rubric profile, and
hard-gate failure counts. They also separate runtime warmup, per-test reset,
test work, host total, and remaining harness overhead so latency regressions can
be attributed instead of inferred from one wall-clock number.

### Routing latency diagnostics

Routing diagnostics are opt-in and do not change normal execution:

- `ST_ROUTING_LATENCY_TRACE=1` records monotonic pipeline, provider, memory,
  HTTP, and UI timing without logging prompt or memory contents.
- `ST_HARNESS_PRESERVE_SANDBOX=1` retains an isolated harness runtime so its
  local logs and audit records can be inspected after a run.

Use `dev/run-routing-latency-desktop-campaign.ps1` for repeated desktop-path
cohorts and `dev/analyze-routing-latency-campaign.ps1` to summarize the result.
The `routing-latency` harness suite supplies focused conversation, memory,
research, tool, file, high-stakes, structured-output, and adversarial probes.
Rejected routing behavior experiments are removed from production rather than
retained behind dormant environment flags; see `ASSISTANT_PIPELINE.md`.

The optional mini-MMLU helper scripts expect `local-benchmark-runner` as a
sibling checkout by default. Override `-BenchmarkRunnerRoot` and `-PythonPath`
when using a different layout. These scripts compare run artifacts; production
routing never receives expected answers, suite IDs, or scoring thresholds.

### Overnight harness runs

For long local runs such as `./dev/harness.ps1 --all --judge none`:

- Keep the local model endpoint running for the entire run if your setup depends on LM Studio or another local OpenAI-compatible server.
- Avoid overlapping harness runs after code changes. A stale headless runtime or MCP server can hold build outputs open and make the next run fail for the wrong reason.
- If you interrupt a long run after rebuilding product code, restart the harness cleanly instead of trusting partial results from the old binaries.

## Screen awareness validation

Fast automated gate:

```powershell
./dev/test.ps1
```

This now covers:

- the standard .NET test suite
- the Windows-only `SirThaddeus.Windows.Tests` helper tests
- the `screen-observe` harness suite, when fixtures are present

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
