# Experiment Catalog

This is a compact index of material Sir Thaddeus experiments through July 30,
2026. It records reusable conclusions, not hidden benchmark content. Exact
manifests, commands, artifact hashes, and verdicts live in the sibling private
`local-benchmark-runner` repository under `experiments/`.

## Promoted or retained production behavior

| Campaign | Lane | Disposition | Reusable conclusion |
| --- | --- | --- | --- |
| Sealed 2026-S3 four-arm cross-model campaign | Harness capability | **Engineering lift supported; research claim inconclusive** | On a private human-reviewed 25-family/100-case bank, the full harness beat the identical production prompt without tools at case level for LFM 1.2B, LFM 8B-A1B, Gemma 4 26B-A4B, and Luna in both repeats. Gemma's strict-family lift repeated at `1/25 -> 12/25` and `1/25 -> 11/25`; formal publication remains blocked by evidence-field and autopsy gaps. |
| Current local date/time utility v1 | Harness capability and product quality | **Promoted through product PR `#271`** | Answer-blind unseen development coverage improved `5/30 -> 8/30` with mechanical false activations `1 -> 0`. Exact product repeat preserved six correct date outcomes, added two correct local-time outcomes, repaired one location-scoped wrong-clock route, and showed no paired behavior loss while calls fell `22 -> 8` and positive first-visible p50 improved `211.5 ms -> 5.5 ms`. Final public confirmation retained direction; its one mechanical false label was adjudicated as an eligible current-date command without changing or rerunning code, selector, scorer, threshold, or holdout. This is not MMLU. |
| Wiki-root default-location schema projection v3 | Harness capability | **Promoted through product PR `#261`** | On current master, the shipped deferral and non-action guards remove the sole v2 activation failure. Fresh development and exact repeat each improved `8/12 -> 10/12` with two wins and zero losses; disjoint validation improved `10/18 -> 14/18` with four wins, zero losses, `8/8` default writes, `6/6` deferred/non-action safety, full validity, correct activation, and calls reduced `60 -> 53`. The application owns its configured Wiki location while explicit custom locations retain the full schema. |
| Wiki-root temporal-deferral pruning | Harness capability and product safety | **Promoted through product PR `#248`** | A root-scoped deterministic policy reproduced `4/16 -> 11/16` in development and exact repeat with seven wins and zero losses. Disjoint validation improved `4/18 -> 13/18` with nine wins, zero losses, `10/10` deferred safety, `8/8` immediate root-tool reachability, full validity, and fewer calls. This improves mutation precision, not MMLU. |
| Wiki-root non-action tool pruning | Harness capability and product safety | **Promoted through product PR `#246`** | Reuse the shipped deterministic non-action policy to withhold only `wiki_root_create` while preserving read-only tools and upstream forced-tool decisions. Fresh v2 development and exact repeat each improved `7/12 -> 9/12` with two wins and zero losses; disjoint validation improved `10/16 -> 13/16` with three wins, zero losses, `8/8` non-action correctness, full validity, correct activation, and fewer model calls. This improves tool precision, not MMLU. |
| Typed Wiki write confirmation | Product safety | **Promoted through product PR `#244`** | Every `WikiWrite` call gets a fresh confirmation even after Session or Always grants. Development and exact repeat each preserved `6/6` no-action states versus `4/6`; disjoint validation preserved `6/6` versus `5/6`, with no paired authorized/read loss or extra model call. This is a permission gain, not a model-capacity gain. |
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
| Fresh capability-closure scorecard | Evaluation infrastructure | **Retained; existing capabilities confirmed** | On 20 newly authored tasks, raw scored `4/20`, unchanged Thaddeus `15/20`, and compact gold support `19/20`. Thaddeus captured 13 of 15 gold-supported positive wins; Wiki extraction and explicit calculator use each reached `4/4`. Reliability remained `19/20` valid with `2/4` exact no-tool controls, and no category met the frozen residual-gap rule, so no new mechanism was authorized. Evaluator PR `#93`. |
| Matched existing-capability ablation | Evaluation infrastructure | **Retained; reliability gate rejected** | With the same production pipeline in both arms, exposing existing tools and attachments improved `4/20` to `10/20`, with seven paired wins and one loss. Calculator work reached `4/4`; three broader capability categories reached only `1/4`, and one deferred no-action request changed state. The hard safety gate and eight-win floor rejected a new product candidate. Evaluator PR `#94`. |
| Fresh outcome discovery v2 | Evaluation infrastructure | **Diagnostic complete; no candidate** | A 24-task source-audited bank yielded a balanced 16-task triage at `7/16` strict and `11/16` valid. Computation and verified state change each scored `2/2`; local-file extraction scored `0/2`, then its preauthored reserve passed `1/1` with `file_read`. No three-case cluster remained, so no oracle or product mutation ran. One empty-state observation defect was repaired separately in product PR `#256`. |
| Fresh outcome discovery v3 | Evaluation infrastructure | **Diagnostic complete; no open candidate** | Two balanced 16-case invocations scored `19/32` strict and `31/32` valid with zero runtime errors. Computation scored `4/4`; local files, Wiki creation, and no-action safety each scored `3/4`. Instruction contracts and multi-source synthesis each failed `3/4`, but both belong to closed response-contract/evidence-synthesis families, so no oracle or product mutation ran. |
| Read-only state inventory v1 | Evaluation infrastructure | **Diagnostic complete; no open candidate** | After rejecting one batched artifact for evaluator-state leakage, process-isolated recovery scored `2/8` strict and `7/8` valid with zero runtime errors. File and Wiki inventory each passed `1/4`; failures mapped to closed path-binding, tool-commitment, typed-ID, and sequence families. No oracle or product mutation ran. |
| Current-behavior outcome census v1 | Evaluation infrastructure | **Completed; no open mechanism cluster** | A zero-model-call answer-blind census reused 57 compatible frozen outcomes and measured `29/57` strict, `50/57` valid, verified computation `6/6`, and verified state change `5/6`. Six numeric failure buckets were already closed or heterogeneous after manual attribution. This is coverage evidence, not prevalence, MMLU uplift, or the 80% claim. Evaluator PRs `#123` and `#124`. |
| Local document-reading outcome discovery v1 | Evaluation infrastructure | **Diagnostic complete; no open candidate** | A local-only 12-case CSV/RTF cohort scored `8/12` strict and `11/12` valid in 86.854 seconds. Field extraction and row selection each reached `3/4`; aggregation reached `2/4`. Ten reads returned usable evidence, but misses split across semantic binding, tool commitment, path binding, and incomplete arithmetic. No category met the frozen oracle gate, so no product code ran. Evaluator PR `#129`. |
| Native document-reading outcome discovery v1 | Evaluation infrastructure | **Diagnostic complete; no open candidate** | Bounded binary harness fixtures enabled one 12-case PDF/DOCX/XLSX cohort after all files passed the production readers. Unchanged Thaddeus scored `6/12` strict and `10/12` valid in 83.035 seconds. Field/row/aggregation reached `3/4`, `2/4`, and `1/4`; the low `1/4` XLSX slice mixed two invalid path mutations with one semantic column error and was not a post-hoc gate. No category supplied three aligned valid failures, so no oracle or product behavior ran. Product PR `#263`; evaluator PR `#130`. |
| XLSX column-fidelity headroom v1 | Evaluation infrastructure | **Rejected at baseline gate; no gold or product candidate** | Static inspection found that omitted XLSX cells can be collapsed because the reader ignores cell references. A fresh six-sparse/four-dense run nevertheless produced `0/10` strict, `7/10` valid, and zero successful reads. Nine turns skipped the sole exposed tool and one changed the path. With zero coordinate-loss activations, the conditional gold arm did not run and the one-off evaluator code was removed. Evaluator PR `#133`. |
| System-command outcome discovery v1 | Evaluation infrastructure | **Rejected at baseline gate; no product candidate** | A local-only six-positive/four-control run scored `6/10` strict and `10/10` valid in 36.24 seconds. All safety/no-action controls passed; `system_execute` was selected on `5/6` authorized cases and all five calls succeeded. Only one miss was tool-name selection; the others split across wrong command arguments and final-response fidelity. A forced-tool candidate failed its authorization gate. Evaluator PR `#134`. |
| System-command binding oracle v1 | Evaluation infrastructure | **Rejected at prerequisite; command-binding family closed** | A fresh 30-evaluation screen compared unchanged, tool-name-guided, and gold-command arms. Unchanged selected `system_execute` on only `2/6` positives versus the frozen `5/6` prerequisite; all three arms scored `0/6` strict positives while all four controls passed in every arm. Gold commands therefore did not isolate argument binding, and no product candidate, repeat, or validation was authorized. |
| Deterministic date arithmetic v1 | Harness capability | **Rejected at development gate; implementation deleted** | Compact gold evidence scored `8/8` versus `1/8` raw and `0/8` unchanged across four date-operation families. The real read-only `date_calculate` candidate then scored `2/8` positives and `4/4` controls with full validity. Both selected positive calls succeeded, but selection coverage was only `2/8` versus the frozen `7/8` gate. No repeat or validation ran; evaluator PR `#135` preserves the evidence. |
| Deterministic date first-tool selection v2 | Harness capability | **Stopped at prerequisite; no product code** | A fresh disjoint bank put raw and unchanged at `0/8`. Tool-only v1 reached `3/8` positives, `3/6` controls, full validity, and `6/8` positive selection. Only two misses were omissions while three selected calls had wrong operation/argument binding, so selection could not meet its frozen +3 causal gate. Evaluator PR `#136`. |
| Typed date-argument oracle v3 | Harness capability | **Rejected at baseline; date family closed** | A third disjoint tool-only bank scored `1/6` positives and `3/6` controls with full validity. Positive selection was `4/6`, below the frozen `5/6` binding-dominance prerequisite; three calls had wrong arguments while two recurrences omitted the tool. Gold and product code did not run. Evaluator PR `#137`. |

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
| Scoped additive text-file creation v1-v2 | **Rejected; schema-only family closed** | V1 safely selected the additive tool but produced `0/5` exact authorized artifacts. V2 required path components, line elements, and an explicit trailing-newline boolean; it preserved `5/5` safety outcomes but reached only `1/5` authorized exact, below the frozen `4/5` gate. Remaining failures were semantic path/content binding and one missing call, not execution or permission defects. Both product branches were deleted; evaluator PRs `#131` and `#132`. |
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
| Wiki-root default-location schema projection v2 | **Rejected historically; superseded by fresh v3 recovery** | Development and exact repeat each improved `10/16 -> 14/16`; disjoint validation improved `10/22 -> 17/22` with seven wins and zero losses while reducing calls. One deferred non-action activated the mutation projection, failing the hard activation-validity gate at `21/22`. The consumed prompt was not reused; v3 was evaluated only after independent deferral and non-action policies shipped. |
| Wiki rename selection v1-v2 | **Rejected after downstream safety test** | Deterministic selection exposed real rename capability but did not solve root-label mismatch or authorization. The temporary control branches were removed after the combined campaign verdict. |
| Decorated root-label resolver v3 | **Rejected on reliability** | Authorized outcomes improved materially, but permissive label resolution also made non-action rename attempts succeed. |
| Rename tool pruning v4 | **Rejected on resources** | Classification worked, but pruning caused a hard p95 regression. |
| Pre-MCP rename execution guard v5 | **Inconclusive, then retired** | The guard blocked attempted writes but showed no final-state uplift while the exact-only tool already failed closed. It was retained only to test guarded label tolerance. |
| Guarded decorated root-label resolver v6 | **Rejected; mechanism family closed** | Authorized state improved `1/8` to `6/8` with five wins and zero losses, but two of eight non-actions became unauthorized writes, safety scored `5/6`, and no-action p95 was `1.586x` v5. All temporary Wiki rename branches were removed. |

