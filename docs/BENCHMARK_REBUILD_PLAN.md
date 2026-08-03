# Benchmark Rebuild Plan

> **This is a research and maintenance plan, not a v1 release gate.** Work lands
> incrementally. Unfinished phases do not make otherwise supported releases
> impossible.

The current product has 151 conversation fixtures across 16 suite directories,
plus targeted private-evaluator campaigns. That breadth is useful, but fixture
counts alone do not prove outcome coverage. This plan converts the estate into
versioned banks without erasing historical trends or pausing productive
experiments for a months-long rewrite.

Metric meanings and retention mechanics live in
[BENCHMARK_METRICS.md](BENCHMARK_METRICS.md). Experiment promotion policy lives
in [EXPERIMENTATION.md](EXPERIMENTATION.md).

## Bank versioning rules

- Treat every executed frozen bank as immutable.
- Add new cases in a new bank version; never rewrite or renumber old cases.
- Record bank ID, version, content hash, scorer version, source/provenance,
  creation date, and lifecycle state.
- Mark replaced banks `retired` or `rebaselined` with a reason and successor.
- Keep development, exact-repeat, and disjoint validation identities separate.
- Do not inspect hidden expected answers while changing production behavior.
- Semantic mutations must change entities, numbers, wording, and tool names
  without copying answer fragments into runtime code.

## Capability coverage map

What each capability can prove today, and what a rebuild would have to add. This
map targets the work; it is not a gate and it carries no thresholds. Phase 3
draws its next bank from this table rather than from a fixture count.

| Capability | Automated outcome coverage | Current state | Rebuild action |
| --- | --- | --- | --- |
| Ordinary no-tool chat | Smoke, quality, personality | Broad but mostly response-scored | Freeze a small deterministic release cohort |
| Current local date/time | Promoted answer-blind campaigns | Strong narrow evidence | Add permanent zero-call regression cases |
| Calculator and Python | Smoke and solver/python probes | Good execution coverage | Add final-outcome and wrong-expression families |
| Web search | Live and stub harness suites | Network-sensitive; response-heavy | Separate a stable-source strict lane from the live diagnostic lane |
| Local text/CSV/RTF read | Private outcome campaigns | Heterogeneous misses | Rebuild around explicit selected-resource identity |
| PDF/DOCX/XLSX read | Private native-document campaigns | Partial; XLSX remains weak | Human-review fresh fixtures; distinguish selection from rendering |
| Wiki attached evidence | Promoted compact packet evidence | Strong narrow evidence | Freeze retrieval-off and contradictory-evidence controls |
| Wiki root creation | Promoted final-state evidence | Strong | Preserve semantic and no-action mutations |
| Typed Wiki read/create/update/rename/delete | Promoted validation evidence | Strong, but absent from the older broad bank | Build a fresh typed-operation temporal bank |
| Wiki revisions and undo | Product and manual coverage | Not a frozen benchmark | Add exact-version and restored-state observations |
| File creation/write | Rejected schema-only candidates | No promotable capability evidence | Do not rebuild until a materially different explicit contract exists |
| System command | Audited path and targeted outcome suite | Adequate selection; mixed binding and rendering | Preserve permission and sensitive-output controls |
| Memory recall and continuity | Routing-latency, one stage fixture, product tests | Under-measured as outcomes | Build multi-turn retained/forgotten/ephemeral families |
| Approved plans, effects, receipts | Focused product tests and rejected oracles | Product surface stronger than benchmark coverage | Add step-progression and verified-receipt tasks |
| Stop, pause, redirect, takeover | Product and manual testing | Not benchmarked as task outcomes | Add stale-call suppression and cancellation-state cases |
| Voice, screen, clipboard | Manual and beta release checklist | Optional and platform-specific | Keep separate platform sheets with explicit skips |

## Incremental sequence

### Phase 0: mechanical foundation

- [x] Define stable metrics, gate classes, missing-data behavior, and lifecycle.
- [x] Add a versioned retention registry and checker to the private evaluator.
- [x] Seed known wins as non-blocking proposed evidence.
- [ ] Normalize one existing immutable campaign into a complete snapshot.
- [ ] Review and activate the first future retention floor without retroactive
      tuning.

### Phase 1: outcome trust

- [ ] Build a small frozen false-success bank covering claimed writes, reads,
      permissions, tool errors, and unavailable resources.
