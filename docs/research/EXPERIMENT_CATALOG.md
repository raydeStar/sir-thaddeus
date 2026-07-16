# Experiment Catalog

This is a compact index of material Sir Thaddeus experiments through July 16,
2026. It records reusable conclusions, not hidden benchmark content. Exact
manifests, commands, artifact hashes, and verdicts live in the sibling private
`local-benchmark-runner` repository under `experiments/`.

## Promoted or retained production behavior

| Campaign | Lane | Disposition | Reusable conclusion |
| --- | --- | --- | --- |
| Expanded semantic Wiki-root language | Harness capability | **Promoted** through product PR `#208` | A narrow deterministic first-tool selection can materially improve an explicit operation while leaving arguments, permission, execution, and later rounds in the normal loop. Unseen validation was `14/16` twice versus `9/16`, five wins and zero losses. |
| By-name Wiki contracts | Harness capability | **Promoted** | Resolve unique local targets inside the audited tool boundary to reduce opaque ID/version bookkeeping; fail closed on ambiguity. |
| Hybrid managed-search parity | Evaluation infrastructure | **Accepted** through product PR `#209` | Start managed search only for harness suites that declare web capability. This corrected an invalid environment; it is not a search-quality claim. |
| Harness-only search evidence capture | Evaluation infrastructure | **Accepted** | Capturing exact model-visible evidence in isolated artifacts can distinguish retrieval, utilization, and repeated-search failures without leaking evidence into normal logs. |
| Tool-call syntax intake | Evaluation infrastructure | **Retained** | Both tested small models produced `8/8` parsed valid forced calls. Keep the diagnostic for new model intake; do not add a recovery parser without observed headroom. |
| General capability development battery | Evaluation infrastructure | **Retained** | The 50-item MMLU-Pro, GSM1k, ARC-Challenge, DROP, and IFEval battery is useful for rapid rejection and attribution, not promotion proof. |
| Causal candidate diagnostics and local outcome battery | Evaluation infrastructure | **Accepted through product PR `#211`** | A candidate row now fails closed unless v2 full composition, complete timing/call attribution, and the predeclared active/inactive event are observed. The live one-item proof captured 34 sanitized events. No assistant behavior or capability claim changed. Evaluator manifest: `experiments/manifests/causal-evaluation-infrastructure-v1.yaml`. |
| Conservative pure-compute memoization | Product latency | **Promoted historically** | Cache only exact successful calculator and nonempty Python results within one turn; do not cache mutable, failed, or external calls. |
| Completion validation and bounded retry | Product quality | **Keep supported path** | Global removal reduced quality. Replace only at a capability seam with equal-quality controlled evidence. |

## Model-capacity campaign

| Experiment | Disposition | Result and lesson |
| --- | --- | --- |
| Four-arm MMLU attribution baseline | **Completed; no routing candidate** | No model crossed the two-item prompt/orchestration-loss trigger. LFM 1.2B showed a small harness development gain; it was not universal across the ladder. |
| Answer-blind MMLU structural diagnosis | **Completed** | Found a finite-choice formatting region on the practical model, but did not establish reasoning-scaffold or routing headroom. |
| Finite-choice contract detection | **Rejected** | `5/20` candidate versus `7/20` raw and `6/20` unchanged; validity and correctness gates failed. |
| Closed-book choice scaffold | **Rejected** | `5/20` candidate tied unchanged and remained below `7/20` raw. |
| Capability-scoped choice prompt | **Rejected; campaign paused** | Improved unchanged by one item to `6/20` but remained below `7/20` raw and reduced validity. Third consecutive valid rejection. |
| Arithmetic Plan-and-Solve scaffold | **Rejected** | Compact one-pass planning did not improve LFM 1.2B strict correctness. |
| Sampled self-consistency and tool-aware voting | **Rejected historically** | Added model calls and latency and did not beat unchanged controls. |
| Historical raw-versus-harness 140-item result | **Consumed attribution evidence** | Different system prompts prevent routing attribution; it is not holdout or promotion evidence. |

## Tool and verification campaign

