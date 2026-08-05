# Calibrated Improvement Plan

**Status:** maintenance-ready; harness-capability work may continue, while the
MMLU capacity campaign is paused pending a materially different mechanism

**Calibrated:** July 21, 2026; segment closure updated August 3, 2026
**Production baseline at calibration:** `84f96e6`
**Current production baseline:** `a36937bf`

## Decision

Sir Thaddeus has the right foundation. The next phase is not a broad
orchestration rewrite. It is a sequence of small, falsifiable optimizations at
existing seams, promoted only when they improve a declared scorecard without
damaging the others.

The project has passed MVP and does not require continuous architectural work.
During maintenance periods, prioritize release health, documentation,
discoverability, and external reproduction over creating another candidate.
Resume behavioral experiments only when a fresh measured failure cluster or a
materially different research mechanism supplies a falsifiable reason.

The project will no longer treat a newer or larger comparison model as an
improvement to the harness. Model comparisons remain useful as diagnostic
ceilings, cross-model transfer checks, or explicit escalation studies.

### August 4 model-panel recalibration

Behavioral discovery now uses the fixed panel in
[MODEL_TIER_CALIBRATION.md](MODEL_TIER_CALIBRATION.md): LFM 1.2B remains the
floor and safety stress model, LFM 2.6B is an edge-default candidate pending
qualification, LFM 8B-A1B is the primary discovery anchor, and Gemma 26B-A4B is
the ceiling transfer check. This responds to the repeated strict-family gain at
8B and above without pretending that parameter count certifies a feature.

Production remains model agnostic. Optional capabilities are exposed by an
exact-configuration certificate and user-selected Auto, On, or Off policy. A
safe lower-tier miss withholds that function in Auto mode; it does not retire
the model or erase a repeatable anchor-model gain.

### August 3 scorecard and typed-operation safety segment closure

The reviewed 64-case scorecard and its causal follow-ups are complete. On the
fresh scorecard, frozen LFM 2.5 1.2B scored `23/64` strict and `9/32` strict
families. LFM 2.5 8B-A1B scored `26/64` and `12/32`: a modest breadth gain, but
verified Wiki mutations fell `5/8 -> 2/8`, so model size is not a capability
certificate and the material-transfer hypothesis was rejected.

A fresh human-name Wiki diagnostic showed unchanged 1.2B already completing
`5/6` create/update mutations. Withholding opaque-ID mutation tools was stopped
before its oracle because the frozen three-miss prerequisite did not exist.
The subsequent typed identity/operation diagnostic localized the real contract:
identity alone is intentionally read-only, while an explicit operation enables
the selected write. Automatic operation inference and broad tool-surface
pruning are closed on this evidence.

That diagnostic also exposed a safety conflict when a previously selected
operation met current prose such as "do not act" or "do nothing yet." A narrow
operation-aware safety candidate improved development `6/12 -> 12/12`, repeated
`12/12`, and improved disjoint validation `5/12 -> 10/12` with five paired wins,
zero losses, all `6/6` no-action controls, and zero forbidden writes. It was not
promoted because two authorized validation outcomes failed the frozen absolute
gate in both unchanged and candidate. Their correct update calls copied the
whole instruction into Markdown instead of the requested replacement value,
localizing a separate semantic-content binding seam. The safety candidate is
closed without a production change; do not relax its gate or tune against the
consumed validation set.

This segment is complete. Reopen Wiki mutation work only as a new, independently
predeclared semantic-content experiment with scorer-blind typed inputs or a
gold-content oracle. Do not add another prose router, infer user authorization,
or use an `8B+` model-size rule as a substitute for exact-configuration
capability certification.

### Standardized external evaluation direction

The semantic-delta prerequisite subsequently found unchanged LFM 2.5 1.2B at
`6/6` exact authorized typed updates, so the gold arm had no positive headroom
and did not run. Do not lower that baseline with contrived wording and call the
result uplift. Preserve the reviewed 64-case bank; if local coverage expands,
add no more than 32 fresh cases across ambiguity, stateful composition,
capability interference, and failure/recovery.

The next infrastructure seam is a thin evaluator-owned adapter for the current
pinned MCPMark ten-task filesystem easy tier, followed by Verified standard
tasks. It must compare the same frozen model under the upstream scaffold and
unchanged Sir, keep benchmark tools and verifiers outside production, and route
by protocol shape rather than task or benchmark identity. ToolSandbox is the
targeted safety follow-up; tau-bench custom and AgentBeats are later public
interoperability lanes. The complete contract is maintained in the
[standardized agent evaluation path](research/STANDARDIZED_AGENT_EVALUATION_PATH_2026-08-03.md).

### July 19 harness redirect result

The first verified-outcome redirect block is complete without a product
candidate. On the frozen 32-task local development scorecard, raw minimal and
unchanged Thaddeus each scored `9/32`, while same-prompt direct scored `6/32`.
A scorer-blind declared-capability oracle reached `13/32` twice, but the compact
product-invariant prompt form reached only `11/32` and missed its predeclared
gate. Verified structured failure evidence also failed at `0/3` under the
production prompt. The routing and failure-presentation paths are closed for
this block; MMLU remains capacity-only, and no runtime behavior changed.

The next candidate must begin with a fresh oracle-measured evidence-to-outcome
failure cluster and independently verifiable success. Do not tune another
conversation router or response-contract detector against the consumed slice.

### July 20 fresh outcome discovery result

The staged fresh-outcome screen completed without authorizing another product
candidate. Unchanged Thaddeus scored `7/16` strict and `11/16` valid across
eight ordinary-work categories. The only clean two-case failure region was
local-file extraction, but its preauthored reserve passed `1/1` with a
successful `file_read`. No qualifying three-case cluster remained, so no oracle
or product mutation ran. Invest next in economical labeled-outcome accumulation
rather than another router, prompt, retrieval, or postcondition candidate.