- [ ] Score final state or required evidence independently of reply wording.
- [ ] Add false-success labels to normalized case output.
- [ ] Add semantic mutations and irrelevant-tool distractors.
- [ ] Exact-repeat, validate, and activate only demonstrated floors.

### Phase 2: context and efficiency

- [ ] Capture prompt tokens, reserved output, context limit, truncation, and
      overflow from the provider boundary.
- [ ] Report context headroom and require zero silent overflow on a frozen bank.
- [ ] Normalize calls, retries, tokens, end-to-end time, first token, and VRAM.
- [ ] Add cost-per-verified-outcome when provider or compute cost is configured.
- [ ] Use matched latency statistics; do not promote small-sample p95 noise.

### Phase 3: additive capability coverage

- [ ] Add fresh, disjoint typed-operation tasks for the next target seam.
- [ ] Add permission denial, allow-once, session, and persistent-policy outcomes.
- [ ] Add local-file, Wiki, web-evidence, routine, memory, and continuity banks
      only as those seams are actively changed.
- [ ] Add desktop, voice, and platform-specific numeric gates only where a
      reliable automated oracle exists; keep honest manual checks otherwise.

### Phase 4: capacity portfolio refresh

- [ ] Preserve existing capacity results as historical bank versions.
- [ ] Add or refresh MMLU-Pro, GSM1k, ARC-Challenge, DROP, and IFBench/IFEval in
      disjoint development and validation versions.
- [ ] Keep raw-model and same-prompt controls separate from harness outcomes.
- [ ] Rebaseline explicitly when upstream benchmark releases or scoring change.

Capacity work is intentionally deferred until the outcome-trust and telemetry
foundation is usable. It should not block harness experiments that already have
sound frozen controls.

## Fixture authoring worksheet

Use this for each new case; do not retrofit checkmarks onto old cases without
reviewing the actual fixture and scorer.

- [ ] Stable case ID with no expected-answer fragments.
- [ ] Declared lane, outcome family, source family, and product seams.
- [ ] Public user input separated from hidden expected state.
- [ ] Deterministic setup and cleanup.
- [ ] Independently observable final state or evidence requirement.
- [ ] Explicit permission and tool-availability assumptions.
- [ ] Negative, no-action, or unavailable-resource behavior where relevant.
- [ ] Semantic mutation or disjoint sibling reserved for validation.
- [ ] Scorer version, bank version, content hash, and provenance recorded.
- [ ] Expected calls/arms and maximum campaign cost predeclared.
- [ ] Missing telemetry behavior declared.
- [ ] Human review confirms the task represents useful product behavior.

## Working cadence

Prefer one small bank or normalization seam at a time:

1. Audit the current fixture and scorer.
2. Predeclare the bank contract and cost.
3. Add a handful of useful cases additively.
4. Run deterministic scorer tests before model calls.
5. Run the balanced development slice, exact repeat, then disjoint validation.
6. Record the verdict and either activate, retain as proposed, or retire.

This cadence preserves running room: new research can continue while the
measurement system becomes broader and more trustworthy.

## What deliberately stays out

Named so that a future reader does not mistake absence for oversight.

- **Frozen GSM1k, ARC-Challenge, DROP, and IFBench/IFEval selections.** The
  capacity lane is paused; rebuilding four banks for a paused lane is the
  largest available waste. Phase 4 picks them up if the lane resumes.
- **Calibration and risk/coverage curves.** Abstention behavior is near-floor on
  a 1.2B model, so the curve carries little signal for the current target.
- **Language-quality guardrails and an unrelated-capability regression
  battery.** These matter for adapter and training work, which is not active.
- **Desktop/headless parity samples and voice/screen/clipboard acceptance
  sheets.** Keep these as honest manual checks; do not put a numeric gate on a
  platform surface without a reliable automated oracle.

## Definition of readiness

The measurement system is trustworthy when:

- every release-blocking metric has an automated source or a signed manual one;
- every required metric declares its missing-data behavior;
- every retained win maps to at least one active retention entry;
- safety and trust gates cannot be averaged away by gains elsewhere;
- context overflow is a hard failure and headroom is visible;
- speed gains remain visible when correctness is flat, and outcome gains remain
  visible when they cost more, with the trade reported explicitly;
- model changes, harness changes, and product-quality changes cannot be mistaken
  for one another; and
- a human can open one snapshot and see what improved, what regressed, what was
  skipped, and what was not observable.
