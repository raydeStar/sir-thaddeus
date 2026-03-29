---
name: 'E2E Harness Self-Fixing Protocol'
description: 'Rules for working with the Sir Thaddeus E2E test harness. Governs how to diagnose, fix, and verify failures without gaming the scoring system.'
applyTo: 'tools/SirThaddeus.Harness/**,tools/SirThaddeus.Harness/StageSuites/**,packages/agent/**,tests/SirThaddeus.Tests/Integration/**'
---

# E2E Harness — Self-Fixing Protocol

You are working on a test harness that evaluates AI response quality. Your job is to fix the underlying agent logic so it produces better responses. You are NOT trying to make tests pass. You are trying to make the agent work correctly.

## Running the Harness

```powershell
# Full run — all suites
.\dev\harness.ps1 --all --judge none

# Single suite
.\dev\harness.ps1 --suite <name> --judge none

# Single test within a suite
.\dev\harness.ps1 --suite <name> --test <test_id> --judge none

# With AI judge scoring (requires local LLM or --judge cursor)
.\dev\harness.ps1 --suite <name> --judge model
```

The harness project lives at `tools/SirThaddeus.Harness/`. The entry point script is `.\dev\harness.ps1`.

## Reading Failure Artifacts

Every test run writes artifacts to:

```
artifacts/harness/<run-id>/<suite>/<test-id>/iter-XX/
├── input.json          ← prompt sent to the agent
├── steps.jsonl         ← full tool call and result trace
├── final.txt           ← the agent's final response text
├── score.json          ← detailed score breakdown with penalties
├── judge_packet.json   ← context sent to judge (if enabled)
└── judge_result.json   ← judge verdict (if enabled)
```

When diagnosing a failure, read these files in this order:

1. `score.json` — which scoring dimensions took the hit?
2. `final.txt` — is this a real answer or a deflection?
3. `steps.jsonl` — did the agent call tools? did it use the results?
4. `input.json` — what was the agent actually asked?

If `score.json` says `PASS` but `final.txt` is clearly weak, hedged, incorrect, or unsupported, continue diagnosis. Treat the run as suspicious until the discrepancy is explained.

When reading `steps.jsonl`, break the run into explicit stages. For each tool call, note:

- the exact query or arguments
- what came back
- whether the result was relevant
- what the agent did next because of that result

If there were multiple searches, retries, or fallbacks, document them separately instead of collapsing them into "the agent searched".

## Stage Suites

Use stage suites under `tools/SirThaddeus.Harness/StageSuites/` for fast, deterministic checks of preprocess, classify, and query seams.

- Stage suites are for diagnosis and regression coverage of pipeline behavior, not a replacement for end-to-end harness validation.
- When testing vague follow-ups with fabricated context, prefer an explicit `followup_anchor` in the stage test `context` block over brittle assistant-text-only parsing.
- Keep stage suite expectations behavioral and general. Do not encode test-specific shortcuts into product code just because a stage suite is deterministic.

## Diagnosing the Root Cause

Before writing any code, identify which layer failed:

- Agent did not call any tools → fix intent routing in `packages/agent/`
- Agent called tools but ignored results → fix response synthesis in `packages/agent/`
- Agent deflected with "I can't help" → fix fallback/error handling in `packages/agent/`
- Agent called wrong tools → fix tool selection/policy in `packages/agent/`
- Agent hallucinated content → fix grounding/citation logic in `packages/agent/`
- Response is all hedging, no substance → fix confidence calibration in `packages/agent/`

## What You Must Do

- Read failure artifacts to understand what actually went wrong.
- Break multi-step traces into numbered stages so the break point is explicit.
- Fix the problem in the source logic: routing, synthesis, tool selection, prompts, fallback handling.
- Ensure your fix works for ANY similar input, not just the specific test case.
- Include a derivation trace explaining: what failed, the root cause, why your fix solves it generally.
- Re-run the harness after every fix to verify improvement.

## What You Must Never Do