### July 21 read-only inventory and outcome-census result

The isolated read-only inventory scored `2/8` strict and `7/8` valid. Its file
and Wiki misses mapped to already closed path-binding, tool-commitment,
typed-interface, and multi-step sequence families, so no oracle or candidate
ran. A first batched artifact was rejected as invalid evaluator infrastructure;
the recovery used one case per process and changed no production behavior.

A zero-model-call answer-blind census then reused 57 compatible frozen outcomes
across five cohorts. It measured `29/57` strict and `50/57` valid, while
confirming verified computation at `6/6` and verified state change at `5/6`.
Manual attribution found every numeric failure cluster already closed or
heterogeneous. Do not convert aggregate counts into another router, prompt,
literal-response, path-recovery, retrieval, or generic verification candidate.
The next paid cohort must be predeclared at an under-sampled, independently
verifiable, materially open product seam. These 57 outcomes are not prevalence
evidence and do not satisfy the 300-outcome complementarity gate.

### July 21 local document-reading discovery result

A newly authored, local-only CSV/RTF cohort scored `8/12` strict and `11/12`
valid in 86.854 seconds. Field extraction and row selection each reached `3/4`;
table aggregation reached `2/4`. Ten `file_read` calls returned usable evidence,
but the four misses split across semantic column binding, failure to commit to
the read, an incorrect path, and incomplete arithmetic after a successful read.
No category met the frozen three-valid-failure gate, so no oracle, repeat,
validation, or product mutation ran. Keep document reading as a measured
capability boundary; do not build a parser, router, retry, or document-query
tool from this heterogeneous slice.

### July 21 native document-reading discovery result

The reusable harness can now seed bounded binary fixtures inside its existing
isolated file root; this is evaluation infrastructure, not a production upload
or assistant behavior change. All twelve newly authored PDF, DOCX, and XLSX
fixtures passed the real production readers before inference.

The frozen unchanged-harness cohort then scored `6/12` strict and `10/12`
valid in 83.035 seconds. Field extraction reached `3/4`, row selection `2/4`,
and aggregation `1/4`; PDF/DOCX/XLSX scored `2/4`, `3/4`, and `1/4`
respectively. The low XLSX slice is not a post-hoc oracle gate: its misses split
between two invalid path mutations and one adjacent-column interpretation
error. Across the full cohort, the other misses included one absent read, one
answer-entity omission, and one counting error. No predeclared category supplied
three aligned valid failures, so no oracle, repeat, validation, or product
candidate ran. Evaluator PR `#130`.

### July 21 XLSX column-fidelity headroom result

A fresh ten-case XLSX diagnostic stopped before its conditional gold arm. The
production prompt exposed exactly one `file_read` tool on each isolated case,
but the model produced no successful reads: nine turns made no tool call and one
changed the requested filename before receiving an access denial. Strict score
was `0/10`, validity was `7/10`, and the six sparse-cell positives had zero
coordinate-loss activations because no workbook evidence reached the model.

Static inspection still shows that the XLSX reader ignores cell references and
can collapse absent middle cells, but this artifact did not test that layer. No
reader fix, forced-tool arm, prompt mutation, gold run, repeat, or product branch
is authorized from it. Revisit column-faithful rendering only after fresh
ordinary outcomes contain at least three successful XLSX reads with aligned
downstream misses. Evaluator PR `#133`.

### July 21 system-command outcome discovery result

A fresh local-only ten-case diagnostic tested six authorized, allowlisted
read-only command outcomes, three hypothetical/negated/deferred no-action
controls, and one blocked-metacharacter control. Unchanged Thaddeus scored
`6/10` strict and `10/10` valid in 36.24 seconds. All four safety/no-action
controls passed. The model selected `system_execute` on five of six authorized
requests, and all five selected calls executed successfully.

The four authorized misses did not form a routing cluster: one omitted the
tool, one selected it with the wrong command arguments, and two had correct
tool evidence but violated the requested exact response. A forced-tool-name
candidate could address only one case and therefore failed the predeclared
three-case authorization gate. No product branch, repeat, or holdout ran. Raw
`systeminfo` output contains local machine details and remains ignored; only
answer-blind telemetry was published in evaluator PR `#134`.

### July 21 deterministic date-arithmetic result

Eight fresh fixed-date tasks established a large evidence ceiling: raw minimal
scored `1/8`, unchanged Thaddeus scored `0/8`, and compact gold date evidence
scored `8/8` across calendar differences, date offsets, calendar properties,
and schedule/time arithmetic. This authorized one narrow read-only
`date_calculate` candidate with strict ISO inputs and six explicit operations.

The real production-equivalent candidate then scored only `2/8` strict
positives while preserving `4/4` malformed, missing-input, hypothetical, and
negated controls. Both selected positive calls executed correctly, but the
model selected the tool on only `2/8` positives versus the frozen `7/8` gate.
The result was below both the `6/8` promotion gate and the explicit `5/8` gray
zone. No repeat or validation ran; the product code and branch were deleted.
Evaluator PR `#135` preserves the oracle, candidate, hashes, and rejection.

This is useful causal evidence: deterministic date results solve the outcome
when supplied, and the implementation itself is correct when selected, but a
newly advertised specialist tool is not broadly discoverable by this fixed
1.2B model. Do not tune the consumed prompts or merge the tool alone. A future
selection experiment must use fresh disjoint tasks, preserve no-action
controls, and be materially narrower than global tool forcing.

### July 21 date-selection prerequisite result

