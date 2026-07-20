# Experiment Catalog

This is a compact index of material Sir Thaddeus experiments through July 19,
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
| Structured-output headroom intake | Evaluation infrastructure | **Retained; no product candidate** | After excluding a schema-echo-confounded v1, the natural-contract v2 produced `10/10` schema-valid and `9/10` semantically exact records in both unconstrained and constrained arms. Constraints added zero correct outcomes. |
| General capability development battery | Evaluation infrastructure | **Retained** | The 50-item MMLU-Pro, GSM1k, ARC-Challenge, DROP, and IFEval battery is useful for rapid rejection and attribution, not promotion proof. |
| Causal candidate diagnostics and local outcome battery | Evaluation infrastructure | **Accepted through product PR `#211`** | A candidate row now fails closed unless v2 full composition, complete timing/call attribution, and the predeclared active/inactive event are observed. The live one-item proof captured 34 sanitized events. No assistant behavior or capability claim changed. Evaluator manifest: `experiments/manifests/causal-evaluation-infrastructure-v1.yaml`. |
| Conservative pure-compute memoization | Product latency | **Promoted historically** | Cache only exact successful calculator and nonempty Python results within one turn; do not cache mutable, failed, or external calls. |
| Completion validation and bounded retry | Product quality | **Keep supported path** | Global removal reduced quality. Replace only at a capability seam with equal-quality controlled evidence. |
| Answer-only successful-tool-evidence projection | Harness capability | **Promoted through product PR `#219`** | Project one unique verbatim scalar only when it exists in both the sanitized draft and successful local tool evidence. Development repeated at `10/12` versus `5/12`; disjoint validation was `12/16` versus `8/16`, four wins, zero losses, full validity, and zero negative activations. Applied cases skip one completion-validator call. |
| Fail-closed unique local-file suffix resolution | Harness capability | **Protected product PR `#233`** | Resolve an incomplete read path only when one authorized-root file has the requested safe suffix. Fresh validation repeated at `12/16` versus `5/16`, with seven wins, zero losses, full validity, zero negative activations, fewer calls/tokens, and lower latency. Explicit `./name`, ambiguity, unsafe syntax, and writes do not receive recursive assistance. |
| Verified-outcome harness redirect scorecard | Evaluation infrastructure | **Accepted; no product behavior changed** | A frozen 32-task local scorecard applies required-tool, forbidden-tool, and observed-state verification to raw, same-prompt direct, and unchanged Thaddeus. Raw and harness each scored `9/32`; direct scored `6/32`. It is consumed development evidence, not the eventual 80% claim. Evaluator PR `#78`. |
| Source-audited representative task pilot | Evaluation infrastructure | **Accepted; no product behavior changed** | Eleven public sources were audited for task, evaluation, or exclusion roles. Ten newly authored local tasks scored `3/10` raw and `4/10` unchanged harness, with two paired wins and one loss. One open-ended exact scorer was unsuitable, so the result is diagnostic rather than a capability claim. Evaluator PR `#90`. |
| Six-category gold-headroom screen | Evaluation infrastructure | **Retained; breadth gate rejected** | Compact scorer-blind support improved the frozen 1.2B model from `2/12` to `9/12` with seven wins, zero losses, and full validity. Extraction and verified arithmetic-result use cleared `2/2`; three policy/selection categories remained `1/2`, so only two of the required three categories established headroom. No product candidate was authorized. Evaluator PR `#92`. |

## Model-capacity campaign

