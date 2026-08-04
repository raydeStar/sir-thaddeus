# Experimentation

This document defines how Sir Thaddeus benchmark, prompt, routing, model, and
capability experiments are proposed, run, rejected, and promoted. Its purpose is
to make improvement cumulative without leaving failed machinery in production.

## Research questions

Track three scorecards separately.

### Model-capacity scorecard

Use MMLU-Pro and a balanced closed-book battery to measure knowledge, practical
math, science, document reasoning, instruction following, validity, and
raw-language preservation. Changes such as prompt selection, context
compaction, adapters, or specialist-model routing belong in this lane. See
[BENCHMARKING.md](BENCHMARKING.md) for the current portfolio and customization
rules.

### Capability-harness scorecard

Use outcome-scored tasks to measure whether tools, retrieval, state, memory,
permissions, and external verification let the same model complete more work.
Tool syntax or response format alone is not task completion.

### Product-quality scorecard

Use time to first token, p50/p95 end-to-end latency, model and tool calls,
resource use, safety, permissions, personality, continuity, validity, and false
success to measure what the user experiences around the task outcome.

Declare which scorecard is primary and make the other two guardrails. A
capability improvement may be valuable without raising MMLU, but it must not
silently damage model-capacity or product-quality controls.

Model experiments use the fixed evaluation panel and promotion sequence in
[MODEL_TIER_CALIBRATION.md](MODEL_TIER_CALIBRATION.md). Panel roles select where
to discover and transfer-test a mechanism; they are not production feature
rules and do not replace same-model controls.

## Core loop

```text
frozen baseline
      |
one predeclared candidate
      |
deterministic checks + balanced reject-only triage
      |-- clearly loses or does not activate --> reject without a hot campaign
      |
paired development run (about 10 minutes or less)
      |-- clearly loses --> reject, preserve evidence, delete code
      |
      `-- wins or reaches a credible repeat gate
              |
         exact candidate repeat
              |-- clearly fails --> reject, preserve evidence, delete code
              |-- repeatable but below promotion --> retain unmerged with a verdict
              |
              `-- passes
                      |
                 disjoint validation + product regression + resource gates
                      |
                 promote through PR or retain/reject with a verdict
```

Small development slices are rejection and iteration tools. They do not
establish statistical truth. Reaching exact repeat is sufficient to retain a
promising research branch for review, but it is not sufficient to merge it.

## Evaluation economics

Optimize for information gained per model minute, not for the number of rows
executed. Ten minutes is the maximum normal hot invocation, not a duration that
every experiment should consume.

Use this evidence ladder:

1. Static analysis, focused unit tests, deterministic stage tests, scorer
   checks, and candidate-activation checks.
2. A balanced ten-item triage slice: two items from each core category when the
   five-category capacity portfolio is relevant. Triage is reject-only.
3. The frozen 50-item development battery only when triage remains credible.
4. The exact candidate repeat only after the development gate passes.
5. Disjoint validation and product regressions only after the repeat succeeds.
6. Confirmation, cross-model transfer, and repeated reliability campaigns only
   after the candidate has earned them.

Record the planned item, arm, provider, repeat, and total case-evaluation counts
before the first model call. Large or multi-model campaigns require an explicit
manifest acknowledgement and should normally be scheduled rather than launched
inside the coding loop.

A frozen control pack may be reused when the suite fingerprint, exact model
artifact and quantization, provider/runtime metadata, prompt/config/tool hashes,
and both repository SHAs match. Before reusing it, run a small unchanged-harness
sentinel and refresh the controls if the sentinel drifts. Control reuse saves
compute; it does not permit comparison across changed baselines.

Exact repetition measures runtime stability. Disjoint validation measures
generalization. A larger item count and paired interval measure whether the
effect is distinguishable from sampling noise. Do not substitute one for the
others, and do not run any of them after an earlier gate has already rejected
the mechanism.

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
- planned case-evaluation count, triage limit, and whether a large campaign is
  explicitly authorized;
- an activation signal proving the candidate mechanism ran on intended items
  and stayed off on negative controls;
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

For fixed-model improvement claims, changing the model is not a candidate
mechanism. A different model may measure a semantic ceiling, transferability,
or an explicit escalation path, but it must remain a separate labeled control.

## Decision rules

Predeclare numeric gates appropriate to the slice. Separate hard gates from
suggested decision signals:

- **Hard gates** protect the claimed outcome and product invariants: strict
  correctness or final state, activation, validity, safety, permissions,
  false-success behavior, and any resource ceiling derived from a real product
  SLO or mechanism-specific risk.