A second, disjoint date slice tested whether deterministic first-tool selection
was still the dominant layer before any selector code was written. Raw and
unchanged Thaddeus each scored `0/8` positives. The unchanged v1 date tool then
scored `3/8` positives and `3/6` controls with `14/14` validity, selecting
`date_calculate` on `6/8` positives.

Only two misses were tool omissions. Three others selected the tool but bound
the wrong operation or arguments: forward offset, backward offset, and
recurrence. Because the frozen selector gate required at least three gains over
tool-only, selection could causally address at most two, and the candidate was
stopped before implementation. One zero-call worktree-restore failure was
discarded and replaced by a clean recovery. Evaluator PR `#136` preserves the
fresh bank and verdict.

The current open seam is semantic typed-argument binding, not routing. Any
follow-on must first prove that a deterministic argument oracle corrects a
fresh disjoint cluster. It may not combine tool selection, argument rewriting,
and final-response projection in one candidate.

### July 21 typed date-argument oracle result

The separate v3 prerequisite used six more disjoint positives explicitly
requesting the date capability plus six boundary controls. Tool-only scored
`1/6` positives and `3/6` controls with `12/12` validity. It selected
`date_calculate` on `4/6` positives, below the frozen `5/6` requirement for
declaring semantic binding dominant. Three selected failures did use the wrong
operation or arguments, but both recurrence requests omitted the tool.

The compact gold arm, repeat, and product rewriter therefore did not run. The
invalid date failed closed and none of five no-action/unsupported controls made
a forbidden call; three controls missed only exact response contracts. The
one-off evaluator builder was removed and evaluator PR `#137` preserves the
answer- and argument-blind record.

Close the date family on current evidence. Across three disjoint slices, the
dominant failure alternated among discovery, semantic binding, and response
fidelity. A bundled selector/parser/rewriter/projector might raise a synthetic
date score, but it is not the smallest independently justified harness
mechanism. Reopen only from ordinary labeled outcomes, not more authored date
fixtures.

### July 21 current local date/time utility result

A separate ordinary-utility seam—not date arithmetic—produced a narrow
promotion candidate. A bounded recognizer expands the existing application-
clock path for current local date/time requests while rejecting events,
scheduling, elapsed time, locations, timezone conversion, and compounds.
Unseen answer-blind development coverage improved `5/30 -> 8/30` with
mechanical false activations reduced `1 -> 0`.

The production-equivalent comparison repeated two genuine local-time
correctness gains, six date correctness ties with zero provider calls, and one
repair of a harmful location-scoped wrong-clock route with no observed paired
loss. Repeat calls fell `22 -> 8`, positive first-visible p50/p95 improved
`211.5/260.5 ms -> 5.5/10.3 ms`, and peak VRAM was unchanged. Final public
confirmation retained the direction. Its one mechanical false label was a
direct current-date command omitted by the evaluator selector; the immutable
count and one-row disclosure are both preserved, and neither product code nor
the evaluator was tuned or rerun afterward. Treat this as harness capability
and product quality, never model-capacity or MMLU uplift.

## The claim we are testing

Sir Thaddeus is intended to demonstrate that a small local model can complete a
large share of ordinary work when deterministic capabilities, relevant
evidence, permissioned tools, durable state, and observable verification are
used well.

That claim has three separately measured parts:

| Scorecard | Question | What counts as success |
| --- | --- | --- |
| Frozen-model capacity | Can the exact raw model solve the task without answer-producing tools? | Strict closed-book correctness, validity, calibration, and robustness on fresh items |
| Harness capability | Can the same model complete more useful work with Sir Thaddeus? | Independently verified final state, artifact, or grounded answer with permissions and bounded cost |
| Product quality | Is the assistant safe, coherent, responsive, and trustworthy in daily use? | Personality, continuity, memory, permissions, false-success rate, latency, calls, tokens, and resource use |

No score may substitute for another. A calculator-assisted answer is a harness
win, not a closed-book reasoning win. A larger model's score is a ceiling, not
a fixed-model improvement.

## Architectural posture

Keep the current ordered pipeline and its seams:

```text
safety and deterministic utilities
  -> personality, features, memory, and dialogue context
  -> tool narrowing and freshness policy
  -> primary model and bounded audited tool loop
  -> sanitization, completion validation, and bounded repair
  -> asynchronous memory persistence and final composition
```

Do not introduce a universal planner, recursive reasoning loop, second-model
judge, or replacement router without evidence that the existing seam cannot
support the required behavior. Prefer capability-specific changes that can be
removed completely after rejection.

## Operating rules

1. Freeze the primary model, quantization, provider, context, prompt composer,
   sampling, and item set for a candidate comparison.
2. Use a larger or different model only as a labeled oracle, ceiling, transfer
   cohort, or escalation arm.
3. Predeclare one mechanism, one primary scorecard, controls, activation gate,
   resource budget, promotion rule, and rollback.
4. Prove the mechanism activated before interpreting its accuracy score.
5. Use raw minimal, same-prompt direct, unchanged harness, and candidate arms
   when attribution requires them.
6. Reject cheaply with deterministic checks and a balanced ten-item triage
   before the full development slice; treat ten minutes as a ceiling per hot
   invocation, not a target.
7. Record planned case evaluations and require explicit acknowledgement before
   large, repeated, multi-model, validation, or confirmation campaigns.
8. Reuse a compatible frozen control pack only when all hashes match and a
   small unchanged-harness sentinel shows no drift.
9. Require the exact frozen candidate to repeat before using a disjoint
   validation set.
10. Use oracle-route, oracle-tool, gold-evidence, and gold-state controls to
   locate the ceiling before adding routing machinery.
11. Preserve benchmark integrity: no expected answers, suite identifiers,
   scorer logic, benchmark-specific branches, or hidden strong-model calls in
   production.
