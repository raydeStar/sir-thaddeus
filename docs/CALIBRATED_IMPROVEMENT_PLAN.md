# Calibrated Improvement Plan

**Status:** active research plan

**Calibrated:** July 16, 2026
**Production baseline at calibration:** `8fc24ea`

## Decision

Sir Thaddeus has the right foundation. The next phase is not a broad
orchestration rewrite. It is a sequence of small, falsifiable optimizations at
existing seams, promoted only when they improve a declared scorecard without
damaging the others.

The project will no longer treat a newer or larger comparison model as an
improvement to the harness. Model comparisons remain useful as diagnostic
ceilings, cross-model transfer checks, or explicit escalation studies.

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
6. Reject cheaply on a development slice of roughly ten minutes or less.
7. Require the exact frozen candidate to repeat before using a disjoint
   validation set.
8. Use oracle-route, oracle-tool, gold-evidence, and gold-state controls to
   locate the ceiling before adding routing machinery.
9. Preserve benchmark integrity: no expected answers, suite identifiers,
   scorer logic, benchmark-specific branches, or hidden strong-model calls in
   production.
10. Record promoted, rejected, inconclusive, and infrastructure results in
    [research/](research/README.md).

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

### Phase 2 - Test local evidence compilation

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
2. one unique-target Wiki mutation not already solved;
3. one read-before-write file or state operation;
4. one tool-semantic slice covering wrong-tool, wrong-argument, and no-tool
   decisions.

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
| 1 | Local document/Wiki evidence compilation | Harness capability | Gold evidence showed headroom; avoids unreliable public search; fits existing Wiki and retrieval seams |
| 2 | Tool-semantic outcome baseline | Harness capability | Syntax is already valid; the unresolved risks are tool choice, arguments, no-tool decisions, and state |
| 3 | One capability-specific external postcondition | Harness capability | Independent verification is stronger than same-model critique, but only after oracle headroom |
| 4 | QLoRA or rationale-distillation pilot | Frozen-model capacity | Prompt-only capacity candidates failed; learning is a materially different mechanism |
| 5 | Helper-call activation and latency cohort | Product quality | Needed before changing Footman, memory gating, validation, or streaming |
| Deferred | Multi-model/MoE routing | Escalation | Does not improve a fixed model and currently lacks labeled complementarity data |

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
