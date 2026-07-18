# Inference Method Gap Map

**Reconciled:** July 18, 2026
**Production baseline:** `a94ce44`
**Evaluator baseline:** `bce818c` through evidence correction PR `#63`

This map ranks research mechanisms against Sir Thaddeus evidence. It does not
replace the experiment ledger or promotion policy. Model capacity, fixed-model
harness capability, complete-system outcomes, product quality, and adapted-
model capacity remain separate scorecards.

## Decision

The ordered pipeline and evaluation foundation are adequate. Do not build a
new planner, router, verifier framework, or agent hierarchy. The strongest
remaining strategy is still narrow externalization: find a repeated failure,
prove that a deterministic or external substrate can correct it, then add the
smallest capability-specific seam.

No untested fixed-model product candidate currently satisfies all five entry
conditions: a repeated unresolved cluster, observable activation, oracle
headroom, an independently checkable score, and acceptable scope. The
current-master refresh found a three-case literal-response cluster and an
offline oracle corrected it, but product candidates v1-v3 all recorded zero
activations and tied unchanged. That family is closed. Do not create another
postcondition until a materially different observable seam establishes causal
activation before scoring.

## Reconciled gap analysis

| Method family | Status | Current evidence | 1B-8B applicability | Expected benefit | Added cost | Verification | Risk | Disposition |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Deterministic tool/schema validation | Implemented | Tool arguments are parsed and checked before dispatch; both intake models produced `8/8` valid forced calls | High | Prevent malformed execution | Negligible | Strong for shape, not meaning | False confidence if called semantic proof | Keep |
| Provider-constrained JSON/grammar decoding | Missing in product; evaluator retained | Corrected v2: unconstrained and constrained were both `10/10` valid and `9/10` semantically exact; zero outcome gain | High when the model supports it | Guaranteed supported structure | Near-zero in the observed run | Strong for structure only | Provider coupling; schema-valid wrong values | Defer until a real contract has at least three structural failures |
| External computation | Implemented selectively | Calculator/Python are useful when translation is correct; forced calculator scored `3/10` versus raw `8/10` | Medium-high | Exact arithmetic or execution | Tool call plus synthesis | Strong for executed result | Bad program/expression remains bad | Keep selective; do not force globally |
| Capability-specific evidence/postconditions | Partial; one promoted | Answer-only tool-evidence projection repeated `10/12` versus `5/12`, then validated `12/16` versus `8/16` with zero negative activations | High | Converts proven tool evidence into correct final contracts | Removes one helper call when active | Strong within its narrow proof | Overprojection or stale evidence | Preferred product pattern after oracle headroom |
| Generic verifier and typed repair framework | Partial primitives; framework missing | Deterministic checks, bounded repair, tool results, and state observations exist; generic verifier prompts failed to activate or improve | Medium | Reusable failure-directed repair | Potential extra call | Depends entirely on verifier | Abstraction without sound checks; more `ToolLoopStep` complexity | Do not build until two real capabilities with oracle headroom need the same seam |
| Corrective retrieval and evidence abstention | Partial/deferred | Gold passage produced `5-6/10` versus `0/10`; forced web routing and several framing changes lost; Wiki packet and answer projection already promoted | Medium-high | Better grounded answers and fewer unsupported claims | Retrieval, rerank, or one retry | Strong only with attributable evidence | Irrelevant/contradictory context; unreliable public search | Find a distinct local or stable-source failure cluster first |
| Verifier-guided candidate selection | Missing; general voting rejected | Self-consistency/voting added latency and lost; no current domain combines repeated misses with a sound selector | Domain-specific | Can select executable or state-valid candidates | Multiple generations | Strong only with external verifier | Cost and selection bias | Defer until a sound verifier already exists |
| Calibrated abstention/adaptive effort | Partial policy, uncalibrated | Deterministic routing and bounded retries exist; no frozen risk/coverage dataset or reliable confidence signal exists | Medium | Lower false-success rate and selective cost | Usually low, sometimes escalation | Statistical, distribution-dependent | Hides errors by shrinking coverage | Defer pending at least 300 labeled outcomes; always report coverage |
| QLoRA/rationale distillation | Missing; planned separate lane | Three prompt/scaffold candidates failed; stable capacity misses exist, but no clean training corpus is frozen | High for a chosen architecture | Only remaining route likely to move intrinsic capacity materially | Training plus new artifact | Strong with four-arm fresh holdouts | Leakage, narrow overfit, quantization drift | Prepare after a generalized failure cluster and dataset audit |
| Multi-model/specialist routing | Deferred | No 300-outcome complementary failure map; changing models does not improve the fixed model | Potentially high | Escalation can improve system outcomes | VRAM and routing latency | Strong if disclosed and paired | Attribution confusion and operational complexity | Keep deferred |
| Broad conversational fast path/router rewrite | Rejected by measurement | Footman made zero LLM calls; optional helpers were 10% of ordinary-turn time; global validation/retry removal reduced quality | Medium | Limited measured upside | Engineering and regression cost | Product latency only | Quality, memory, safety, continuity | Do not reopen without a new measured blocker |