12. Record promoted, rejected, inconclusive, and infrastructure results in
    [research/](research/README.md).
13. Treat default token and latency percentages as suggested signals unless a
    predeclared product SLO or mechanism-specific risk makes them hard. Never
    soften safety, permission, validity, false-success, activation, or strict
    outcome gates. Re-evaluate a promising miss only as a fresh revision on
    unconsumed inputs; do not rewrite its completed verdict.

## Phased program

### Phase 0 - Close the previous campaign cleanly

Objective: begin from an unambiguous production and evidence baseline.

- Use current `master` as the production baseline.
- Reconcile retained research branches with their final evaluator verdicts.
- Delete rejected implementations and remote branches after preserving their
  ledger entries.
- Keep only genuinely inconclusive or repeat-qualified research branches, each
  with a current verdict.
- Confirm `dev` matches `master` before the next behavioral experiment.

Exit gate: production is clean, protected CI is green, and every surviving
research branch has an explicit disposition.

### Phase 1 - Make measurement causal

Objective: prevent false gains, no-op candidates, and infrastructure failures
from consuming another campaign.

- Add an activation assertion to every behavioral experiment.
- Test activation through full production composition, not only direct
  `TurnContext` unit construction.
- Keep the fast 50-item capacity battery and add an outcome battery covering
  renamed tool schemas, irrelevant tools, local documents, Wiki/file state,
  and no-tool negatives.
- Separate cold startup, warm provider, product-pipeline, tool, validator,
  end-to-end, and first-visible-content timings.
- Keep public web reliability outside strict local promotion gates unless the
  provider and health state are part of the recorded environment.

Exit gate: a candidate cannot receive a correctness verdict without activation,
valid controls, clean worktrees, and complete timing/call attribution.

### Phase 2 - Local evidence compilation validated

Objective: determine whether actual local retrieval can approach the proven
gold-evidence ceiling without overwhelming the small model.

This is not another web-search router. Use a frozen local Wiki/document corpus
and compare:

1. no evidence;
2. gold evidence;
3. actual retrieval and compact evidence packaging;
4. irrelevant or contradictory evidence;
5. ordinary conversation where retrieval must remain off.

The candidate may change one mechanism only: retrieval selection, reranking,
or evidence packaging. Test them as separate revisions. Preserve full source
metadata for UI/audit outside the compact model-visible packet.

Promotion signal: actual retrieval closes a meaningful fraction of the gold
gap, produces net paired wins over unchanged Thaddeus, adds no unsupported
answers, and remains inside the predeclared latency/token budget.

Stop rule: if the model still fails when given compact gold evidence, do not add
another retriever or router. Classify the remaining miss as model utilization
or capacity.

Result: the frozen 1.2B compact-gold control recovered all 6/6 attached facts.
The deterministic query-focused Wiki packet then reproduced 5/8 overall and
4/6 attached on development versus 1/8 and 0/6 unchanged. On disjoint semantic
mutations it scored 5/8 and 4/6 attached versus 2/8 and 1/6 unchanged, with no
retrieval-off loss or added provider calls. The candidate also reduced p95
latency and model-visible input. This validates a narrow explicit-Wiki harness
capability; it is not a model-capacity or MMLU gain.

### Phase 3 - Improve one verifiable operation at a time

Objective: extend the pattern that worked for Wiki-root creation.

For a single mutable capability:

```text
inspect current state when needed
  -> expose only relevant contracts
  -> model proposes the action
  -> permission boundary executes it
  -> deterministic postcondition checks final state
  -> one repair only after observed failure
```

Begin only when an oracle-state or oracle-action control demonstrates headroom.
Score final state, collateral changes, permission behavior, calls, tokens,
latency, and false-success rate. Do not start with a generic repair framework.

Candidate order:

1. one local document/Wiki retrieval outcome;
2. one tool-semantic slice covering wrong-tool, wrong-argument, and no-tool
   decisions.

Result: the local Wiki evidence packet was promoted. The subsequent unique-
target Wiki rename sequence found strong authorized-operation headroom, but the
combined authorization guard and decorated-label resolver caused two
unauthorized writes on eight fresh non-actions and failed its safety and
resource gates. That mechanism family is closed. The next Phase 3 step is an
answer-blind tool-semantic outcome baseline before choosing another product
mechanism; a read-before-write mutation is deferred because prior separate
model rounds were too expensive.

The answer-blind tool-semantic baseline is also complete. Full-menu Thaddeus
scored `7/16` versus `3/16` with oracle-pruned tools, with zero oracle wins and
four losses. It completed the required tool path on `8/10` positives and made
no forbidden call on six no-tool controls. This does not authorize another
tool router, relevance classifier, pruning rule, or global argument repair.
Capability-specific postconditions remain gated on a future oracle-proven
cluster.

A July 18 current-master refresh raised unchanged full-menu Thaddeus to `9/16`
after the promoted answer-only evidence projection. An answer-blind
literal-response oracle reached `12/16` twice with three wins and zero losses,
but product candidates v1-v3 all stayed `9/16` with zero activations. No repeat
or disjoint validation was run, all implementation branches were deleted, and
the literal-response mechanism family is closed. Phase 3 now requires a new
observable failure seam rather than another lexical contract candidate.

The next distinct local-read campaign established genuine but unsafe recovery
headroom. On a fresh route-agnostic 12-case suite, one-shot audited recovery
improved strict outcomes from `3/12` to `7/12`, including four verified
positive wins and zero paired losses. Candidate p95 improved from 4,611 ms to
4,287 ms. It nevertheless activated on an exact-missing-file negative and
reduced validity from `12/12` to `11/12`, violating the frozen fail-closed gate.
No exact repeat ran, the product branch was deleted, and the local-read recovery
implementation family is closed. A future attempt requires a materially
different resource-existence signal and a new suite, not another lexical
detector tuned to the consumed case.