| Experiment | Disposition | Result and lesson |
| --- | --- | --- |
| Forced calculator oracle | **Rejected as product mechanism** | `3/10` versus raw GSM1k `8/10`; tool availability did not fix incorrect expression construction. |
| Tool-call content recovery | **Rejected before implementation** | No malformed recoverable region existed in either tested model. |
| Explicit-format contract headroom | **Rejected before implementation** | Too few generalized contract violations existed on the primary LFM cohort to justify another repair stage. |
| Tool-integrated draft verification v1 | **Rejected no-op** | `6/10` tied unchanged and zero Python calls activated. |
| Tool-integrated draft verification v2 | **Rejected no-op** | Broader shape detection still produced zero tool calls and missed the activation gate. |
| Tool-integrated contract verification v1 | **Rejected as misattributed** | Repeated `5/10` was not causal; a skipped-memory provenance record prevented all ten intended activations. |
| Contract verification output cap v2 | **Rejected** | Saved six aggregate tokens, made no executable calls, and did not improve p95. |
| Contract verification user-role change | **Rejected; family paused** | Changing the verifier follow-up role produced no executable tool calls. |
| Tool-semantic outcome baseline v1 | **Diagnostic accepted; no candidate** | Full menu scored `7/16` versus `3/16` oracle-pruned, `1/16` no-tools, and `3/16` raw. Full completed required tool paths on `8/10` positives and made zero forbidden calls. Oracle pruning had zero wins and four losses, so no routing, relevance, or pruning candidate was authorized. |

## Retrieval and search campaign

| Experiment | Disposition | Result and lesson |
| --- | --- | --- |
| DROP evidence ablation | **Headroom established** | With supplied passage: `5-6/10`; question-only: `0/10`. Evidence can help, but actual retrieval remains unproven. |
| Selective web evidence route | **Rejected before product implementation** | Unchanged Thaddeus already searched `8/8`; explicit forcing tied strict score and increased calls, tokens, and latency. |
| Response-contract evidence framing | **Rejected** | Strict score moved `1/4` to `2/4`, but there was a paired loss and resource regressions. |
| Model-visible source-metadata removal | **Rejected; sequence paused** | Correctness tied `1/6`; searches and calls increased and p95 more than doubled. Structural success cues mattered. |
| Managed SearXNG harness parity | **Accepted as infrastructure** | Restored answer-bearing evidence from `3/6` to `6/6` and reduced degraded-path calls/latency; final utilization remained weak. |
| Compact search-evidence envelope | **Rejected as implemented** | Correctness moved from `0/6` to `6/6`, but calls, searches, tokens, and observed p95 violated the frozen resource gates. The capability signal justified one separately declared follow-up. |
| Deterministic short-answer evidence postcondition | **Inconclusive; retained research in maybe bin** | The combined candidate held `6/6` across development, repeat, unseen validation, and a post-crash replay versus `1/6` unchanged and `0/6` raw on the unseen slice. Resource savings were unstable after the crash and free SearXNG retrieval was not reproducible enough for promotion. It is unmerged and inactive. |

## Stateful Wiki campaign

| Experiment | Disposition | Result and lesson |
| --- | --- | --- |
| Read-before-write | **Rejected on resources** | Exact state improved `2/6` to `5/6`, but p95 was 2.49 times baseline. State inspection is useful; separate model rounds are too costly. |
| Deterministic prefetch v1 | **Rejected; early campaign paused** | Did not satisfy repeatable accuracy/resource gates. |
| Deterministic prefetch v2 and compact v3 | **Inconclusive after evaluator correction** | A tool-manifest defect invalidated the original validation interpretation. Code remained removed. |
| Deterministic prefetch revalidation v4 | **Rejected with useful signal** | Pooled exact state improved `8/24` to `13/24`, but missed all frozen accuracy gates and added calls/tokens. |
| Broad forced write selection v5 | **Rejected** | Broad activation did not preserve accuracy and generality. |
| High-confidence write selection v6 | **Rejected; family paused** | Third consecutive valid miss in the sequence. |
| Side-effect evidence repair | **Rejected** | Promising first run did not reproduce and exceeded the hard latency limit. |
| Semantic Wiki actions v1 | **Rejected** | Missed the development accuracy floor. |
| Semantic Wiki actions replication v2 | **Inconclusive** | Direction repeated, but the frozen zero-loss clause failed. |
| Semantic Wiki actions validation v3 | **Retained research, not promoted** | Validation gate missed; evidence informed narrower root selection. |
| Narrow root selector v6-v8 | **Retained then rejected for promotion** | Root-only headroom existed, but the language grammar was too narrow for promotion. |
| Expanded root language v9-v12 | **Promoted** | Material repeated and unseen gain with product and resource gates; this is the campaign's strongest narrow capability result. |
| Semantic write-selection validation v5 | **Inconclusive retained research** | Development repeated, but semantic-mutation validation weakened. Not promoted. |
| Wiki rename selection v1-v2 | **Rejected after downstream safety test** | Deterministic selection exposed real rename capability but did not solve root-label mismatch or authorization. The temporary control branches were removed after the combined campaign verdict. |
| Decorated root-label resolver v3 | **Rejected on reliability** | Authorized outcomes improved materially, but permissive label resolution also made non-action rename attempts succeed. |
| Rename tool pruning v4 | **Rejected on resources** | Classification worked, but pruning caused a hard p95 regression. |
| Pre-MCP rename execution guard v5 | **Inconclusive, then retired** | The guard blocked attempted writes but showed no final-state uplift while the exact-only tool already failed closed. It was retained only to test guarded label tolerance. |
| Guarded decorated root-label resolver v6 | **Rejected; mechanism family closed** | Authorized state improved `1/8` to `6/8` with five wins and zero losses, but two of eight non-actions became unauthorized writes, safety scored `5/6`, and no-action p95 was `1.586x` v5. All temporary Wiki rename branches were removed. |

