# Sir Thaddeus Agent Guide

## Scope

Apply this guide to the entire repository. Treat more-specific `AGENTS.md`
files, when present, as additional instructions for their directory only.

## Product objective

Sir Thaddeus is a local-first assistant intended to show that a small model can
complete a large share of ordinary work when deterministic capabilities,
memory, permissions, tools, and observable verification are used well.

Keep two claims separate:

- **Model capacity:** closed-book knowledge and reasoning, measured with MMLU
  and related controls.
- **Harness capability:** successful user outcomes created by tools, retrieval,
  state, permissions, and external verification.

Do not substitute one score for the other. State the primary metric before an
experiment begins. Preserve raw-model language quality while improving product
capability.

Track **product quality** as a third, independent scorecard: latency, safety,
personality, continuity, validity, permissions, false success, and resource use.
Changing to a larger or newer model may be a useful deployment comparison, but
it is not evidence that the harness improved the fixed model under test.

## Required orientation

Before changing assistant behavior, read:

1. `docs/ASSISTANT_PIPELINE.md` for the supported production path and retired
   experiments.
2. `docs/EXPERIMENTATION.md` for benchmark and promotion policy.
3. `docs/TESTING.md` for the narrowest trustworthy verification command.

For optimization work, also read `docs/CALIBRATED_IMPROVEMENT_PLAN.md` and the
current evidence in `docs/research/` before selecting a candidate.

Use `THADDEUS_ROUTING_LATENCY_SCOPE.md` as historical evidence, not as the
current architecture contract. Verify drift-prone claims against current code,
GitHub state, and fresh artifacts.

When available, use the `sir-thaddeus-experiment-loop` skill for MMLU, harness,
prompt, routing, model-comparison, latency, VRAM, or benchmark work.

## Production invariants

- Preserve safety boundaries, personality, memory, dialogue continuity,
  permissions, tool policy, auditing, sanitization, and explicit response
  contracts unless the task explicitly changes one of them.
- Keep desktop and headless behavior aligned.
- Keep benchmark datasets, expected answers, suite identifiers, scorer logic,
  and promotion thresholds outside production assemblies.
- Prefer a generalized fix at the narrowest product seam over a broad rewrite.
- Do not retain rejected behavior behind a disabled flag. Remove it.
- Treat external tools and model providers as untrusted boundaries.

## Experiment contract

- Change one mechanism per experimental branch.
- Predeclare the hypothesis, controls, primary metric, guardrails, time budget,
  promotion threshold, and rollback before running the candidate.
- Compare the candidate with both the raw-model control and the unchanged
  Thaddeus control under the same model and sampling configuration.
- Use a development slice of roughly ten minutes or less for rejection and
  iteration. A small development win is not promotion evidence.
- Reject a clearly losing candidate immediately. If it wins or reaches the
  exact-repeat gate with a credible directional improvement, retain it long
  enough to rerun the exact candidate.
- Use a disjoint frozen validation set only after the exact repeat succeeds.
- Run broad product regressions only after the candidate survives the focused
  gates.
- Record correctness, validity, paired wins/losses, model calls, tokens,
  latency, peak memory/VRAM, and escalation or tool-use rates when applicable.
- Delete clearly non-working implementation branches, but preserve their
  manifests, raw artifacts, and verdicts in the experiment record. A candidate
  that reaches exact repeat or validation may remain as a labeled, unmerged
  research branch until the campaign decision; do not keep it as dormant
  production code or imply that retention is promotion.
- Treat every material mutation after a failed run as a new candidate. Do not
  tune repeatedly against a holdout until one run happens to pass.

## Benchmark integrity

- Never add task IDs, fixture names, expected-answer fragments, subject-specific
  answer keys, scorer imports, or benchmark-aware conditionals to runtime code.
- Do not read hidden expected outputs or scoring predicates while optimizing
  product behavior. Inspect raw model outputs, traces, tool results, and public
  task inputs instead.
- Do not weaken thresholds, rewrite expected answers, or make scoring more
  lenient to promote a candidate.
- Use semantic mutations, renamed tools/arguments, changed numbers/entities,
  paraphrases, irrelevant-tool distractors, and temporal holdouts to test
  generalization.
- Keep stronger-model calls visible. Report them as escalation, not local-model
  success.
- Score final state and independently verifiable outcomes when possible. Treat
  formatting, valid JSON, plausible plans, and LLM-judge approval as enabling
  signals rather than proof of completion.

## Known evidence and stop rules

- Sampled self-consistency and tool-aware majority voting added latency and did
  not beat unchanged controls. Do not reintroduce them without materially new
  evidence and an independent selector.
- Global retry removal and global completion-validation removal reduced quality.
  Keep the supported path unless a route-specific controlled experiment proves
  a better replacement.
- The historical raw-versus-harness MMLU gap used different system prompts. Do
  not attribute that gap to routing without a same-prompt control.
- Always-on retrieval, blind regeneration, recursive autonomous planning, and
  same-model self-critique are not default strategies.
- Use oracle-route, oracle-tool, and gold-evidence controls to identify the model
  ceiling. Stop adding routing machinery when the model still fails with the
  correct capability and evidence supplied.

## Branch and delivery policy

- Keep only `master` and `dev` as permanent product branches. A promising
  experiment that reaches exact repeat or validation may remain temporarily as
  a clearly labeled research branch while its verdict is reviewed.
- Create short-lived branches from the current production baseline. Use
  `codex/experiment-<mechanism>` for behavioral experiments and `codex/<change>`
  for normal product work.
- `master` is protected. Deliver changes through a PR and wait for the actual
  remote checks.
- Keep `dev` synchronized with `master` between experiments.
- Delete the temporary branch after merge or a clear rejection. Retained,
  below-promotion candidates must stay unmerged and carry a current verdict.
- Preserve unrelated user work and do not rewrite or discard a dirty worktree.

## Verification ladder

Use the cheapest layer that can falsify the current hypothesis, then expand:

1. Static checks and focused unit tests.
2. One deterministic stage test or one harness case.
3. The containing suite.
4. Exact benchmark repeat.
5. Disjoint validation and product regression suites.
6. `./dev/test.ps1` and the relevant conversation-level smoke coverage.
7. Protected PR checks, package smoke tests, and the post-merge `master` run.

Read the exact GitHub Actions log before changing code for CI failures. On this
Windows environment, prefer the repository scripts and isolated project tests
over ad hoc whole-solution commands when the test host is unstable.

## Reporting

Lead with the verdict: promoted, rejected, inconclusive, or blocked. Include the
baseline SHA, model/configuration, exact commands, artifact locations, paired
results, resource costs, regressions, and remaining uncertainty. Be explicit
when a result is historical, estimated, or not independently verified.