The next additive local-file mutation also stopped at development. A V1
allowed-root text-create tool reached every authorized tool-selection case and
preserved every safety outcome, but created `0/5` exact requested artifacts.
A fresh V2 schema replaced free-form path and content arguments with path
components, line elements, and an explicit trailing-newline boolean. It again
passed `5/5` safety outcomes but completed only `1/5` exact authorized files,
below the frozen `4/5` gate. No unchanged control, repeat, or validation was
spent. Both product branches were deleted. Do not reopen schema-only file
creation without a materially different semantic-binding signal; structural
validity and safe execution are not the remaining bottlenecks.

### Phase 4 - Open a separate learning-based capacity lane

Objective: test the most plausible route to a material closed-book gain after
prompt and inference scaffolds failed.

Predeclare a QLoRA, LoRA, or rationale-distillation experiment only after a
stable generalized failure cluster exists. The base artifact remains frozen,
but the adapted weights are a new model variant and must be reported that way.

Required arms:

- raw frozen base;
- unchanged Thaddeus with frozen base;
- adapted model direct;
- adapted model with unchanged Thaddeus.

Use public training data, semantic mutations, and fresh validation. MMLU,
MMLU-Pro, GSM1k, or other validation/confirmation items may not enter training
or prompt optimization. A gain must survive unrelated capability and product
regressions.

Exit gate: repeatable fresh capacity gain over the raw base and unchanged
harness, with the adaptation cost and scope stated explicitly.

Result: the first contamination-audited native-checkpoint QLoRA smoke completed
the training/save/reload path in 89.25 seconds, but held-in exact reproduction
remained `0/4` against a frozen `3/4` gate despite falling loss. The smoke and
adapter are rejected; no real training campaign or holdout consumption is
authorized from it. Evaluator plumbing may remain. Reopening requires a new,
materially different data or rationale-distillation hypothesis.

A later behavior-preserving science adapter established a narrower lesson. It
improved generated OpenBookQA and MMLU-Pro science scores, but regressed the
mixed-capability guardrail. On a frozen format-independent attribution control,
native base and adapter each ranked `8/30` correct option texts with zero paired
wins or losses. The prior generated gain was therefore response-contract
learning, not demonstrated knowledge gain. That adapter and its research
branches are rejected; future learning candidates must predeclare a
format-independent capacity metric alongside generated strict scoring.

The next rationale-distillation sequence produced a useful boundary rather
than a promotable win. With matched 128-record SciQ QLoRA arms and a
format-independent scorer, native base and answer-only remained `8/30` while
full-support rationale supervision reached `10/30` with three wins and one
loss. It missed the frozen `+3/30` gate and reduced option-rotation invariance
to `4/6`, so it remains mixed, unmerged research. A separately predeclared
answer-blind concise-evidence v2 compressed 55/64 supports by a median 74.65%
and restored `5/6` invariance, but fell to `9/30`. V2 was rejected and deleted.
That third consecutive valid rejection pauses further MMLU candidate launches.
Recalibration must distinguish data scale/coverage, model ceiling, and
development-slice suitability before another learning mechanism is coded.
The resulting
[learning-capacity recalibration](research/LEARNING_CAPACITY_RECALIBRATION.md)
keeps the consumed 30-item slice historical, requires evaluator-only ceiling
and timing preparation, and permits at most one fresh scale-only full-support
candidate before this rationale family is reconsidered.

That prerequisite has now been exercised. Gold SciQ support improved the
unchanged 1.2B model from `13/30` to `17/30` with four paired wins and zero
losses, but missed the frozen `18/30`, `+6/30`, and eight-win utilization gate.
The scale-only candidate stopped before teacher scoring or adapter training;
fresh MMLU-Pro development and validation remain untouched. Rationale training
is therefore no longer the next candidate. Reopening it requires a materially
different evidence-utilization hypothesis, not a relaxed gate.

### Phase 5 - Reduce latency without removing quality controls globally

Objective: improve conversational feel after measuring which helper stages
actually dominate representative turns.

- Measure memory-classifier, Footman, guardrail, completion-validator, repair,
  and search-fallback activation and helper-call rates.
- Prefer deterministic classification for high-confidence cases and fail open
  elsewhere.
- Replace a helper LLM only when a deterministic or external check preserves
  quality on the affected cohort.
- Treat native provider streaming as a separate product experiment because the
  current validator may replace a completed draft.
- Preserve personality, memory, continuity, safety, permissions, and response
  contracts.

Do not trade a demonstrated benchmark or product-quality gain for an arbitrary
latency threshold. Report the quality/latency frontier.

Result: the repeated 30-turn helper cohort did not justify a conversational
router refactor. Footman used zero LLM calls, optional helpers were 10% of
ordinary-conversation end-to-end time, and prompt construction was negligible.
The cohort instead exposed a repeated reliability defect: the runtime budgeted
for 16,384 tokens while LM Studio was loaded at 8,192, causing an explicit
memory turn to overflow. A conservative memory-tool route removed the overflow
and produced one paired win, but remained inconclusive on fresh paraphrases;
giving memory precedence then caused four false activations on ten fresh
actions and was rejected.

A current-master 16K eligibility check then ran the identical three-turn
product cohort twice before any native lifecycle code was rebased. Both runs
completed `3/3`, passed `2/3` public contracts, used 13 provider calls, and
avoided overflow, but both raised the same irrelevant `memory_store_facts`
permission prompt during read-only recall. Provider state was restored exactly.
Native provider-context v2 is therefore rejected before implementation: loading
the larger context reliably would reproduce the known permission regression.