- **Suggested signals** describe desirable efficiency direction: fewer calls,
  fewer tokens, lower p50/p95, or a default percentage improvement. Missing a
  suggested signal does not reject a material strict-outcome gain when the hard
  non-regression budgets pass.
- Do not turn a suggested signal into a hard gate after seeing a run. Conversely,
  do not relabel a completed hard gate as suggested to promote its candidate;
  predeclare a fresh revision on unconsumed inputs.

As defaults:

- require a meaningful net gain over both controls;
- require activation telemetry or trace evidence before interpreting the score;
- do not increase invalid outputs or critical safety failures;
- require an exact repeat before consuming a holdout;
- require a disjoint validation result in the same direction;
- reject a candidate that wins only by changing scoring, prompts for one known
  fixture, task weights, exclusions, or hidden strong-model use;
- reject resource regressions that exceed a predeclared product or
  mechanism-specific hard ceiling;
- treat small-slice p95 and percentage token targets as advisory unless the
  sample plan can support a tail estimate and the threshold maps to a product
  SLO;
- stop a mechanism family when oracle controls show that model semantics, not
  routing or execution, are the limiting factor.

Do not average away paired losses. Report wins, losses, unchanged cases,
category results, and repeat stability.

For a harness-capability candidate, strict verified outcomes are normally the
primary metric. Raw and same-prompt direct arms without the required capability
are diagnostic attribution controls, not promotion competitors. Calls, tokens,
and end-to-end latency remain guardrails; they need not improve by an arbitrary
percentage when correctness improves materially and the declared non-regression
budgets pass.

## Experiment branches

- Branch from the current `master` production baseline.
- Use one mechanism per `codex/experiment-<mechanism>` branch.
- Keep evaluation-only changes out of production packages when possible.
- Do not stack unrelated candidates.
- Delete clearly failed code and its temporary branch after recording the
  verdict. A candidate that reaches exact repeat or validation may remain as a
  labeled, unmerged research branch until the campaign is closed.
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
- the stabilized 1.2B same-model MMLU control put raw, same-prompt direct,
  current harness, and the historical product SHA at `10/20`; the saved
  `13/20` harness repeats did not reproduce under the frozen runtime and remain
  consumed historical observations rather than a current harness uplift;
- three isolated MMLU candidates failed their gates and that campaign is paused
  pending a materially different mechanism;
- sampled self-consistency and majority voting did not establish an uplift;
- global retry and completion-validation removal reduced quality;
- raw-versus-harness comparisons with different system prompts do not isolate
  routing effects;
- generalized response-contract, search-retry, memory-evidence, and error-
  sanitization fixes were retained;
- tool-syntax probes showed no meaningful parser headroom on the tested models;
- gold-evidence controls showed retrieval headroom, while broad live-search
  augmentation remained unreliable or failed to improve synthesis;
- explicit Wiki-root selection produced a repeated narrow validation gain and
  was promoted without being described as a reasoning gain;
- a recent tool-integration candidate was rejected after traces showed the
  intended mechanism did not activate, establishing activation evidence as a
  prerequisite to score interpretation;
- failed experiments were removed rather than stored behind dormant flags.

These conclusions can be revisited only with a materially different mechanism,
predeclared controls, and fresh evidence.

## Recommended experiment order

1. Make evaluator composition, candidate activation, and final-state scoring
   explicit enough that a score can be attributed to one mechanism.
2. Test local Wiki or document evidence compilation on tasks where the needed
   source is already available and the oracle-evidence arm proves headroom.
3. Establish an outcome-scored tool-semantics baseline that measures argument
   choice, execution, and final state rather than JSON syntax alone.
4. Add one capability-specific external postcondition and one failure-triggered
   repair only after an oracle control shows that repair can help.
5. Explore an adapter or fine-tuning lane only for a stable, generalized
   model-capacity failure cluster with disjoint validation.
6. Measure helper-stage activation, time to first token, and duplicated model
   calls before changing the production path for latency.
7. Consider sparse specialist-model routing only after at least 300 labeled
   outcomes reveal reproducible complementary failures.

Do not reintroduce blind voting, always-on retrieval, universal planning, or
learned routing without evidence that addresses their known failure modes.

See the [calibrated improvement plan](CALIBRATED_IMPROVEMENT_PLAN.md) for phased
execution and the [research findings](research/README.md) for the living evidence
ledger behind this order.
