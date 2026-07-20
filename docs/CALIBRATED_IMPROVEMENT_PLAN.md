# Calibrated Improvement Plan

**Status:** active research plan

**Calibrated:** July 18, 2026
**Production baseline at calibration:** `84f96e6`
**Current production baseline:** `b7934c0`

## Decision

Sir Thaddeus has the right foundation. The next phase is not a broad
orchestration rewrite. It is a sequence of small, falsifiable optimizations at
existing seams, promoted only when they improve a declared scorecard without
damaging the others.

The project will no longer treat a newer or larger comparison model as an
improvement to the harness. Model comparisons remain useful as diagnostic
ceilings, cross-model transfer checks, or explicit escalation studies.

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
| Promotion candidate | Generalized Wiki-root temporal-deferral pruning | Harness capability and product safety | Fresh development and exact repeat each improved `4/16` to `11/16` with seven wins and zero losses. Disjoint validation improved `4/18` to `13/18` with nine wins, zero losses, `10/10` deferred safety, `8/8` immediate tool reachability, full validity, and fewer calls. Protected product PR `#248` is the current gate. |
| Retained research | Application-owned Wiki-root default-location schema projection v2 | Harness capability | The candidate reproduced four development wins and added seven disjoint validation wins with zero losses, but one deferred non-action activated the mutation projection. The hard validity gate rejected promotion; retain the unmerged branch without tuning against the consumed holdout. |
| Completed | Deterministic Wiki-root non-action tool pruning | Harness capability and product safety | Fresh v2 development and exact repeat each improved `7/12` to `9/12`; disjoint validation improved `10/16` to `13/16`, with zero paired losses, full validity, correct activation, fewer model calls, and all non-action controls passing. Promoted through product PR `#246`. |
| Completed | Current-master postcondition and local-read recovery refresh | Harness capability diagnostic | Audited recovery produced four verified fresh gains but failed the exact-missing-resource boundary at 11/12 validity; the implementation was removed |
| Completed | Source-diverse gold-headroom screen | Harness capability diagnostic | Compact support moved the frozen 1.2B model from `2/12` to `9/12` with seven wins and zero losses, but only extraction and arithmetic-result use cleared `2/2`; the frozen three-category breadth gate rejected a new product candidate. |
| Completed | Fresh capability-closure scorecard | Harness capability diagnostic | Raw scored `4/20`, unchanged Thaddeus `15/20`, and compact gold support `19/20`. The harness captured 13/15 gold-supported positive wins, but `19/20` validity and `2/4` exact no-tool controls failed the frozen reliability gate; no category met the three-outcome residual-gap rule. |
| Completed | Matched existing-capability ablation | Harness capability diagnostic | Holding the production pipeline constant, capabilities-on improved `4/20` to `10/20` with seven wins and one loss. Compute reached `4/4`, but the eight-win rule missed and a deferred request created state, failing the hard no-action gate. No mechanism was authorized. |
| Stopped at prerequisite | QLoRA or rationale-distillation pilot | Adapted-model capacity | Full-support rationale supervision produced a mixed `10/30` versus `8/30`, concise-evidence v2 fell to `9/30`, and the fresh gold-support prerequisite reached only `17/30` versus the frozen `18/30` and `+6/30` gates. No scale adapter was trained. Pursue reliable evidence utilization and externally verified outcomes before reopening learning. |
| Closed | Distinct corrective local retrieval with abstention | Harness capability | Fresh route-agnostic validation found four wins but one forbidden exact-missing-file activation; three implementations are rejected and the family is closed pending a materially different existence signal |
| Closed | Native provider-context contract v2 | Product quality | Current master at a verified 16K context reproduced the irrelevant memory-write permission prompt twice, so the large lifecycle candidate was not rebased |
| Closed | Conservative explicit-memory tool budget revalidation | Harness capability and reliability | Fresh development repeated good precision (`3/4` positives, `0/6` negatives) and removed two permission prompts, but increased provider calls from 45 to 50; no repeat ran |
| Completed | Expand the representative verified-outcome scorecard | Evaluation infrastructure | The fresh 20-task expansion confirmed large fixed-model harness value (`4/20` raw to `15/20` unchanged) but found no repeated residual category large enough to authorize another mechanism. Preserve the consumed evidence and select the next experiment only from a fresh causal seam. |
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