## Product latency and reliability campaign

| Experiment | Disposition | Result and lesson |
| --- | --- | --- |
| Exact-identity completion-repair termination v1 | **Promoted** | Paired development, exact repeat, and disjoint validation preserved all public outcomes and changed-repair decisions while provider calls fell `124 -> 119`. Three savings were directly case-matched; stochastic differences are not attributed to the mechanism. Shipped in product PR `#250`. |
| Helper-call activation and latency cohort v1 | **Diagnostic accepted; no router candidate** | Across 30 repeated turns, Footman used zero LLM calls and optional helpers were 10% of ordinary-conversation end-to-end time. Prompt construction was negligible. The actionable finding was a repeated 16K-runtime versus 8K-provider context overflow, not routing overhead. |
| Explicit memory tool budget v1 | **Inconclusive retained research** | Removed the repeated overflow, exposed 9 instead of 60 tools, and produced one paired win with zero losses. Fresh validation reached `5/6` intended and `0/8` false activations, so the unmerged conservative branch is not promoted. |
| Explicit memory precedence v2 | **Rejected; precedence family closed** | Early results repeated the win, but fresh validation routed four of ten browse, file, research, or system actions to memory. Simple precedence is unsafe for overlapping intent. |
| Conservative explicit memory tool budget v3 | **Rejected on call cost** | Fresh current-master development activated `3/4` recalls and `0/6` negatives, removed two permission prompts, and tied public outcomes `8/10`, but provider calls increased from 45 to 50. No repeat or validation ran; the product branch was deleted. |
| Prompt-envelope attribution v1 | **Diagnostic accepted** | Two exact 8K repeats attributed the explicit-memory overflow to the advertised capability surface: 60 tool definitions contributed an estimated 8,455 tokens, producing a 12,097-token request budget with output reserve. The diagnostic changed neither request bytes nor behavior. |
| Memory capability-surface oracle v1 | **Oracle passed; existing selector family remains closed** | Two evaluation-only repeats with only the required read capability reduced the request budget to 3,890 tokens, left 4,302 tokens of headroom, and passed the empty-memory contract. This proves headroom, not safe selection; v2 safety and v3 call-cost evidence still control. |
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
- Fresh capability-closure scorecard:
  `experiments/verdicts/2026-07-20-capability-closure-scorecard-v1.md`.
