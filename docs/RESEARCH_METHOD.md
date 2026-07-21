# The Small-Model Improvement Method

Sir Thaddeus is both a local-first assistant and an ongoing engineering
experiment: can a fixed small model complete more ordinary work when the
surrounding product supplies deterministic capabilities, relevant evidence,
durable state, explicit permissions, and observable verification?

This document explains how that question is tested. It is intentionally
stricter than prompt iteration and intentionally narrower than a claim that a
harness can turn a small model into a large one.

## The three scorecards

Every experiment declares one primary scorecard. The other two remain
guardrails.

| Scorecard | Question | Evidence |
| --- | --- | --- |
| Model capacity | Did the fixed model become better at closed-book knowledge or reasoning? | Strict correctness, validity, calibration, robustness, and fresh capacity items without answer-producing tools. |
| Harness capability | Can the same model complete more useful work with the product? | Independently verified answers, files, state changes, tool outcomes, and permission behavior. |
| Product quality | Did the experience become faster, safer, clearer, or more reliable? | First-visible latency, end-to-end latency, calls, tokens, resources, continuity, safety, permissions, and false-success rate. |

The scorecards do not substitute for one another. A calculator-assisted answer
is a harness win, not a closed-book math win. A different model is a deployment
comparison, not an improvement to the fixed model. A faster response is not a
win if it increases false actions.

## The loop

```text
freeze the product, model, provider, prompt, tools, and item set
  -> predeclare one mechanism and its rejection rule
  -> run static and reject-only checks
  -> compare paired controls on a short development slice
  -> rerun the exact candidate in reverse order
  -> consume a disjoint validation set only after repetition
  -> run safety, permission, latency, resource, and product regressions
  -> merge through protected CI or delete the implementation
  -> preserve the verdict either way
```

The development slice is usually designed to finish in ten minutes or less.
Ten minutes is a ceiling, not a target. Cheap checks should reject a bad idea
before model time is spent, and a development win earns another test rather
than a production merge.

## Attribution controls

When attribution requires them, a candidate is compared against four arms
under the same model and inference configuration:

1. **Raw minimal:** the smallest valid evaluator prompt and no product
   capabilities.
2. **Same-prompt direct:** the production identity and safety prompt with one
   direct generation.
3. **Unchanged harness:** the production-equivalent Sir Thaddeus pipeline.
4. **Candidate:** the same pipeline with only the declared mechanism changed.

Oracle-route, oracle-tool, compact-gold-evidence, or gold-state arms may locate
a ceiling. They are labeled as diagnostics and never counted as local-model
success.

## Promotion rules

A mechanism is eligible to ship only when:

- its activation is proved rather than inferred from a score;
- strict outcomes improve without averaging away paired losses;
- the exact candidate repeats in the same direction;
- a frozen, disjoint validation set supports the result;
- negative and semantic-mutation controls remain safe;
- permissions, validity, personality, memory, and continuity do not regress;
- resource costs remain inside the predeclared budget; and
- the production change contains no evaluator or benchmark knowledge.

Promising but incomplete evidence may remain as labeled research. Clearly
failed implementation code is removed rather than hidden behind a disabled
flag. Its manifest, artifact hashes, and verdict remain in the evaluator
ledger so the same attractive failure is not rediscovered later.

## Benchmark-integrity boundary

Production assemblies may see user input, advertised tool schemas, permitted
context, and actual tool results. They may not see:

- expected answers or answer fragments;
- suite or fixture identifiers;
- scorer predicates or promotion thresholds;
- benchmark-specific routing conditions;
- hidden stronger-model calls; or
- development-set wording copied into runtime rules.

Candidates are challenged with paraphrases, renamed tools and arguments,
changed entities and numbers, reordered schemas, irrelevant capabilities,
missing resources, contradictory evidence, permission-sensitive requests, and
temporal holdouts. When an observed result changes the implementation, the
next test uses a fresh candidate revision and unconsumed inputs.

## What the work has shown so far

The evidence supports a narrower and more useful claim than the original MMLU
ambition:

- The stabilized 1.2B MMLU-Pro slice reached `10/20` for raw, same-prompt
  direct, and the current harness. No repeatable closed-book uplift was found.
- Sampled voting and several reasoning or routing mechanisms added cost without
  producing a reliable gain. They were removed.
- Existing capabilities moved a fresh 20-task verified-outcome scorecard from
  `4/20` raw to `15/20` under unchanged Thaddeus, while a compact-gold ceiling
  reached `19/20`.
- Fail-closed unique-file resolution improved a disjoint slice from `5/16` to
  `12/16` twice, with seven paired wins, zero losses, and fewer model calls.
- Wiki temporal-deferral policy improved validation from `4/18` to `13/18`
  while preserving every deferred state and immediate-action control.
- A narrow current local date/time utility produced two correctness gains, one
  harmful-route repair, and a positive first-visible p50 improvement from
  `211.5 ms` to `5.5 ms` with zero model calls on eligible turns.

These are fixed-product slices, not a universal leaderboard. Sample sizes are
often deliberately small because the early gates optimize for information per
model minute. The full context, limitations, promoted results, and negative
findings live in [the research record](research/README.md).

## The current conclusion

The experiments have not shown that orchestration reliably increases a small
model's closed-book intelligence. They have shown that a disciplined harness
can make the same model complete particular real tasks more accurately,
quickly, and safely.

The useful pattern is usually not more autonomous reasoning. It is removing
mechanical work from the model, supplying compact evidence, constraining a
capability at the narrowest safe seam, and verifying the actual outcome.

That conclusion is provisional by design. A materially different capacity
mechanism may reopen the MMLU lane, but it must meet the same attribution and
generalization standard.

## Reproduce or challenge it

Start with:

- [Experimentation](EXPERIMENTATION.md) for the complete decision policy;
- [Benchmarking](BENCHMARKING.md) for suites and scorecard definitions;
- [Testing](TESTING.md) for the shortest trustworthy commands;
- [Current evidence](research/CURRENT_EVIDENCE.md) for supported conclusions;
- [Experiment catalog](research/EXPERIMENT_CATALOG.md) for promoted, rejected,
  inconclusive, and infrastructure results.

The private evaluator repository preserves hidden holdouts and expected
outputs, so production code cannot optimize against them. Public product
changes, test fixtures, activation diagnostics, result summaries, and PR
history remain reviewable here. Independent reproductions, stronger public
outcome sets, and failed replications are all more useful than another
uncontrolled prompt tweak.
