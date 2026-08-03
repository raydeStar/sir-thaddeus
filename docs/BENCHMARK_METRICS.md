# Benchmark Metric Registry

This is the stable specification for Sir Thaddeus benchmark measurements. It
defines what a metric means, where it comes from, and how missing data behaves.
It is not a release checklist and it does not claim that every desired metric
is implemented.

Sir Thaddeus keeps three independent lanes:

1. **Model capacity**: closed-book knowledge and reasoning by the frozen model.
2. **Harness capability**: independently verified outcomes produced with tools,
   retrieval, state, permissions, and orchestration.
3. **Product quality**: latency, safety, continuity, validity, permissions,
   false success, and resource use around those outcomes.

A campaign declares one primary lane. The other two remain visible guardrails;
they are never blended into a single flattering score.

## Mechanical retention registry

Known wins live in the private evaluator's versioned
`experiments/retention.json`, not in Markdown checkboxes. The registry records:

- a stable entry ID and one or more product-seam tags;
- lane, immutable bank identity, version, and content hash;
- compatible `baseline_id` and source run IDs;
- explicit metric operators, thresholds, tolerances, and missing-data behavior;
- estimated case-evaluation cost; and
- approval record, lifecycle state, evidence, retirement, rebaseline, and
  successor records.

The evaluator exposes:

```powershell
benchrun retention validate --registry experiments/retention.json
benchrun retention check `
  --registry experiments/retention.json `
  --snapshot artifacts/retention/snapshot.json `
  --seam wiki-typed `
  --out artifacts/retention/latest `
  --fail-on-blocked