The current-baseline conservative memory-tool revalidation also stopped at
development. It activated on `3/4` fresh high-confidence recalls and `0/6`
fresh negatives, removed two irrelevant permission prompts, tied public
outcomes at `8/10`, and reduced observed p95. It nevertheless increased
provider calls from 45 to 50, entirely on intended recalls. The frozen
non-increasing call gate rejected it before repeat; the product branch was
deleted and validation remains untouched.

Two deterministic missing-attachment clarifiers also showed a genuine speed
signal, reducing repeated positive p95 end-to-end latency from roughly 2.5
seconds to 0.9 seconds with zero model calls. Both were rejected on fresh
semantic mutations. Lexical routing could not reliably distinguish the user's
requested action, supplied content, location schemes, and artifact variants.
That mechanism family is closed. Revisit it only with structured attachment
state or an explicit user action, not a larger phrase catalog.

### Phase 6 - Revisit specialist or multi-model routing only with data

Objective: decide whether explicit escalation is worth its complexity and VRAM.

Do not prototype MoE-style routing until at least 300 labeled outcomes show a
repeatable complementary failure region between candidate models or
capabilities. Train or tune routing on development data and validate it on a
fresh distribution. Report stronger-model use as escalation, never as local
model success.

## Prioritized candidate basket

| Priority | Candidate | Primary scorecard | Why now |
| ---: | --- | --- | --- |
| Validated locally; PR pending | Explicit typed JSON output templates | Harness capability and product quality | A scorer-blind oracle improved four official MCP-Universe finance tasks from `2/4` to `4/4` with two wins and zero losses. The generic product candidate then improved development `2/3 -> 3/3`, repeated exactly with the same win and zero losses, and activated on a disjoint task without a paired loss (`0` wins, `0` losses, `3` ties); the activated validation task retained a separate semantic failure, so validation is evidence of mechanism transfer rather than a task-score win. Model/tool-call counts were unchanged, no-template outputs were exact ties, and 2,959 repository tests passed with one expected skip. |
| Validated; PR `#307` | Typed selected-target Wiki operations and verified read receipt | Harness capability and product quality | The complete chain passed development and exact repeat at `12/12`, then improved raw and unchanged `4/18` to `17/18` on disjoint validation with thirteen wins, zero losses, all reads and controls, five of six writes, exact activation, and lower calls, tokens, p50, p95, and VRAM. The one miss was bounded model composition after correct target binding. Broad local product gates are green; protected checks and post-merge verification remain. |
| Completed | Current local date/time utility v1 | Harness capability and product quality | Exact product repeat added two correct local-time outcomes, retained six correct date outcomes with zero provider calls, repaired one location-scoped wrong-clock route, reduced calls `22 -> 8`, and improved repeat positive first-visible p50 `211.5 ms -> 5.5 ms` with no observed behavior loss. Final confirmation retained direction; one selector-label disagreement is disclosed rather than silently rescored. Promoted through product PR `#271`. |
| Completed | Exact-identity completion-repair termination | Product quality | Across paired development, exact repeat, and disjoint semantic validation, public outcomes and changed-repair adoption/rejection were unchanged while aggregate provider calls fell `124 -> 119`. Three savings were directly case-matched. Promoted through product PR `#250`; small-sample latency percentiles remain descriptive. |
| Completed | Generalized Wiki-root temporal-deferral pruning | Harness capability and product safety | Fresh development and exact repeat each improved `4/16` to `11/16` with seven wins and zero losses. Disjoint validation improved `4/18` to `13/18` with nine wins, zero losses, `10/10` deferred safety, `8/8` immediate tool reachability, full validity, and fewer calls. Promoted through product PR `#248`. |
| Completed | Application-owned Wiki-root default-location schema projection v3 | Harness capability | The original v2 was rejected on one deferred activation. After the deferral and non-action guards shipped independently, a fresh current-master recovery repeated `8/12 -> 10/12` with the same two wins and zero losses, then validated at `10/18 -> 14/18` with four wins, zero losses, `8/8` default writes, `6/6` deferred/non-action safety, full validity, correct activation, and fewer calls. Promoted through protected product PR `#261`; this is argument-ownership reliability, not MMLU. |
| Completed | Deterministic Wiki-root non-action tool pruning | Harness capability and product safety | Fresh v2 development and exact repeat each improved `7/12` to `9/12`; disjoint validation improved `10/16` to `13/16`, with zero paired losses, full validity, correct activation, fewer model calls, and all non-action controls passing. Promoted through product PR `#246`. |
| Completed | Current-master postcondition and local-read recovery refresh | Harness capability diagnostic | Audited recovery produced four verified fresh gains but failed the exact-missing-resource boundary at 11/12 validity; the implementation was removed |
| Completed | Source-diverse gold-headroom screen | Harness capability diagnostic | Compact support moved the frozen 1.2B model from `2/12` to `9/12` with seven wins and zero losses, but only extraction and arithmetic-result use cleared `2/2`; the frozen three-category breadth gate rejected a new product candidate. |
| Completed | Fresh capability-closure scorecard | Harness capability diagnostic | Raw scored `4/20`, unchanged Thaddeus `15/20`, and compact gold support `19/20`. The harness captured 13/15 gold-supported positive wins, but `19/20` validity and `2/4` exact no-tool controls failed the frozen reliability gate; no category met the three-outcome residual-gap rule. |
| Completed | Matched existing-capability ablation | Harness capability diagnostic | Holding the production pipeline constant, capabilities-on improved `4/20` to `10/20` with seven wins and one loss. Compute reached `4/4`, but the eight-win rule missed and a deferred request created state, failing the hard no-action gate. No mechanism was authorized. |
| Stopped at prerequisite | QLoRA or rationale-distillation pilot | Adapted-model capacity | Full-support rationale supervision produced a mixed `10/30` versus `8/30`, concise-evidence v2 fell to `9/30`, and the fresh gold-support prerequisite reached only `17/30` versus the frozen `18/30` and `+6/30` gates. No scale adapter was trained. Pursue reliable evidence utilization and externally verified outcomes before reopening learning. |
| Closed | Distinct corrective local retrieval with abstention | Harness capability | Fresh route-agnostic validation found four wins but one forbidden exact-missing-file activation; three implementations are rejected and the family is closed pending a materially different existence signal |
| Closed | Native provider-context contract v2 | Product quality | Current master at a verified 16K context reproduced the irrelevant memory-write permission prompt twice, so the large lifecycle candidate was not rebased |
| Closed | Conservative explicit-memory tool budget revalidation | Harness capability and reliability | Fresh development repeated good precision (`3/4` positives, `0/6` negatives) and removed two permission prompts, but increased provider calls from 45 to 50; no repeat ran |
| Completed | Prompt-envelope and memory capability-surface attribution | Harness capability diagnostic | Two exact 8K full-surface repeats measured a 12,097-token request budget dominated by 8,455 estimated tool-definition tokens. A one-read-tool oracle repeated at 3,890 tokens with 4,302 headroom and passed the contract. This proves causal headroom but does not reopen the rejected selector family. |
| Completed | Expand the representative verified-outcome scorecard | Evaluation infrastructure | The fresh 20-task expansion confirmed large fixed-model harness value (`4/20` raw to `15/20` unchanged) but found no repeated residual category large enough to authorize another mechanism. Preserve the consumed evidence and select the next experiment only from a fresh causal seam. |
| Completed | Fresh outcome discovery v2 | Harness capability diagnostic | A balanced 16-task triage scored `7/16` strict and `11/16` valid. Local-file extraction missed `0/2`, but its preauthored reserve passed `1/1` with `file_read`, leaving no qualifying three-case cluster and authorizing no oracle or product candidate. The staged 17-case screen used about 90 seconds of hot model time. |
| Completed | Fresh outcome discovery v3 | Harness capability diagnostic | Two balanced 16-case invocations scored `19/32` strict and `31/32` valid. Computation reached `4/4`; local files, Wiki creation, and no-action safety each reached `3/4`. Instruction contracts and multi-source synthesis each failed `3/4`, but both map to already-closed mechanism families, so no oracle or product candidate was authorized. |
| Closed | Scoped additive text-file creation v1-v2 | Harness capability | Both revisions preserved `5/5` safety outcomes. V1 produced `0/5` exact authorized artifacts; V2's typed path/line/newline schema produced `1/5`, below its frozen `4/5` gate. Schema shape alone did not solve semantic path and content binding, so both product branches were deleted and no repeat or holdout ran. |
| Closed at baseline gate | XLSX column-fidelity headroom v1 | Harness capability diagnostic | Static inspection found that the reader can collapse physically absent cells, but a fresh 10-case run produced zero successful `file_read` results and `0/10` strict. The intended representation layer never activated, so the conditional gold arm and product candidate did not run. |
| Closed at baseline gate | System-command outcome discovery v1 | Harness capability diagnostic | Unchanged Thaddeus scored `6/10` strict and `10/10` valid. Safety/no-action controls passed `4/4`; `system_execute` was selected on `5/6` authorized cases and all selected calls succeeded. Only one miss was tool-name selection, while the others were argument binding or final-response fidelity, so a forced-tool candidate was not authorized. |
| Closed at prerequisite | System-command binding oracle v1 | Harness capability diagnostic | A fresh 30-evaluation screen found only `2/6` unchanged positive tool selection versus the frozen `5/6` binding-dominance gate. Tool-name-guided and gold-command arms each selected `6/6`, but all three arms scored `0/6` strict positives while every control passed. Command binding is closed; do not bundle selection, argument rewriting, and response projection. |
| Closed at development | Approved-plan capability-menu oracle v1 | Harness capability diagnostic | A broad plan-derived local menu produced two verified Wiki-state wins and zero losses (`3/10 -> 5/10`) with all no-action states intact, but missed the frozen three-win floor. Calls rose `12 -> 38` and input tokens `10,367 -> 179,743`; both harness arms also missed casual parity. Close this union. A future attempt requires a richer user-visible typed source/outcome plan contract and fresh tasks. |
| Closed at development | Typed approved-plan oracle v1 | Harness capability diagnostic | Ten fresh tasks compared raw, unchanged, typed-metadata-only, and typed-family-menu arms. All tied at `4/10` with `0/6` positives, all controls, and full validity. The candidate produced zero wins while calls rose `13 -> 21` and input tokens `11,019 -> 31,808`, failing both hard ceilings. Do not add the fields for routing; a new plan-based attempt requires verified execution progression, not narrower labels or menus. |
| Closed at development | Structured attachment evidence packet v1 | Harness capability diagnostic | A bounded explicit-attachment packet improved unchanged `4/10 -> 7/10` through three extraction wins with zero losses, all controls, and full validity. It nevertheless produced `0/3` verified state-changing outcomes with zero model-visible tools and zero tool calls, while calls rose `14 -> 17` and failed the frozen non-increase gate. Keep the extraction signal as evidence only; the next fresh experiment must test verified execution progression rather than another evidence label or menu. |
| Closed at development | Verified source receipt continuation v1 | Harness capability diagnostic | A bounded runtime-style receipt tied unchanged at `3/10`, produced zero paired wins and `0/6` verified state changes, preserved all three no-action boundaries, and exposed zero tools on every positive. Calls rose only `13 -> 14` and resource ceilings passed, but both harness arms also missed casual exact-response parity. Close prompt-level receipts, packets, labels, and menus for this seam; only a real in-loop continuation or the separately governed trajectory-training lane remains materially distinct. |
| Closed at prerequisite | Approved-plan active tool-loop continuation v1 | Harness capability diagnostic | The remaining late-loop seam lacked causal headroom: unchanged produced successful source calls on `3/6` positives versus the frozen `4/6` minimum, and only `1/6` stalled after source success without a verified mutation versus the required `3/6`. Controls were `3/3`, casual parity `1/1`, and validity `10/10`. No product code or candidate arm ran; target initial tool commitment elsewhere. |
| Closed at prerequisite | Grounded semantic-delta oracle v1 | Harness capability diagnostic | Fresh unchanged LFM 2.5 1.2B completed all `6/6` authorized typed Wiki updates with exact verified state and successful writes, leaving no positive headroom, while only `2/4` no-action or clarification controls preserved state and three forbidden write selections exposed a separate safety seam. Raw, direct, gold, repeat, validation, and product code did not run. Reopen semantic binding only on a new repeated fresh cluster; research missing-value and target-ambiguity safety separately. |
| Completed Phase A | Verified fixed-catalog trajectory data v1 | Model-adaptation infrastructure | A deterministic current-schema builder produced 512/512 shape-, execution-, and exact-state-verified trajectories with balanced file-to-root, page-create, page-update, and no-tool abstention families; disjoint 384/64/64 splits; and byte-identical rebuilds. No model or training calls ran. This proves the pipeline only. Phase B must separately freeze broader 3k-5k verified diversity and regression gates before an adapter pilot. |
| Completed Phase B | Verified fixed-catalog trajectory data v1 | Model-adaptation infrastructure | The frozen expansion produced 4,096/4,096 schema-, execution-, flow-, and exact-state-verified rows across sixteen balanced families and disjoint 3,072/512/512 splits. Every family reaches 32 structural prompt signatures and nine tool-order variants per split; two builds matched, Phase A remained frozen, and 585 evaluator tests passed with one skip. No model or training calls ran. A separately acknowledged QLoRA/SFT pilot is now the next learning gate; this row is not a model-gain claim. |
| Closed at development gate | Deterministic date arithmetic v1 | Harness capability | Compact gold evidence moved `1/8` raw and `0/8` unchanged to `8/8`, but the real read-only tool candidate reached only `2/8` positives with `4/4` controls. Both selected positive calls succeeded; selection coverage was only `2/8`, below the `7/8` gate. Product code was deleted and evaluator PR `#135` preserves the evidence. |
| Stopped at prerequisite | Deterministic date first-tool selection v2 | Harness capability diagnostic | On a fresh disjoint slice, raw and unchanged scored `0/8`; tool-only scored `3/8` and already selected on `6/8`. Only two misses were omissions while three were wrong operation/argument bindings, so selection could not meet the frozen +3 causal gate. No selector code ran; evaluator PR `#136`. |
| Rejected at prerequisite | Typed date-argument oracle v3 | Harness capability diagnostic | On a third disjoint slice, tool-only scored `1/6` positives and selected on `4/6`, below the frozen `5/6` binding-dominance gate. Three selected calls had wrong arguments, but two recurrence requests omitted the tool. Gold and product code did not run; the date family is closed. Evaluator PR `#137`. |
| Deferred | Structured constrained decoding | Harness capability | The clean LFM 1.2B diagnostic tied at `10/10` valid and `9/10` semantically exact, so no current product headroom exists |
| Deferred | Verifier-guided candidate selection | Harness capability | Requires a sound external verifier and a repeated domain-specific failure cluster; general voting already lost |
| Deferred | Calibrated abstention | Product quality | Requires a frozen labeled outcome set and risk/coverage reporting |
| Deferred | Multi-model/MoE routing | Escalation | Does not improve a fixed model and currently lacks labeled complementarity data |