## Retired architecture ideas

| Mechanism | Disposition | Reason |
| --- | --- | --- |
| Global completion-validation removal | **Retired** | Reduced quality. |
| Global retry removal | **Retired** | Reduced quality. |
| Shadow `TurnPlan` compilation | **Retired** | No demonstrated product uplift. |
| `RouterV2` and LLM task-plan builder | **Retired** | Uncomposed experimental architecture without promotion evidence. |
| Always-on retrieval | **Not authorized** | Evidence can distract small models; retrieval needs selective and negative controls. |
| Recursive autonomous planning | **Not authorized** | Adds unbounded cost and no current causal evidence. |
| Same-model self-critique | **Not authorized** | A second opinion from the same model is not independent verification. |
| Multi-model/MoE routing | **Deferred** | Requires a labeled complementary failure region and must be reported as escalation, not fixed-model uplift. |

## Evaluator evidence pointers

Use these repository-relative locations in the sibling
`local-benchmark-runner` repository to recover exact manifests and verdicts:

- MMLU attribution and stop decision: `experiments/verdicts/baseline-mmlu-attribution-v1.md`
  and `experiments/verdicts/mmlu-campaign-stop-20260714.md`.
- General battery and evidence/calculator headroom:
  `experiments/verdicts/2026-07-15-general-capability-headroom-v1.md`.
- Search and evidence sequence: `experiments/manifests/selective-evidence-v1.yaml`,
  `response-contract-evidence-v1.yaml`, `model-visible-search-evidence-v1.yaml`,
  `compact-search-evidence-envelope-v1.yaml`, and
  `deterministic-short-answer-validation-v1.yaml` on its retained research
  branch.
- Tool syntax and contract headroom:
  `experiments/verdicts/2026-07-16-tool-call-syntax-headroom-v1.md` and
  `2026-07-16-explicit-format-contract-headroom-v1.md`.
- Tool-semantic attribution:
  `experiments/manifests/tool-semantic-outcome-baseline-v1.yaml` and
  `experiments/verdicts/2026-07-16-tool-semantic-outcome-baseline-v1.md`.
- Tool-integrated verification sequence:
  `experiments/verdicts/2026-07-16-tool-integrated-draft-verification-v1.md`
  through `2026-07-16-contract-verification-round-trace-v1.md`.
- Stateful Wiki sequence: the dated `everyday-state-*` manifests and verdicts
  under `experiments/`, ending with
  `2026-07-15-everyday-state-semantic-wiki-root-language-product-regressions-v12.md`.
- Wiki page rename sequence: `experiments/manifests/wiki-page-rename-selection-v1.yaml`
  through `wiki-guarded-root-label-resolution-v6.yaml`, ending with
  `experiments/verdicts/2026-07-16-wiki-guarded-root-label-resolution-v6.md`.

## Update rule

Add a row only after the evaluator verdict is frozen. Use aggregate scores and
mechanism-level lessons; do not copy raw outputs or hidden expected values.
When a later correction supersedes a verdict, update the existing row rather
than leaving contradictory summaries.