- DO NOT open, read, grep, or reference any `.yaml` test spec files under `tools/SirThaddeus.Harness/Suites/`. These contain expected keywords, tool lists, and scoring thresholds. Looking at them defeats the purpose of the test.
- DO NOT hardcode responses. No `if (input.Contains("specific test phrase"))` anywhere.
- DO NOT add test-specific branches in agent code. Your fix must be general-purpose.
- DO NOT lower `min_score` values in test YAML to make failing tests pass.
- DO NOT modify `ScoringEngine.cs` to be more lenient unless explicitly asked to.
- DO NOT fake tool calls or fabricate tool results.
- DO NOT return canned responses for recognized test patterns.
- DO NOT "fix" a test by making the test expect less. Fix the agent to deliver more.

## Harness Edit Boundary

Treat `tools/SirThaddeus.Harness/` and `dev/harness.ps1` as protected infrastructure.

- Default rule: do not modify the harness, scorer, suite loader, runner, or wrapper script when a failing test can be fixed in agent/runtime/tool code.
- Allowed harness changes must be limited to real test-quality defects such as:
	- execution bugs in the wrapper or runner
	- non-hermetic state leakage between tests or runs
	- artifact capture bugs that misreport what happened
	- legitimate replay/stub issues that prevent the harness from observing true behavior
- Disallowed harness changes include:
	- making scoring more lenient for existing failures
	- weakening assertions, thresholds, or expected structure
	- hiding tool failures, deflections, or missing evidence
	- special-casing known failure patterns so they score better without a real behavior fix
- Any harness modification requires an explicit justification in the derivation trace explaining why the problem could not be solved in product code and how the harness change improves measurement quality rather than pass rate.

If you catch yourself thinking "I'll just add the expected keyword to a response template" — stop. That is cheating. Fix why the agent does not naturally produce a correct answer.

## Derivation Trace Format

Every fix must include this in the commit message or PR description:

```
## Derivation Trace

### What Failed
[test_id] scored [X]/10. List the penalties from score.json.
If the score and the answer disagree, say so explicitly and quote the relevant part of `final.txt`.

### Root Cause
Explain the actual bug or logic gap. Reference specific files and methods.

### Fix
Describe the code change and where it was made.

### Why This Fixes It Generally
Explain why this works for all similar queries, not just the failing test.
```

## Verify and Loop

After implementing a fix:

1. Run the failing suite: `.\dev\harness.ps1 --suite <name> --judge none`
2. Check `score.json` — did the failing dimensions improve?
3. If score improved but is still below `min_score`, loop back to diagnosis.
4. If score did not change, your fix targeted the wrong layer.
5. If score jumped from near-zero to exactly 10.0, investigate — that is suspicious.

## Pushback Protocol

If you believe a test is unfair, the scoring is miscalibrated, or a requirement is impossible to meet without hardcoding, do NOT silently work around it. Instead, state your objection clearly:

```
## Pushback: [test_id]

### Concern
[Why you think the test or scoring is wrong]

### Evidence
[The actual response and score breakdown]

### Proposed Resolution
[A test fix, scoring adjustment, or alternative approach]
```

The human operator will review. Do not proceed by burying the problem.

## Scoring Dimensions (Reference)

The harness evaluates responses on these dimensions. Understanding them helps you target the right fix:

- **Hard Assertions** (pass/fail, score forced to 0 on failure): required tools called, forbidden tools avoided, response text exists, structured error format, no hallucinated URLs.
- **Keyword Coverage** (up to -5.0): did the response contain expected terms naturally?
- **Deflection Penalty** (up to -7.5): did the agent say "I can't help" instead of answering?
- **Tool Result Incorporation** (up to -4.0): did the agent use the data it gathered from tools?
- **Assertion Density** (up to -3.0): is the response all hedging or does it make claims?
- **Forbidden Keywords** (-1.5 each): profanity, competitor names, banned phrases.
- **Response Length**: too short despite tool usage (-1.5), over max chars (-1.0).
- **"As an AI" Phrasing** (-0.5): cop-out self-reference.
- **Personality Heuristics**: signature, verbosity, empathy, structure expectations.
- **AI Judge** (when enabled): correctness, completeness, synthesis quality, confidence calibration. Weighted at 70% of final score when active.