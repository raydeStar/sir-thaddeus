# Research Record

This folder is the durable, product-repository summary of Sir Thaddeus
research. It exists so future experiments start from established evidence
rather than conversation history, an old branch, or an attractive mechanism
that has already failed.

## Start here

1. [CURRENT_EVIDENCE.md](CURRENT_EVIDENCE.md) summarizes what is working, what
   is not working, and what remains uncertain.
2. [EXPERIMENT_CATALOG.md](EXPERIMENT_CATALOG.md) lists the material mechanisms
   tried so far and their disposition.
3. [../CALIBRATED_IMPROVEMENT_PLAN.md](../CALIBRATED_IMPROVEMENT_PLAN.md)
   defines the current forward plan and stop rules.
4. [../EXPERIMENTATION.md](../EXPERIMENTATION.md) defines the experiment
   protocol; [../BENCHMARKING.md](../BENCHMARKING.md) defines the scorecards.

## Source of truth

This folder is an answer-blind summary. The sibling private
`local-benchmark-runner` repository remains authoritative for manifests,
immutable artifact hashes, exact commands, suite fingerprints, and verdicts.
Raw outputs, expected answers, scorer predicates, and hidden holdouts do not
belong here or in production assemblies.

If this summary disagrees with an evaluator verdict, the evaluator verdict
wins. Correct this folder in the next documentation PR.

## How to keep it current

Every completed experiment must leave one of four explicit dispositions:

- **Promoted:** demonstrated behavior merged through protected CI.
- **Rejected:** mechanism failed a predeclared gate; implementation removed.
- **Inconclusive:** evidence cannot support promotion or rejection.
- **Infrastructure:** measurement improved, but no product capability claim is
  allowed.

When a verdict changes:

1. Update the relevant row in `EXPERIMENT_CATALOG.md`.
2. Update `CURRENT_EVIDENCE.md` only when the result changes a reusable lesson.
3. Update the calibrated plan only when the result changes priority or closes a
   mechanism family.
4. Record the evaluator manifest or verdict by its evaluator-repository-relative
   path; use the catalog's evidence-pointer section as the index.
5. Never copy hidden inputs, expected outputs, answer fragments, or raw model
   responses into this repository.

Historical scores are labeled with their date and scope. They are evidence,
not a live leaderboard.