## Verification audit

| Existing seam | Classification | What it proves | What it does not prove |
| --- | --- | --- | --- |
| `ToolArgumentValidator` | Deterministic structure validator | JSON parse, required fields, known fields, coarse types | Correct tool, correct entity, correct sequence, or task completion |
| `EnforcingToolRunner` and tool results | External execution plus permission boundary | The authorized tool ran or returned a typed failure | The model requested the right operation or summarized it correctly |
| Wiki revision, ambiguity, and observed-state contracts | State/postcondition validation | Named state exists or changed under the tool's concurrency rules | Broader user intent satisfaction |
| `EvidenceBackedAnswerOnlyProjection` | Retrieved/tool-evidence validator plus deterministic renderer | One unique scalar is present in both successful evidence and the sanitized draft | General factual correctness or multi-value synthesis |
| `ToolBackedResponseQualityGuards` | Capability-specific evidence interpretation | Certain supported tool outcomes can be rendered or rescued deterministically | A universal correctness judgment |
| `CompletionValidator` heuristics | Deterministic response-quality checks | Empty, echoed, refused, or mechanically malformed contract responses | Factual or semantic correctness |
| `CompletionValidator` LLM fallback | LLM completeness heuristic | A same-model quality opinion | Independent verification; it fails open by design |
| `RepairLoop` | Bounded same-model repair using observed feedback | One targeted retry was attempted and rechecked | Correctness unless the triggering and final checks are external or deterministic |

The current pieces are sufficient for another narrow postcondition. They are
not evidence that a generalized verifier abstraction would earn its cost.

## Ranked experiment basket

### 1. Current-master postcondition headroom refresh - completed

- Reason for rank: it is the cheapest way to determine whether the promoted
  answer-only projection left a repeated argument, sequence, or state cluster.
- Primary scorecard: harness capability.
- Hypothesis: at least three current-master failures share one externally
  observable typed failure and a gold tool/state oracle corrects them.
- Activation: successful tool trace plus independently observed final response
  or state mismatch.
- Controls: unchanged full menu, oracle tool/evidence/state, no-tools negatives;
  raw only where the task is meaningful without tools.
- Budget: one 10-16 item invocation under ten minutes.
- Gate: at least three oracle paired wins, zero safety/permission losses, and a
  single generalized failure class.
- Result: current master scored `9/16`; an answer-blind oracle reached `12/16`
  twice, but product candidates v1-v3 stayed `9/16` with zero activations.
- Stop: the mechanism family is closed; validation remains unconsumed.
- Rollback: evaluator evidence only; write no product branch until the gate.

### 2. Learning-lane dataset and QLoRA pilot — smoke rejected

- Reason for rank: prompt-only capacity work reached its stop rule; weight
  adaptation is materially different and may move stable reasoning failures.
- Primary scorecard: adapted-model capacity, never fixed-model harness uplift.
- Hypothesis: a clean generalized training set improves fresh capacity items
  without reducing base breadth.
- Activation: new adapter/model artifact and recorded training provenance.
- Controls: base raw, base harness, adapted raw, adapted harness.
- Budget: first prove overfit on a tiny training/dev slice; schedule real
  training separately from the sub-ten-minute inference loop.
