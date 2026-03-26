---
name: 'Harness Iteration Framework'
description: 'Reusable workflow for running the full Sir Thaddeus E2E harness, breaking failures into stages, and iterating on product fixes with fast feedback.'
applyTo: 'tools/SirThaddeus.Harness/**,packages/agent/**,apps/headless-runtime/**,apps/ui-avalonia/**,tests/SirThaddeus.Tests/Integration/**'
---

# Harness Iteration Framework

Use this workflow when the goal is to improve real product behavior through the Sir Thaddeus harness. Optimize for fast diagnosis, disciplined reruns, and general fixes.

## Core Rule

Treat the harness as a measuring device, not the thing to optimize directly. Improve routing, tool use, synthesis, fallback behavior, state handling, or runtime transport in product code. Do not tailor behavior to known tests.

## Phase 0: Pick The Fastest Useful Loop

Choose the narrowest loop that still gives trustworthy signal.

- Full baseline: `./dev/harness.ps1 --all --judge none`
- Suite baseline: `./dev/harness.ps1 --suite <name> --judge none`
- Single failing test: `./dev/harness.ps1 --suite <name> --test <id> --judge none`
- Judge validation after product fixes: `./dev/harness.ps1 --suite <name> --judge model`

Rules:

- Start broad only when establishing current system state or confirming final regression coverage.
- After the first failing full run, narrow immediately to the failing suite or test.
- Do not keep rerunning the full harness while debugging one issue unless the failure suggests cross-suite state leakage or broad routing regressions.

## Phase 1: Run The Full Baseline

When asked for the full harness state, run:

```powershell
./dev/harness.ps1 --all --judge none
```

Capture:

- overall pass/fail summary
- failing suites/tests
- run id / artifact root

Do not conclude from the summary alone. The harness can under-report answer-quality defects when `--judge none` is used.

## Phase 2: Break Failures Into Layers Before Editing

For each failing test, inspect artifacts in this order:

1. `score.json`
2. `final.txt`
3. `steps.jsonl`
4. `input.json`

Decide which layer failed:

- No tools called when tools were needed: routing / intent classification / policy
- Wrong tools called: tool selection / route shaping
- Right tools called but wrong answer: synthesis / grounding / fallback logic
- Tool results missing or malformed: tool adapter / runtime transport / parsing
- Good answer but bad score: read the response and trace anyway; confirm whether this is a real product defect or a scoring blind spot

Always inspect `final.txt` directly. Scores are not sufficient evidence.

If a test reports `PASS` but the answer looks weak, hedged, off-topic, or obviously wrong, treat it like a failure in this workflow until you prove it is only a measurement issue.

Practical artifact walk:

```powershell
# Read the score, but do not stop there
Get-Content artifacts/harness/<run-id>/<suite>/<test-id>/iter-01/score.json

# Read the actual answer verbatim
Get-Content artifacts/harness/<run-id>/<suite>/<test-id>/iter-01/final.txt

# Read the tool trace step by step
Get-Content artifacts/harness/<run-id>/<suite>/<test-id>/iter-01/steps.jsonl

# Confirm the exact prompt and allowed tools
Get-Content artifacts/harness/<run-id>/<suite>/<test-id>/iter-01/input.json
```

For every `tool_call` and `tool_result` pair in `steps.jsonl`, write down:

- step index
- tool name
- arguments or query used
- result summary such as result count, timeout, or structured error
- why the next step happened

## Phase 3: Decompose The Pipeline

When a failure is unclear, isolate the pipeline instead of editing immediately.

- Prompt shaping: what exact user message hit the system?
- Route selection: did the request go to the correct subsystem?
- Query construction: was the search / lookup query well formed?
- Tool execution: did the tool actually return relevant evidence?
- Final synthesis: did the assistant use the evidence instead of deflecting or hallucinating?

When there are multiple searches or retries, break them apart individually. Record what search 1 asked for, what came back, what search 2 changed, and whether the second step improved evidence or only repeated work.

If needed, inspect runtime logs, audit traces, and supporting product code separately before recombining them into a fix.

## Phase 4: Kill Slow Or Low-Signal Loops

If a loop is too slow or too noisy, replace it.

- Replace repeated full runs with suite or test reruns.
- Replace aggregate-score reading with direct artifact inspection.
- Replace speculative fixes with trace-backed fixes.
- Replace unstable end-to-end investigation with deterministic unit coverage where possible.

Anti-patterns:

- rerunning all suites after every small change
- trusting `FinalScore` without reading the response
- editing multiple unrelated subsystems before isolating the broken stage
- changing harness behavior when product logic can be fixed instead

## Phase 5: Apply General Product Fixes

Fix the narrowest product seam that explains the failure class.

Preferred targets:

- `packages/agent/` for route, fallback, grounding, synthesis, tool incorporation
- `apps/headless-runtime/` for event transport, sandbox behavior, runtime execution issues
- `apps/ui-avalonia/` only when the product defect is in desktop rendering or interaction

Avoid:

- test-id branches
- keyword injection for known prompts
- changes to harness score thresholds or suite expectations
- fake tool calls or fabricated evidence

## Phase 6: Verify In Expanding Rings

After a fix:

1. rerun the failing test
2. rerun the containing suite
3. rerun the full harness only after local evidence says the fix is stable

Use `--judge model` selectively when `judge none` appears to over-score or under-score a response.

## Phase 7: Record The Derivation

For every harness-driven change, record:

### What Failed

- failing test id
- hard failures and soft penalties from `score.json`

### Root Cause

- precise broken layer
- specific file and method responsible

### Fix

- exact product change made

### Why It Generalizes

- why this improves similar prompts, not only the observed case

## Practical Default Workflow

1. Run full baseline once.
2. Group failures by subsystem and symptom.
3. Pick one failure cluster.
4. Read artifacts for one representative failing or suspiciously-passing test.
5. List each tool step, its arguments, and its result before touching code.
6. Decompose the stage failure.
7. Fix product logic.
8. Rerun test, then suite.
9. Repeat until the cluster is stable.
10. Rerun full harness.

## Escalation Rule

If the artifacts show a measurement defect rather than a product defect, stop and document it explicitly. Harness changes are allowed only for real runner, isolation, or artifact-capture defects, never to make existing failures score better.