| Experiment | Disposition | Result and lesson |
| --- | --- | --- |
| Four-arm MMLU attribution baseline | **Completed; no routing candidate** | The stabilized same-model control put raw, same-prompt direct, current harness, and the historical product SHA at `10/20`. The saved `13/20` repeats did not reproduce under the frozen runtime, so they remain consumed historical observations rather than a current harness uplift. |
| Answer-blind MMLU structural diagnosis | **Completed** | Found a finite-choice formatting region on the practical model, but did not establish reasoning-scaffold or routing headroom. |
| Finite-choice contract detection | **Rejected** | `5/20` candidate versus `7/20` raw and `6/20` unchanged; validity and correctness gates failed. |
| Closed-book choice scaffold | **Rejected** | `5/20` candidate tied unchanged and remained below `7/20` raw. |
| Capability-scoped choice prompt | **Rejected; campaign paused** | Improved unchanged by one item to `6/20` but remained below `7/20` raw and reduced validity. Third consecutive valid rejection. |
| Arithmetic Plan-and-Solve scaffold | **Rejected** | Compact one-pass planning did not improve LFM 1.2B strict correctness. |
| Sampled self-consistency and tool-aware voting | **Rejected historically** | Added model calls and latency and did not beat unchanged controls. |
| Native-checkpoint QLoRA training-path smoke v1 | **Rejected; plumbing retained in evaluator** | In 89.25 seconds, loss fell `6.8134 -> 2.2826` and a 884,736-parameter adapter saved/reloaded consistently, but held-in exact reproduction stayed `0/4` versus a frozen `3/4` gate. This is infrastructure evidence only, not a benchmark or capacity gain. |
| Behavior-preserving selective science adapter | **Rejected; research branches deleted** | Fresh OpenBookQA improved `15/40 -> 19/40`, and generated MMLU-Pro science improved `0/30 -> 4/30` twice, but the adapter regressed a mixed guardrail from `8/10` to `6/10`. A format-independent option-content control tied native base at `8/30` with zero paired wins/losses, showing response-contract improvement rather than attributable knowledge gain. |
| Full-support rationale distillation v1 | **Mixed; closed and archived, not merged** | With matched 128-record QLoRA arms, native base and answer-only scored `8/30`; human SciQ support-rationale supervision scored `10/30` with three wins and one loss. It missed the frozen `+3` gate and reduced rotation invariance from `5/6` to `4/6`. Evaluator PR `#74` was closed during retirement; immutable history was archived and no adapter shipped. |
| Concise-evidence rationale distillation v2 | **Rejected; implementation deleted; campaign paused** | An answer-blind selector changed 55/64 supports and reduced median rationale length 74.65%. Training and activation passed and invariance returned to `5/6`, but strict accuracy was `9/30`: `+1` over base/answer-only and `-1` versus full-support v1. This third valid rejection paused the MMLU candidate loop. |
| Scale-only support-rationale 512 prerequisite | **Rejected before training; implementation deleted** | On a fresh 30-item SciQ oracle, gold human support moved the unchanged 1.2B model from `13/30` to `17/30` with four wins, zero losses, and positive margin movement. It missed the frozen `18/30`, `+6/30`, and eight-win utilization gate, so no teacher, adapter, MMLU-Pro development, repeat, or validation run was spent. |
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
| Provider-constrained structured output v2 | **Deferred for no headroom** | Unconstrained and constrained were both `10/10` structurally valid and `9/10` semantically exact. The identical semantic miss was not schema-repairable; no product code or validation run was justified. |
| Current-master tool-semantic refresh | **Diagnostic accepted** | After the promoted answer-only evidence projection, unchanged Thaddeus scored `9/16` with full validity versus `3/16` raw and `1/16` same-prompt direct. This is a local harness-outcome signal, not a general or MMLU uplift. |
| Declared-capability route oracle | **Oracle passed; product path rejected** | An evaluator-only split reached `13/32` twice, four above both raw and harness. Substituting a compact product-invariant prompt reached `11/32`, below the frozen `12/32` and `+3` gate, so no production router was implemented. |
| Structured tool-failure evidence oracle | **Rejected before product implementation** | Concise verified permission/unavailable evidence under the production prompt produced `0/3` strict with `3/3` validity. Missing error metadata was not the bottleneck; the closed literal-response family was not revived. |
| Literal-response contract oracle | **Headroom established offline** | An answer-blind public-prompt projection raised the immutable `9/16` artifact to `12/16` twice with three wins, no losses, and three activations. Oracle headroom authorized a narrow product attempt but was not itself promotable. |
| Literal-response contract candidates v1-v3 | **Rejected; family closed** | All three product candidates tied unchanged at `9/16` with zero activations. V3 recorded 44 calls, 67,408 tokens, full validity, and zero paired wins/losses; repeat and disjoint validation were not run. The implementation branches were deleted. |
| One-shot audited local-read recovery v1-v3 | **Rejected; family closed** | Fresh route-agnostic adjudication confirmed four verified gains and zero paired losses (`7/12` candidate versus `3/12` unchanged) while improving p95. V3 also activated on an exact-missing-file negative, reducing validity to `11/12`; no repeat ran and all product branches were deleted. |

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

## Product latency and reliability campaign

| Experiment | Disposition | Result and lesson |
| --- | --- | --- |
| Helper-call activation and latency cohort v1 | **Diagnostic accepted; no router candidate** | Across 30 repeated turns, Footman used zero LLM calls and optional helpers were 10% of ordinary-conversation end-to-end time. Prompt construction was negligible. The actionable finding was a repeated 16K-runtime versus 8K-provider context overflow, not routing overhead. |
| Explicit memory tool budget v1 | **Inconclusive retained research** | Removed the repeated overflow, exposed 9 instead of 60 tools, and produced one paired win with zero losses. Fresh validation reached `5/6` intended and `0/8` false activations, so the unmerged conservative branch is not promoted. |
| Explicit memory precedence v2 | **Rejected; precedence family closed** | Early results repeated the win, but fresh validation routed four of ten browse, file, research, or system actions to memory. Simple precedence is unsafe for overlapping intent. |
| Conservative explicit memory tool budget v3 | **Rejected on call cost** | Fresh current-master development activated `3/4` recalls and `0/6` negatives, removed two permission prompts, and tied public outcomes `8/10`, but provider calls increased from 45 to 50. No repeat or validation ran; the product branch was deleted. |
| Missing attachment clarification v1 | **Rejected** | Repeated a large zero-call latency gain, then falsely replaced a reminder and an email-drafting request on fresh validation. |
| Missing attachment request clause v2 | **Rejected; regex family closed** | Clause anchoring repeated positive p95 of 74 ms first-visible and 883 ms end-to-end versus 1,235 ms and 2,481 ms unchanged, but fresh validation reached only `5/7` intended with `2/12` false activations. Use structured attachment state or an explicit action if revisited. |
| Native model-load contract v1 | **Inconclusive retained research** | Reproduced the requested 16,384-token provider state and removed overflow, but added one irrelevant permission prompt and repeated at `1.258x` baseline p95, narrowly missing the `1.25x` gate. |
| Prepermission no-op validation v1 | **Rejected** | Static validation activated, then the model chose worse fallback tools; correctness fell `2/3` to `1/3`, a permission prompt appeared, calls increased, and p95 rose. |
| Native model-load current-baseline eligibility v2 | **Rejected before product implementation** | Two identical current-master runs at a verified 16,384-token context each completed `3/3`, passed `2/3` contracts, and reproduced one irrelevant `memory_store_facts` permission prompt. The lifecycle branch was not rebased; provider restoration was exact. |

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
- Selective science adaptation and format-independent attribution:
  `experiments/verdicts/2026-07-18-behavior-preserving-logit-distillation-v1.md`,
  `2026-07-18-selective-science-adapter-activation-v1.md`,
  `2026-07-18-selective-science-adapter-cross-benchmark-v1.md`, and
  `2026-07-18-selective-science-answer-selection-v1.md`.