- Gate: repeatable fresh-holdout gain with no leakage and acceptable breadth.
- Stop: no dev learning signal, contamination risk, or holdout regression.
- Rollback: delete the adapter; production remains on the frozen base.
- Result: the contamination audit, native load, 40-step QLoRA run, adapter save,
  and fresh reload completed in 89.25 seconds. Loss fell `6.8134 -> 2.2826`,
  but held-in exact reproduction stayed `0/4` versus the frozen `3/4` gate.
- Disposition: reject the smoke and do not start a real training campaign from
  it. Retain only the evaluator plumbing and provenance record. A materially
  different rationale-distillation or training-data hypothesis needs a new
  predeclaration; changing steps or targets is not a continuation.

### 3. Distinct corrective local retrieval with abstention

- Reason for rank: evidence headroom is large, but web routing and framing are
  already closed and free public search is not reproducible.
- Primary scorecard: harness capability and false-success rate.
- Hypothesis: an evidence-sufficiency check plus at most one corrective local
  retrieval pass increases supported correct answers without increasing
  unsupported answers.
- Activation: first retrieval is deterministically inadequate or contradictory.
- Controls: no retrieval, unchanged, gold, candidate, irrelevant, contradictory.
- Budget: ten-item triage; no web dependency.
- Gate: at least two supported paired wins, zero added unsupported answers, and
  bounded calls/latency.
- Stop: gold evidence still fails or the sufficiency check cannot separate
  relevant from distractor evidence.
- Rollback: remove the retrieval policy; preserve the corpus and verdict.

### 4. Native provider-context contract v2

- Disposition: rejected before product implementation.
- Result: two identical current-master runs at a verified 16K context each
  completed `3/3`, passed `2/3` public contracts, and reproduced one irrelevant
  `memory_store_facts` permission prompt. The provider was restored exactly.
- Primary scorecard: product reliability, not capability.
- Conclusion: the frozen lifecycle mechanism would reliably expose a product
  state that still violates the permission gate. Do not rebase it until the
  read-only memory tool surface is independently safe.
- Activation: native provider metadata confirms the requested loaded instance.
- Controls: unchanged JIT load, candidate load, already-loaded preservation.
- Budget: three public contracts per arm, reversed order, under ten minutes.
- Gate: zero overflow, no permission regression, same or better correctness,
  p95 within `1.25x`.
- Stop: any new prompt, state displacement, or repeated latency miss.
- Rollback: completed without a product branch; the exact provider instance was
  restored.

### Deferred mechanisms

- Structured decoding: reopen only on measured structural failures; the clean
  v2 diagnostic added zero correct records.
- Verifier-guided sample-and-select: requires an already sound external checker.
- Calibrated abstention: requires a frozen labeled outcome set and risk/coverage
  evaluation.
- Multi-model routing: requires 300 labeled complementary outcomes and must be
  reported as escalation.

## Selected next action

The corrective local-retrieval, native provider-context, and conservative
memory-tool candidates are now closed. Memory-tool v3 repeated good precision
(`3/4` recalls and `0/6` negatives) and removed two permission prompts, but
increased provider calls from 45 to 50 and stopped before repeat. Do not select
another product mechanism immediately. First aggregate current-master artifacts
answer-blind across the fixed 1.2B scorecards; run at most one abridged refresh
if existing evidence cannot identify a repeated observable failure cluster with
oracle headroom. Reopen learning only with a materially different,
contamination-audited rationale-distillation or training-data hypothesis. The
[learning-capacity recalibration](LEARNING_CAPACITY_RECALIBRATION.md) now
defines that lane: evaluator-only timing and ceiling preparation first, then at
most one scale-only 512-example candidate on a fresh 120-item development slice.

## Research basis

- [LM Studio structured output](https://lmstudio.ai/docs/developer/openai-compat/structured-output)
- [llama.cpp grammar and JSON-Schema support](https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md)
- [JSONSchemaBench](https://arxiv.org/abs/2501.10868)
- [XGrammar](https://arxiv.org/abs/2411.15100)
- [PAL](https://arxiv.org/abs/2211.10435)
- [CRITIC](https://arxiv.org/abs/2305.11738)
- [Corrective Retrieval-Augmented Generation](https://arxiv.org/abs/2401.15884)
- [QLoRA](https://arxiv.org/abs/2305.14314)
