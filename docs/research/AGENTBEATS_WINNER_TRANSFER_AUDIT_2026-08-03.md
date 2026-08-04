# AgentBeats Winner Transfer Audit

Date: 2026-08-03

## Verdict

**Retain the design direction; do not add a winner-inspired runtime candidate
yet.** The strongest transferable pattern is deterministic reduction of the
model's action space at a boundary whose semantics are already known. Sir
Thaddeus already applies that pattern through capability filtering, explicit
typed Wiki operations, runtime-owned targets, schema validation, permission
gates, and independently observed Wiki effects.

The current misses do not support a cheap schema normalizer, terminal alarm,
planner, retrieval layer, self-critique pass, or general action-claim repair.
The next eligible seam is narrower and harder: grounded semantic-delta binding
for an already selected update operation. It requires an oracle prerequisite
on fresh tasks before product code.

This is a transfer audit, not a reproduction of the external leaderboards. The
public repositories establish what their authors built and reported; they do
not establish a fixed-model uplift for Sir Thaddeus.

## What the winners demonstrate

| System | Strongest relevant mechanism | Evidence strength | Transfer decision |
| --- | --- | --- | --- |
| AgentWhetters BWIM | Typed actions plus a deterministic 2.5-D executor remove vertical placement from the model's output space | Strong within its task: the authors report 94.6% structural accuracy, a drop to 43.8% without the 2.5-D reduction, and transfer improvement on IGLU | Keep reducing action space only where the application owns the removed dimension |
| CAReful | Live-state grounding, schema guards, programmatic ambiguity ordering, policy checking, and an action-claim guard | Credible architecture and competition submission; no matched fixed-model ablation was found in the inspected README | Audit against existing state, policy, and verification boundaries before adding a guard |
| Pi-Bench purple agent | One model call per turn plus four reversible schema-derived post-processors; no planner, retrieval, vote, or critique pass | Clear implementation contract, but the inspected README does not isolate each post-processor's score contribution | Reopen normalization only after at least three structural failures of the same kind |
| AgentWhetters SWE-bench Pro | Flat loop, discovered repository test command, captured pre-existing failures, mechanical completion gate, and a bounded QA repair phase | Winner-reported system architecture; uses a frontier reasoning model, so small-model transfer is not established | Preserve baseline-aware mechanical gates; do not infer that longer loops improve local-model capability |
| AgentWhetters general purple | Thin adapters selected from protocol structure, with one shared reasoning core and no benchmark-name routing | Strong architecture example; not a fixed-model ablation | Use for the future A2A integration boundary, separate from product-behavior experiments |

## What Sir Thaddeus already has

These are retained analogues, not new candidates:

- **Output-space reduction.** Footman capability filtering and explicit Wiki
  operation projection reduce visible tools. Typed selections keep opaque
  identity runtime-owned while the model supplies semantic content.
- **Schema and permission enforcement.** `ToolArgumentValidator`, the audited
  MCP boundary, explicit mutation target checks, and permission policy reject
  malformed or unauthorized execution before side effects.
- **State-derived evidence.** Versioned Wiki results are classified as
  independently verified effects rather than trusting transport success or a
  model claim.
- **Bounded repair.** Completion repair is limited and validation-led. Historical
  global retry and validation removal reduced quality, while sampling and
  majority voting added latency without a win.
- **Mechanical experiment gates.** Campaigns freeze controls, task counts,
  hashes, promotion thresholds, repeats, validation, resource costs, and
  rollback before model calls.
- **Action-space observability.** Runtime diagnostics already record projected
  tool counts and withheld tools, so future action-space changes can be
  attributed rather than guessed.

## Scorer-blind local artifact audit

The latest Wiki operation/non-action campaign was inspected using only public
user requests, raw model/tool traces, final responses, and the already recorded
campaign verdict. Hidden expected values and scorer predicates were not used to
select a runtime change.

Findings:

- The candidate improved disjoint validation from `5/12` to `10/12`, with five
  paired wins, zero losses, `6/6` no-action controls, and zero forbidden writes,
  but correctly remained rejected because its frozen absolute gate was missed.
- The two remaining candidate misses selected the correct update tool, root,
  page, and schema-valid `markdown` argument.
- In both misses, the argument copied an instruction wrapper into the page
  rather than writing only the requested replacement value. Examples of the
  faulty shape were `Update ... to Status: clear; do not delete ...` and
  `Change ... to Owner: Lena Fox` instead of the requested scalar content.
- No repeated invalid-JSON, missing-required-field, unknown-field, alias,
  scalar-to-array, or empty-extra-key failure cluster appeared in this
  campaign. Pi-Bench-style structural normalization therefore has no measured
  target here.
- One task produced a successful versioned Wiki update and then claimed in the
  final response that the change could not be executed. The same task repeated
  that contradiction in baseline and candidate arms, but this is one semantic
  task shape rather than three aligned fresh failures. It is recorded as a
  product-quality signal, not authorization for a global response rewrite.

## Candidate gate: grounded semantic delta

The next prerequisite should test whether supplying an explicit, source-grounded
delta fixes a fresh cluster after tool selection is already correct:

```text
operation: update
target_id: runtime-owned identity
field: semantic content field
new_value: exact requested replacement
source_quote: verbatim span from the user request or approved plan
expected_postcondition: independently observable final value
```

This contract must not infer authorization, select a tool, guess an entity, or
rewrite arbitrary prose. The first test is an evaluator-only gold-delta oracle
on fresh tasks. Product implementation is eligible only if the oracle produces
a dominant causal improvement while preserving no-action, permission, validity,
latency, token, and raw-language guardrails.

## Stop and reopen rules

- **Reversible schema normalization:** reopen after at least three fresh
  structural failures sharing one schema-derived repair. Every transformation
  must be lossless or mechanically reversible and must not alter semantic
  values.
- **False action-claim guard:** reopen after at least three successful,
  independently verified effects followed by materially false completion
  claims. Prefer a typed verified receipt over phrase-by-phrase rewriting.
- **Terminal progress alarm:** reopen only after a fresh bounded multi-turn
  cohort shows repeated near-budget stalls with the correct capability still
  available. It must remain silent outside that state.
- **Additional planning, retrieval, voting, or critique:** remain closed without
  materially new evidence and an independent selector or verifier.
- **Protocol adapters:** pursue only in the external benchmark integration lane.
  Route by protocol shape, never benchmark identity, and keep the production
  reasoning core and provider configuration reusable.

## Primary sources

- [AgentWhetters BWIM](https://github.com/paulwhitten/AgentWhetters-bwim)
- [CAReful CAR-bench agent](https://github.com/gmsh/car-bench-exp-agent)
- [Pi-Bench purple agent](https://github.com/ab-shetty/pi-bench-alpha)
- [AgentWhetters SWE-bench Pro simple loop](https://github.com/paulwhitten/AgentWhetters-swe-bench-pro-purple-simple_loop)
- [AgentWhetters general protocol-adapter agent](https://github.com/paulwhitten/AgentWhetters-general-purple)