```

The checker emits machine-readable JSON and a human-readable Markdown report.
Seam selection is repeatable, so a change may request more than one `--seam`.
The report states the declared case-evaluation cost before a hot campaign.

The initial registry is intentionally **proposed-only**. Historical verdicts
are preserved, but they do not become release gates until the project owner
reviews a future floor and tolerance against a compatible frozen bank.
Inventing thresholds after seeing the historical results would violate the
experiment contract.

## Run compatibility

`baseline_id` identifies a comparison contract, not just a Git SHA. Generate a
new ID when any score-affecting component changes:

- model artifact, quantization, provider-visible model ID, or context window;
- sampling, maximum output, prompt/config, or tool manifest;
- product or evaluator behavior;
- bank content, scorer, normalization, or aggregation; or
- environment policy that changes tool availability or resource measurement.

The snapshot must also record product and evaluator SHAs. An active entry blocks
on a `baseline_id` mismatch. Compatible frozen controls may be reused only after
an unchanged-harness sentinel shows no drift.

## Metric definitions

Availability is descriptive: **captured** means the evaluator schema supports
the field, **campaign** means some campaigns derive it, and **planned** means a
normalized implementation or bank is still required.

| Metric ID | Lane | Definition and aggregation | Source | Availability | Default missing behavior |
| --- | --- | --- | --- | --- | --- |
| `strict_outcome_rate` | Harness or capacity | Strict passes divided by all attempted cases; never drop invalid rows | `CaseResult.passed` | Captured | Block when hard |
| `family_completion_rate` | Harness | Independently verified final-state passes by declared outcome family | final-state scorer and family tag | Campaign | Block when hard |
| `validity_rate` | All | Valid responses divided by all attempts | `CaseResult.is_valid` | Captured | Block when hard |
| `paired_wins`, `paired_losses`, `paired_ties` | All | Same-item candidate versus unchanged control | comparison report | Captured | Block when hard |
| `activation_rate` | Harness | Cases where the candidate mechanism activated as predeclared | trace diagnostics | Campaign | Block for attribution |
| `model_calls` | Product quality | Total and matched per-case model calls | `CaseResult.model_calls` | Captured | Warn unless hard |
| `tool_calls` | Harness or product quality | Total and categorized calls, including escalation | `CaseResult.tool_calls` | Captured | Warn unless hard |
| `tokens_total` | Product quality | Input plus output tokens, total and matched per case | token fields | Captured | Warn unless hard |
| `latency_e2e_ms` | Product quality | User request to final response; paired median plus declared tail statistic | `CaseResult.latency_ms` | Captured | Warn unless hard |
| `latency_first_token_ms` | Product quality | User request to first visible response token | `CaseResult.first_token_ms` | Captured | Warn unless hard |
| `peak_vram_mb` | Product quality | Maximum provider-observed VRAM during the arm | `CaseResult.peak_vram_mb` | Captured | Unknown unless required |
| `retry_rate` | Product quality | Retries divided by attempted cases | attempt and retry fields | Captured | Warn unless hard |
| `false_success_rate` | Harness or product quality | Replies claiming completion when final state or required evidence disproves it | new frozen negative bank | Planned | Block when active |
| `context_headroom_tokens` | Product quality | Context limit minus maximum observed prompt plus reserved output | prompt/provider telemetry | Planned | Block when active |
| `context_overflow_count` | Product quality | Requests truncated, rejected, or silently clipped by context pressure | provider and prompt telemetry | Planned | Block when active |
| `permission_burden` | Product quality | Prompts, denials, and redundant prompts per verified outcome | permission trace | Planned | Warn unless hard |
| `cost_per_verified_outcome` | Product quality | Declared compute/API cost divided by independently verified successes | cost plus outcome records | Planned | Unknown until configured |

Formatting, plausible plans, valid JSON, and LLM-judge approval are enabling
signals. They are not proof that work completed.

## Gate classes and missing data

- **Hard**: a predeclared floor or ceiling required for promotion. Missing
  telemetry is `block`.
- **Trend**: a metric that must remain visible and within a reviewed tolerance.
  Missing telemetry is normally `warn`, never silently zero.
- **Diagnostic**: context for interpretation with no promotion authority.
  Missing telemetry remains `unknown`.

An active hard entry must provide a SHA-256 bank hash, compatible baseline ID,
source run IDs, threshold, tolerance, `block` behavior, and a dated approval
record. Proposed evidence cannot block. A stale entry warns and must be
refreshed before relying on it.

## Trust and safety hard gates

These are release boundaries. They are never averaged against a gain in another
lane, and a single confirmed case blocks regardless of sample size.

| Boundary | Source | Availability |
| --- | --- | --- |
| Required and forbidden tool contracts | `CaseResult.tool_calls` | Captured |
| Permission decisions honored across deny, once, session, and always | campaign records | Captured |
| Stateful no-action controls: nothing changed when nothing was authorized | final-state scorer | Captured |
| Typed target mismatch fails before any side effect | final-state scorer | Captured |
| Structured-error and hallucinated-citation assertions | harness suites | Captured |
| Unauthorized effect, target escape, and permission bypass | consolidated zero-tolerance bank | Planned |
| Denied-action truthfulness: the reply states the action did not happen and does not invent its result | reply plus final state | Planned |
| Interruption safety: stale proposed calls stay skipped after redirect or takeover | audit records | Planned |
| Audit completeness: proposed effect, decision, execution, outcome, and undo relationship all present | audit records | Planned |

Unnecessary permission prompts are counted separately from violations. A prompt
that did not contribute to a verified outcome is an experience cost, not a
safety failure, and belongs to `permission_burden`.

## Small-sample rules

Most frozen harness banks hold 16-18 cases. These rules define what such a bank
can support.

**Latency.** Do not use a 16- or 18-case p95 as a stable release ratchet. With
fewer than 30 matched observations, report paired per-case deltas, median, total
elapsed time, and the slowest cases. A tail percentile may remain descriptive.
It becomes a gate only with at least 30 compatible observations or a
mechanism-specific hard ceiling predeclared before the run.

**Outcomes.** Prefer paired metrics over aggregate counts. On an 18-case bank,
`strict_outcome_rate >= 16/18` is noise-sensitive and will produce false blocks
that get waived; `paired_losses <= 1` against the frozen control is robust,
individually inspectable, and encodes "do not lose this win" directly. Gate a
retained win on `paired_losses` first, and use the strict rate as a `trend`
companion rather than the primary floor.

Every gate includes an explicit tolerance declared before the run. Correctness
cannot be traded for speed unless the experiment manifest predeclares the trade.

## Seam and cost mapping

Seams are a **closed vocabulary**. "Relevant seam" must not be a judgment call
at promotion time, or seam selection erodes under deadline. Adding a seam is a
reviewed registry change, not an ad-hoc tag.

| Area | Seams |
| --- | --- |
| Wiki | `wiki`, `wiki-typed`, `wiki-root`, `attachments`, `evidence`, `non-action`, `temporal` |
| Files and resources | `files`, `path-resolution`, `system-command` |
| Routing and contracts | `tool-routing`, `tool-evidence`, `response-contract`, `answer-only`, `approved-plan` |
| Deterministic utility | `datetime`, `deterministic-utility` |
| Recovery and cost | `recovery`, `repair`, `retry`, `latency`, `confirmation` |
| Trust | `permissions`, `tool-errors`, `false-success` |
| Experience | `thinking-model`, `sanitization`, `memory`, `continuity` |
| Research and web | `web-search` |
| Control | `stop-control` |

A change selects the smallest relevant set, then expands only after focused
gates pass. Cross-cutting changes select every affected seam.

`estimated_case_evaluations` counts planned case/arm executions, not distinct
prompts. The checker sums active selected entries, so an entry may not become
`active` with a zero or unrecorded cost — the declared-cost line would then
understate a hot campaign. Large multi-model, repeated, or validation campaigns
still require the acknowledgement defined in
[EXPERIMENTATION.md](EXPERIMENTATION.md).

## Lifecycle

- **Proposed**: historical or new evidence awaiting a reviewed future gate.
- **Active**: mechanically enforced for a compatible baseline and bank after a
  dated owner approval.
- **Stale**: evidence may no longer represent production; warns but does not
  masquerade as a passing gate.
- **Retired**: preserved with date/reason and optional successor; never deleted.
- **Rebaselined**: the old trend ends explicitly, with reason and successor.

Never edit or renumber a frozen bank in place. Create an additive version. If a
scorer, bank, or model contract must change, close the prior trend with a
rebaseline record and start a new one. Retiring a capability requires a written
reason rather than deleting its history.

## Activating a proposed win

1. Locate immutable raw artifacts and compute the bank content hash.
2. Normalize compatible baseline and candidate observations into a snapshot.
3. Predeclare the future floor, tolerance, missing behavior, and campaign cost.
4. Run the unchanged sentinel and exact candidate repeat.
5. Review the evidence; then change the registry status to `active`.

The registry is a ratchet, not a second benchmark implementation. Campaign
modules remain responsible for producing honest normalized observations.