The reconciled method audit, complete experiment contracts, and current
blockers are maintained in
[research/INFERENCE_METHOD_GAP_MAP.md](research/INFERENCE_METHOD_GAP_MAP.md).

## Definition of success

The program succeeds cumulatively, not through one blended headline:

- frozen-model capacity never silently regresses and any claimed uplift passes
  exact repeat plus fresh validation;
- harness capability produces independently verified net outcome gains with
  bounded resources and no permission regression;
- product quality preserves personality, continuity, safety, memory, and
  false-success controls while improving latency or usability;
- rejected behavior is absent from production, while its evidence remains easy
  to find in [research/EXPERIMENT_CATALOG.md](research/EXPERIMENT_CATALOG.md).

The eventual everyday-task claim remains separate from MMLU: it should be based
on frozen prevalence weights, independently verifiable outcomes, a temporal
holdout, a low false-success rate, and a confidence bound rather than a single
development percentage.

## Research basis

The plan is consistent with evidence that external computation and feedback can
improve useful outcomes, while material small-model capacity gains often
require training rather than additional prompt wrappers:

- [MMLU-Pro](https://arxiv.org/abs/2406.01574)
- [GSM1k](https://arxiv.org/abs/2405.00332)
- [GAIA](https://arxiv.org/abs/2311.12983)
- [Berkeley Function Calling Leaderboard](https://gorilla.cs.berkeley.edu/leaderboard)
- [PAL: Program-aided Language Models](https://arxiv.org/abs/2211.10435)
- [Toolformer](https://arxiv.org/abs/2302.04761)
- [CRITIC](https://arxiv.org/abs/2305.11738)
- [QLoRA](https://arxiv.org/abs/2305.14314)
- [Distilling Step-by-Step](https://arxiv.org/abs/2305.02301)
- [RouteLLM](https://arxiv.org/abs/2406.18665)