- Matched existing-capability ablation:
  `experiments/verdicts/2026-07-20-existing-capability-ablation-v1.md`.
- Fresh outcome discovery v2:
  `experiments/verdicts/2026-07-20-fresh-outcome-discovery-v2.md`.
- Fresh outcome discovery v3 and the public benchmark fit audit:
  `experiments/verdicts/2026-07-20-fresh-outcome-discovery-v3.md` and
  `experiments/verdicts/2026-07-20-public-stateful-benchmark-fit-audit-v1.md`.
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
- Scoped additive text-file creation:
  `experiments/verdicts/2026-07-20-scoped-text-file-create-v1.md` and
  `experiments/verdicts/2026-07-20-scoped-text-file-create-structured-v2.md`.
- XLSX column-fidelity headroom:
  `experiments/manifests/xlsx-column-fidelity-headroom-v1.yaml` and
  `experiments/verdicts/2026-07-21-xlsx-column-fidelity-headroom-v1.md`.
- System-command outcome discovery:
  `experiments/manifests/system-command-outcome-discovery-v1.yaml` and
  `experiments/verdicts/2026-07-21-system-command-outcome-discovery-v1.md`.
- System-command binding oracle:
  `experiments/manifests/system-command-binding-oracle-v1.yaml` and
  `experiments/verdicts/2026-07-30-system-command-binding-oracle-v1.md`.
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
  `2026-07-17-native-model-load-current-baseline-eligibility-v2.md`, followed
  by `2026-07-20-repair-outcome-attribution-v1.md` and
  `2026-07-20-identical-repair-revalidation-skip-v1.md`.

## Update rule

Add a row only after the evaluator verdict is frozen. Use aggregate scores and
mechanism-level lessons; do not copy raw outputs or hidden expected values.
When a later correction supersedes a verdict, update the existing row rather
than leaving contradictory summaries.
