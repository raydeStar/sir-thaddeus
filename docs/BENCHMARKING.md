# Benchmarking

Sir Thaddeus tracks model capacity, harness capability, and product quality
separately. A tool, retrieval, or state-management gain may improve the user
outcome without making the underlying model more knowledgeable. Reports must
name the primary lane and its guardrails before showing a score.

## Scorecards

| Scorecard | Question | Typical controls | Primary evidence |
| --- | --- | --- | --- |
| Model capacity | Can this model understand and solve the task without answer-producing tools? | Raw minimal, same-prompt direct, unchanged no-tools harness, candidate | Strict closed-book correctness, validity, calibration, and robustness |
| Harness capability | Can the same model complete more real work with Sir Thaddeus capabilities? | Unchanged harness, candidate harness, and oracle capability when diagnostic | Independently verified final state or artifact, permissions, calls, latency, and resources |
| Product quality | Does the change preserve or improve the experience around those outcomes? | Unchanged product, candidate product, focused safety and continuity regressions | Time to first token, p95 latency, personality, continuity, safety, permissions, false success, and resource use |

Do not combine the three into one unlabeled percentage. Report an augmented
outcome as an augmented outcome; a calculator-assisted correct answer is useful,
but it is not a closed-book reasoning win.

## Fixed-model attribution rule

For a harness-improvement experiment, freeze the exact model artifact,
quantization, provider, context, sampling, prompt composer, and item set across
the unchanged and candidate arms. A newer, larger, or different model may be
used as a ceiling, transfer, or explicitly labeled escalation control, but its
gain cannot satisfy the fixed-model promotion gate.

## General capability portfolio

The core portfolio favors objectively scored tasks that produce useful signal
for small local models. Availability in the evaluator varies; adding a row is a
separate evaluator change, not production behavior.