- Rationale-distillation sequence:
  `experiments/verdicts/2026-07-18-support-rationale-distillation-v1.md` and
  `experiments/verdicts/2026-07-18-concise-evidence-rationale-distillation-v2.md`.
- General battery and evidence/calculator headroom:
  `experiments/verdicts/2026-07-15-general-capability-headroom-v1.md`.
- Source-audited representative tasks and the six-category headroom screen:
  `experiments/verdicts/2026-07-19-representative-task-executable-v2.md` and
  `experiments/verdicts/2026-07-20-gold-headroom-screen-v1.md`.
- Search and evidence sequence: `experiments/manifests/selective-evidence-v1.yaml`,
  `response-contract-evidence-v1.yaml`, `model-visible-search-evidence-v1.yaml`,
  `compact-search-evidence-envelope-v1.yaml`, and
  `deterministic-short-answer-validation-v1.yaml` on its retained research
  branch.
- Local answer-only evidence projection:
  `experiments/manifests/answer-only-tool-evidence-projection-v1.yaml` and
  `experiments/verdicts/2026-07-17-answer-only-tool-evidence-projection-v1.md`.
- Tool syntax and contract headroom:
  `experiments/verdicts/2026-07-16-tool-call-syntax-headroom-v1.md` and
  `2026-07-16-explicit-format-contract-headroom-v1.md`.
- Tool-semantic attribution:
  `experiments/manifests/tool-semantic-outcome-baseline-v1.yaml` and
  `experiments/verdicts/2026-07-16-tool-semantic-outcome-baseline-v1.md`.
- Current-master tool-semantic and literal-response follow-up:
  `experiments/verdicts/2026-07-17-current-master-tool-semantic-refresh-v1.md`,
  `2026-07-17-literal-response-contract-oracle-v1.md`, and
  `2026-07-18-literal-response-contract-candidate-v1.md` through
  `2026-07-18-literal-response-contract-candidate-v3.md`.
- Local-read evidence oracle and recovery sequence:
  `experiments/verdicts/2026-07-17-local-read-evidence-oracle-v1.md`,
  `2026-07-17-local-read-failure-recovery-v1.md` through
  `2026-07-17-local-read-failure-recovery-v3.md`, and
  `2026-07-17-local-read-route-agnostic-validation-v1.md`.
- Tool-integrated verification sequence:
  `experiments/verdicts/2026-07-16-tool-integrated-draft-verification-v1.md`
  through `2026-07-16-contract-verification-round-trace-v1.md`.
- Stateful Wiki sequence: the dated `everyday-state-*` manifests and verdicts
  under `experiments/`, ending with
  `2026-07-15-everyday-state-semantic-wiki-root-language-product-regressions-v12.md`.
- Wiki page rename sequence: `experiments/manifests/wiki-page-rename-selection-v1.yaml`
  through `wiki-guarded-root-label-resolution-v6.yaml`, ending with
  `experiments/verdicts/2026-07-16-wiki-guarded-root-label-resolution-v6.md`.
- Latency and reliability sequence:
  `experiments/verdicts/2026-07-16-helper-call-latency-cohort-v1.md`,
  `2026-07-16-explicit-memory-tool-budget-v1.md`,
  `2026-07-16-explicit-memory-precedence-v2.md`,
  `2026-07-17-explicit-memory-tool-budget-v3.md`,
  `2026-07-16-missing-attachment-clarification-v1.md`, and
  `2026-07-16-missing-attachment-request-clause-v2.md`, plus
  `2026-07-17-native-model-load-contract-v1.md` and
  `2026-07-17-native-model-load-current-baseline-eligibility-v2.md`.

## Update rule

Add a row only after the evaluator verdict is frozen. Use aggregate scores and
mechanism-level lessons; do not copy raw outputs or hidden expected values.
When a later correction supersedes a verdict, update the existing row rather
than leaving contradictory summaries.
