# Experimentation

This document defines how Sir Thaddeus benchmark, prompt, routing, model, and
capability experiments are proposed, run, rejected, and promoted. Its purpose is
to make improvement cumulative without leaving failed machinery in production.

## Research questions

Track two scorecards separately.

### Model-capacity scorecard

Use MMLU-mini and other closed-book reasoning controls to measure knowledge,
reasoning, validity, and raw-language preservation. Changes such as prompt
selection, context compaction, adapters, or specialist-model routing belong in
this lane.

### Capability-harness scorecard

Use outcome-scored tasks to measure whether tools, retrieval, state, memory,
permissions, and external verification let the same model complete more work.
Tool syntax or response format alone is not task completion.

Declare which scorecard is primary. A capability improvement may be valuable
without raising MMLU, but it must not silently damage model-capacity controls.

## Core loop

```text
frozen baseline
      |
one predeclared candidate
      |
paired development run (about 10 minutes or less)
      |
  loses ---------> reject, preserve evidence, delete code
      |
    wins
      |
exact candidate repeat
      |
  fails ---------> reject, preserve evidence, delete code
      |
   passes
      |
disjoint validation + product regression + resource gates
      |
promote through PR or reject
```

Small development slices are rejection and iteration tools. They do not
establish statistical truth.

## Predeclaration

Before changing behavior, record:

- experiment ID and mechanism;
- hypothesis and non-goals;
- baseline commit;
- model, provider, quantization, context, sampling, and prompt/config hashes;
- development slice and its fingerprint;
- raw and unchanged-harness controls;
- primary and guardrail metrics;
- maximum run time and model-call budget;
- promotion, rejection, and rollback rules;
- holdout status and whether it has ever been inspected.

Use the manifest template bundled with the `sir-thaddeus-experiment-loop`
skill. Store raw outputs and traces outside production assemblies. Treat any
post-run change to the mechanism or threshold as a new candidate revision.

## Controls

Use paired controls with the same model and inference configuration:

1. **Raw minimal:** the model with the smallest valid evaluator prompt.
2. **Same-prompt direct:** the production system/personality prompt with one
   direct model path and no unrelated orchestration.
3. **Unchanged harness:** the production-equivalent Sir Thaddeus path.
4. **Candidate:** only the proposed mechanism differs.

The same-prompt direct control is required before attributing a raw/harness gap
to routing or orchestration. When useful, add oracle-route, oracle-tool,
gold-evidence, or stronger-model upper bounds, labeled separately.

## Decision rules

Predeclare numeric gates appropriate to the slice. As a default:

- require a meaningful net gain over both controls;
- do not increase invalid outputs or critical safety failures;
- require an exact repeat before consuming a holdout;
- require a disjoint validation result in the same direction;
- reject a candidate that wins only by changing scoring, prompts for one known
  fixture, task weights, exclusions, or hidden strong-model use;
- reject resource regressions that exceed the declared latency or VRAM budget;
- stop a mechanism family when oracle controls show that model semantics, not
  routing or execution, are the limiting factor.

Do not average away paired losses. Report wins, losses, unchanged cases,
category results, and repeat stability.

## Experiment branches

- Branch from the current `master` production baseline.
- Use one mechanism per `codex/experiment-<mechanism>` branch.
- Keep evaluation-only changes out of production packages when possible.
- Do not stack unrelated candidates.
- Delete failed code and the temporary branch after recording the verdict.
- Merge only after exact repeat, disjoint validation, product regression, and
  protected CI succeed.
- Synchronize `dev` with the resulting `master` before the next experiment.

## Integrity boundary

Runtime code may receive user input, public tool schemas, permitted context,
and actual tool results. It must never receive expected answers, scorer code,
hidden predicates, suite IDs, promotion thresholds, or evaluator-only metadata.

For product-behavior optimization, do not inspect hidden suite YAML or expected
outputs. If the explicit task is to repair evaluation infrastructure, isolate
that work, explain why a product fix is insufficient, and verify that the
change improves measurement rather than pass rate.

Use mutations and negative controls to challenge generality:

- paraphrase requests;
- rename tools and arguments;
- change numbers, entities, and harmless formatting;
- reorder schemas;
- add irrelevant tools or documents;
- include unavailable, contradictory, permission-sensitive, and impossible
  tasks;
- collect temporal holdouts after prompts and routes are frozen.

## Promotion gate

A production PR must state:

- what failed in the baseline;
- the generalized mechanism being tested;
- development, exact-repeat, and validation results;
- raw, same-prompt, and unchanged-harness comparisons;
- product regressions and safety checks;
- first-token, end-to-end, call-count, token, and resource effects when relevant;
- why the behavior generalizes beyond the observed items;
- rollback instructions.

Do not merge an experiment merely because it is elegant, faster, or plausible.
Merge only demonstrated behavior.

## Evidence already established

Treat `docs/ASSISTANT_PIPELINE.md` as the current production contract and
`THADDEUS_ROUTING_LATENCY_SCOPE.md` as the historical evidence record.

Current conclusions include:

- deterministic tools can create large gains on externally verifiable work;
- sampled self-consistency and majority voting did not establish an uplift;
- global retry and completion-validation removal reduced quality;
- raw-versus-harness comparisons with different system prompts do not isolate
  routing effects;
- generalized response-contract, search-retry, memory-evidence, and error-
  sanitization fixes were retained;
- failed experiments were removed rather than stored behind dormant flags.

These conclusions can be revisited only with a materially different mechanism,
predeclared controls, and fresh evidence.

## Recommended experiment order

1. Direct-language attribution using raw, same-prompt, and full-harness arms.
2. Typed context compaction without loss of continuity or personality.
3. Grammar-constrained tool output where the configured provider supports it.
4. Capability-specific external postconditions and one failure-triggered repair.
5. Read-before-write state inspection for one mutable capability.
6. Selective retrieval with known-answer and unknown-answer controls.
7. Sparse specialist-model routing after enough labeled outcomes exist.
8. Adapters or fine-tuning only for a stable, generalized failure cluster.

Do not reintroduce blind voting, always-on retrieval, universal planning, or
learned routing without evidence that addresses their known failure modes.