| Capability | Preferred benchmark | Role |
| --- | --- | --- |
| Broad knowledge and reasoning | [MMLU-Pro](https://arxiv.org/abs/2406.01574) | Core breadth measure; original MMLU remains a continuity diagnostic |
| Practical mathematics | [GSM1k](https://arxiv.org/abs/2405.00332) | Fresh GSM-style arithmetic and contamination check |
| General science | [ARC-Challenge](https://arxiv.org/abs/1803.05457) | Accessible science reasoning across the full model ladder |
| Document and numerical reasoning | [DROP](https://arxiv.org/abs/1903.00161) | Reasoning over supplied text, references, counts, and arithmetic |
| Precise instruction following | [IFBench](https://arxiv.org/abs/2507.02833) or IFEval | Verifiable constraint-following and generalization |

Use MATH, MuSR, GPQA Diamond, and the current LiveBench release as deeper or
fresh confirmation lanes. They can be floor-level for the smallest models, so
do not make them the only promotion signal. GAIA Level 1 is a useful separate
assistant-outcome lane when browsing, files, and multimodal tools are in scope.

Coding benchmarks remain available for product work, but they are not part of
the default general-capability headline.

## Run tiers

### Triage

Use a deterministic, balanced ten-item subset of declared development data:

- 2 MMLU-Pro;
- 2 GSM1k;
- 2 ARC-Challenge;
- 2 DROP;
- 2 IFBench or IFEval.

Triage exists to reject weak or inactive mechanisms while coding. It cannot
authorize validation, support a public score, or become a holdout merely
because only ten items were executed.

### Development

Target a sub-ten-minute, fixed 50-item battery:

- 10 MMLU-Pro;
- 10 GSM1k;
- 10 ARC-Challenge;
- 10 DROP;
- 10 IFBench or IFEval.

This slice can reject a mechanism or justify an exact repeat. It cannot prove a
general improvement.

### Validation

Use at least 50 disjoint items per core category. Freeze the suite before the
candidate runs, report every category separately, and use paired confidence
intervals or an equivalent paired test. Do not tune against validation failures.
Size the validation run for the smallest effect worth shipping; 50 per category
is a floor, not proof that a smaller observed difference is measurable.

### External confirmation

Use a current LiveBench release, temporal questions, or another independently
maintained holdout. Run difficult MATH, MuSR, and GPQA lanes only where the raw
model is above the floor.

### Reference conformance

The generated-answer evaluator is the correct instrument for paired Thaddeus
attribution because every arm receives the same user-facing contract. It is not
automatically comparable with public leaderboard scores that use a different
chat template, prompt, answer extraction, or log-likelihood procedure.

For each frozen model intake and after evaluator scoring changes, run a pinned
official or widely used reference implementation on a small raw-model sample.
Compare item-level outcomes and investigate parser or prompt divergence before
publishing capacity results. Report the generated-answer score and reference
score as separate metrics; neither may silently replace the other.

## Required measurements

Every comparison should report:

- strict correctness and valid response rate;
- category macro-average plus each category result;
- paired wins, losses, and unchanged items;
- exact-repeat stability;
- paraphrase, option-order, entity, and number mutations;
- false-confidence or abstention behavior where scoring supports it;
- model and tool calls, tokens, p50/p95 latency, and peak memory or VRAM;
- hidden or stronger-model escalation as a separate line item.

Prefer an improvement whose paired confidence interval excludes zero on
validation. A narrow product mechanism may use a narrow primary category, but
the remaining portfolio becomes its regression guardrail.

## Attribution controls

Keep the model, quantization, context, sampling, prompt composer, provider, and
item set frozen. Compare these arms when applicable:

1. `raw`: minimal evaluator prompt and one model call;
2. `production_prompt_no_tools`: the shared production prompt and one no-tools
   call. Historical artifacts call this `same_prompt_direct`; never silently
   reinterpret those results as tool-enabled;
3. `same_prompt_tools_direct`: the shared production prompt and the same
   allowlisted tools, using only an evaluation-owned model/tool loop with no
   routing, retrieval discipline, retry/repair, synthesis, or verification;
4. `harness_full`: unchanged production-equivalent orchestration;
5. candidate or ablation: exactly one declared mechanism differs.

The equal-tools arm is the primary architecture control. Full-harness versus
raw remains useful product evidence, but it does not isolate orchestration from
tool availability.

The equal-tools runner is also a state boundary, not merely a prompt variant.
It runs one case per process with evaluator-owned file, Wiki, memory, and audit
paths; applies only fabricated case state; verifies the pre-turn snapshot; and
records the post-turn snapshot plus every tool result. A failed preflight is an
infrastructure error and cannot be scored as a model miss. Expected answers
remain in the sibling evaluator and are never included in model messages.

For hosted Luna-style runs, Codex CLI is an explicitly labeled transport rather
than a direct model API. It ignores user config and repository rules, executes
in an empty read-only temporary directory, and fails if the CLI event stream
shows transport-level command, MCP, web-search, or file-tool use. Sir
Thaddeus tool calls remain separate, allowlisted, audited records.

For augmented tasks, show closed-book and tool-enabled results in separate
columns. An oracle route, oracle tool, or gold-evidence arm can identify a model
ceiling, but it is diagnostic rather than a product score.

## Safe customization

Benchmark datasets, expected answers, suite identifiers, scorer code, and
promotion thresholds belong in the sibling `local-benchmark-runner` repository,
never in production assemblies.

To add or customize a benchmark:

1. Declare the capability and scorecard lane.
2. Freeze development, validation, and confirmation selections before running
   the candidate.
3. Record provider, model, prompt, tool, config, and repository hashes.
4. Use deterministic scoring or independently observed final state where
   possible.
5. Run raw, production-prompt/no-tools, same-prompt/equal-tools, unchanged
   harness, and candidate controls on the same items.
6. Rerun a promising candidate exactly before consuming validation.
7. Reuse a frozen control pack only when all compatibility hashes match and a
   small unchanged-harness sentinel shows no drift.
8. Preserve artifacts and a verdict; keep clearly failed behavior out of
   production.

See [EXPERIMENTATION.md](EXPERIMENTATION.md) for branch and promotion policy and
[TESTING.md](TESTING.md) for local commands and artifact locations. The
[calibrated improvement plan](CALIBRATED_IMPROVEMENT_PLAN.md) defines the active
sequence of work; the [research findings](research/README.md) preserve current
and historical verdicts.

## Interpreting the Wiki result

The semantic Wiki experiment is a harness-capability result. The evaluator
creates disposable local state, sends a normal request, and compares the final
Wiki snapshot with evaluator-only expectations. Production never receives the
expected state.

The promoted production behaviors improve two narrow, real operations:
explicit Wiki-root creation and question-focused compilation of an explicitly
attached multi-page Wiki scope. On the frozen 1.2B validation slice, compact
Wiki evidence scored 5/8 overall and 4/6 attached versus 2/8 and 1/6 for the
unchanged full-scope prompt, while using the same 18 provider calls and less
prompt and latency budget. It does not improve MMLU, general science, or
mathematical reasoning. That narrow claim is intentional and should remain
narrow in release notes and benchmark summaries.
