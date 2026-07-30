# Current Evidence

**Evidence cutoff:** July 22, 2026

**Production baseline before the current promotion:** `a1eaab94`; empty-state Wiki observation coverage was
repaired through product PR `#256`, AngleSharp was pinned to `1.5.2` through
security PR `#259`, the July outcome evidence was reconciled in PR `#260`, and
the Wiki default-location projection was promoted through PR `#261`; native
document evaluation support landed through PR `#263` and its evidence through
PR `#264`, structured file-creation findings through PR `#265`, and the date
specialization family was closed through PRs `#268`-`#270`
**Authoritative ledger:** sibling `local-benchmark-runner` repository

## Executive read

The foundation is working. Sir Thaddeus has a shared, bounded assistant
pipeline; permissioned tools; local state; memory and continuity; strict
evaluation infrastructure; and short-lived experiment branches. The strongest
demonstrated gains come from making a narrowly identified operation easier and
verifying the final outcome.

The evidence does not support a broad claim that prompt scaffolds, extra
reasoning calls, routing, or a larger comparison model reliably improve the
closed-book capacity of a fixed small model. Those mechanisms have often added
calls and latency while tying or losing to unchanged Thaddeus.

## What is working

### Sealed cross-model harness evidence

- A private, human-reviewed 100-case bank grouped into 25 semantic families
  compared raw, identical production prompt without tools, direct tools, and
  the unchanged full harness across LFM 1.2B, LFM 8B-A1B, Gemma 4 26B-A4B, and
  hosted Luna. Every accepted arm was repeated exactly.
- Full-harness case completion exceeded the same-prompt no-tools control for
  every model in both repeats. Gemma also produced a statistically clear
  strict-family lift in both repeats: `1/25 -> 12/25` and `1/25 -> 11/25`.
- The result is strong engineering evidence, not a closed-book capacity claim
  or publication-ready proof. Missing initial-state preflight telemetry,
  incomplete semantic fallback judgments and failure autopsies, two recurring
  Luna direct-tools setup failures, and Luna run-to-run drift remain explicit
  limitations. See [SEALED_2026S3_HARNESS_EVIDENCE.md](SEALED_2026S3_HARNESS_EVIDENCE.md).

### Production and evaluation foundation

- Desktop and headless execution share the ordered pipeline responsibilities,
  permission boundary, and bounded tool loop.
- Raw minimal, same-prompt direct, unchanged harness, and candidate arms can be
  compared with the same model and inference configuration.
- Development, exact-repeat, disjoint-validation, product-regression, and
  resource gates have prevented attractive but non-causal results from being
  promoted.
- Harness-only evidence capture, final-state Wiki observations, managed-search
  parity, and tool-call intake checks are useful evaluation infrastructure.
- Exact duplicate pure-compute calls can be memoized conservatively within a
  turn, and repeated harness campaigns can build once rather than rebuilding
  every invocation.
- Phase 1 causal evaluation infrastructure now exports a sanitized v2
  `diagnostics.json`, proves full production composition, separates stage and
  provider timing, and requires explicit candidate activation expectations
  before correctness can be interpreted. The one-item live proof observed the
  full composition and 34 allowlisted events. This is measurement
  infrastructure, not evidence that assistant capability improved.
- The v2 harness can arrange and observe evaluator-owned files under a
  traversal-safe disposable root, alongside existing Wiki final-state checks.
  The frozen local outcome battery covers tool aliases, irrelevant-tool
  negatives, local evidence, Wiki/file state, reasoning, and response contracts.
- The harness redirect now has a clean 32-task verified-outcome development
  baseline for the frozen 1.2B model. Raw and unchanged Thaddeus each completed
  `9/32`; same-prompt direct completed `6/32`. The evaluator applies the same
  required-tool, forbidden-tool, and observed-state contract to every arm, so a
  direct answer cannot receive completion credit for guessing a tool outcome.
- A source-audited representative-task layer now derives newly authored local
  fixtures from OASST1, Dolly-15K, IFEval, BFCL, and tau-bench capability
  taxonomies without copying public prompts or answers. Its first ten-task
  diagnostic scored `3/10` raw and `4/10` unchanged Thaddeus, with two paired
  harness wins, one loss, and one unsuitable open-ended exact scorer. This is
  task-distribution and evaluator evidence, not a population or capability
  claim.
- A fresh six-category gold-headroom screen then scored `2/12` raw and `9/12`
  with compact scorer-blind support: seven paired wins, zero losses, and full
  validity. Direct extraction and verified arithmetic-result use each cleared
  `2/2`; classification was already `2/2` raw. Evidence synthesis, tool-result
  interpretation, and state decisions each retained one genuine supported
  failure, so only two categories met the frozen rule and the three-category
  breadth gate authorized no product candidate.
- A follow-on fresh 20-task capability-closure scorecard scored `4/20` raw,
  `15/20` unchanged Thaddeus, and `19/20` with compact gold support. On 16
  positive extraction and compute tasks, the harness captured 13 of the 15 net
  wins exposed by gold support. Attached Wiki extraction and explicit
  calculator expressions each reached `4/4`; file extraction reached `2/4`
  and word-to-expression computation reached `3/4`. The frozen reliability
  gate still failed because validity was `19/20` and exact no-tool controls
  were `2/4`, although no forbidden tool was called. No category had the
  required three-outcome residual gold gap, so the result confirms existing
  shipped value but selects no new product mechanism. Evaluator PR `#93`.
- A harder matched capability ablation then held the production prompt and
  pipeline constant while withholding versus exposing evaluator-declared tools
  and attachments. Outcomes improved from `4/20` to `10/20`, with seven paired
  wins and one loss. Explicit calculator work reached `4/4`, while attached
  evidence, local reads, and verified Wiki state changes each reached `1/4`
  strict. One deferred no-action request executed `wiki_root_create`, so the
  hard state-safety gate failed at `3/4`; the frozen eight-win gate also missed
  by one. This bounds the earlier gain without invalidating it: existing
  capabilities add net value, but broader strict reliability remains uneven.
  No product candidate was authorized. Evaluator PR `#94`.

### Narrow harness gains

- A bounded current-local-date/time utility passed train screening, exact
  product comparison, repeat, and disjoint answer-blind validation without a
  model or tool change. Unseen development coverage improved `5/30 -> 8/30`
  with mechanical false activations reduced `1 -> 0`. Product comparison
  repeated two genuine local-time correctness gains, six current-date
  correctness ties with zero provider calls, and one repair of a harmful
  location-scoped wrong-clock route with no observed paired loss. On repeat,
  positive first-visible p50/p95 improved `211.5/260.5 ms -> 5.5/10.3 ms`,
  model calls fell `22 -> 8`, and peak VRAM was unchanged. A final public
  confirmation retained direction; its sole mechanical false label was a
  direct current-date command omitted by the evaluator selector and was
  preserved as a disclosed adjudication rather than silently rescored. This is
  a narrow harness-capability and product-quality result, not MMLU or a general
  language router. Promoted through product PR `#271`.
- Deterministic Wiki-root temporal-deferral pruning reproduced exactly on a
  fresh 16-case development slice, improving `4/16` unchanged to `11/16` with
  seven paired wins and zero losses. Disjoint validation improved `4/18` to
  `13/18` with nine wins, zero losses, `10/10` deferred state preservation,
  `8/8` immediate-control root-tool reachability, full validity, and model calls
  reduced from 55 to 48. The candidate recognizes generalized future-date,
  conditional-approval, post-event, and explicit-not-now language while
  preserving immediate temporal distractors. This is tool precision and
  permission-interruption reduction, not model capacity. Promoted through
  product PR `#248`.
- Deterministic Wiki-root non-action tool pruning reproduced on three fresh
  slices without changing the model or positive selector. V1 development and
  repeat each improved `6/12` to `9/12` with three wins and zero losses. A
  corrected, disjoint v2 development slice and exact repeat each improved
  `7/12` to `9/12` with two wins and zero losses; validation improved `10/16`
  to `13/16` with three wins, zero losses, `8/8` candidate non-action outcomes,
  full validity, correct activation, and model calls reduced from 49 to 44.
  Authorized-write outcomes were identical in every comparison. The mechanism
  withholds only `wiki_root_create` when the existing policy already classifies
  the turn as informational, hypothetical, negated, or deferred. This is a
  tool-precision and permission-interruption gain, not model capacity.
- The original application-owned default-location projection remains consumed
  historical evidence: it improved `10/16 -> 14/16` twice and validation
  `10/22 -> 17/22`, but one deferred request activated the projection and
  failed its hard validity gate. After the independently promoted deferral and
  non-action guards shipped, a fresh current-master recovery reproduced
  `8/12 -> 10/12` in development and exact repeat with the same two wins and
  zero losses. Disjoint validation improved `10/18 -> 14/18` with four wins,
  zero losses, `8/8` default-location writes, `6/6` deferred/non-action state
  preservation, full validity, correct `8` intended and `10` inactive
  activations, and model calls reduced `60 -> 53`. Promoted through protected
  product PR `#261` as argument-ownership reliability, not MMLU or model
  capacity.
- A typed-capability Wiki confirmation candidate prevented two unauthorized
  root creations in development and exact repeat (`6/6` no-action states versus
  `4/6` unchanged), then prevented one further mutation on a disjoint validation
  slice (`6/6` versus `5/6`). It preserved all paired authorized and read-only
  outcomes, used identical model-call counts, and stayed within the `1.25x` p95
  guardrail. Validation also showed the 1.2B model still fails some authorized
  writes for argument and selection reasons; the permission boundary improves
  safety, not model capacity.
- Explicit Wiki-root creation is the clearest promoted capability result. The
  expanded semantic selector scored `14/16` twice on unseen validation versus
  `9/16` for its frozen parent, with five paired wins, zero paired losses, full
  validity, and acceptable outcome-normalized resources.
- By-name Wiki contracts reduce opaque identifier and version bookkeeping while
  preserving permissions, revisions, ambiguity checks, and concurrency rules.
- Calculator and Python tools improve tasks when the model forms the correct
  expression or program. Tool execution is a real capability; it is not proof
  of better closed-book reasoning.
- Completion validation and bounded retry remain net-positive globally.
  Experiments that removed them broadly reduced quality.
- A narrow answer-only successful-tool-evidence projection improved a disjoint
  16-item local file/Wiki validation slice from `8/16` unchanged to `12/16`,
  with four paired wins, zero losses, `16/16` validity, and zero activations on
  eight negative contracts. Development and exact repeat were both `10/12`
  versus `5/12`. The projection uses no new model or tool call and returns
  before completion validation only when one unique verbatim scalar is proved
  by both the draft and successful tool evidence. This is harness capability,
  not closed-book model improvement.
- Fail-closed unique local-file suffix resolution improved a fresh disjoint
  16-case validation slice from `5/16` unchanged to `12/16` twice, with seven
  paired wins, zero losses, `16/16` validity, and zero negative activations.
  Candidate calls fell from 48 to 39 and tokens from 39,074 to 37,102; p50 and
  p95 were lower in both orders. A 10,000-file synthetic root kept unique,
  missing, and ambiguous resolution below 8 ms on the validation machine. The
  resolver stays inside allowed roots, skips reparse points, and never applies
  to writes. This is product capability and path reliability, not model
  knowledge or MMLU improvement.
- Gold or supplied document evidence produced a large DROP headroom signal:
  the tested model scored `5-6/10` with the passage and `0/10` without it. This
  justifies testing evidence retrieval and packaging, not assuming the current
  retriever is sufficient.
- Compact, completion-signaled search evidence plus a deterministic
  answer-evidence postcondition repeatedly reached `6/6` on development and
  unseen validation, versus `1/6` unchanged Thaddeus and `0/6` raw on the unseen
  slice. This is the strongest conditional evidence-use signal, but it remains
  unmerged research because free-provider retrieval and resource savings were
  not stable after a machine crash.

### Useful negative and diagnostic results

- A scorer-blind declared-capability route combined the raw no-capability arm
  with unchanged Thaddeus on capability-required tasks and reached `13/32`
  twice, four outcomes above both controls. It is an evaluator-only ceiling:
  runtime code cannot read evaluator capability declarations, and the raw arm
  omits product identity, personality, memory, continuity, and safety context.
- Replacing raw with a compact benchmark-agnostic Sir Thaddeus identity and
  safety prompt reduced that composite to `11/32`, only two above both
  controls, below the frozen `12/32` and `+3` gate. A second oracle supplied
  verified structured tool-failure evidence under the production prompt and
  scored `0/3`. No routing or failure-presentation product candidate was
  created. The result bounds these mechanisms without weakening their gates.

- The stabilized same-model MMLU rerun found no reproducible harness uplift.
  With the exact LFM 1.2B Q4_K_M artifact loaded at context 8192 and parallel
  one, raw, same-prompt direct, unchanged current Thaddeus, and the historical
  product SHA each scored `10/20`. The saved `13/20` repeats remain consumed
  historical observations, but they did not reproduce under the frozen runtime
  and are not a current harness-capability claim. MMLU is now capacity-only.
- The 50-item general-capability battery is a useful fast portfolio. On the 8B
  diagnostic run, raw scored `37/50` and unchanged harness `36/50`, so it did
  not support a general routing or prompt-expansion candidate.
- A contamination-audited native-checkpoint QLoRA smoke completed in 89.25
  seconds on the RTX 4090. Loss fell from `6.8134` to `2.2826`, 884,736 LoRA
  parameters trained, and the saved adapter reloaded consistently. It remained
  `0/4` exact on its four held-in nonce mappings, below the frozen `3/4` gate,
  so the smoke is rejected. This proves the local training/save/reload plumbing
  is usable; it is not model-capacity or benchmark evidence.
- A later behavior-preserving science adapter produced a reproducible generated
  signal but no attributable knowledge gain. It moved fresh OpenBookQA from
  `15/40` to `19/40` with nine wins and five losses, then moved generated,
  parse-correct MMLU-Pro science from `0/30` to `4/30` twice. A frozen
  answer-content likelihood control removed labels, generation, and parsing;
  native base and adapter both selected `8/30` correct option texts with zero
  paired wins or losses. The adapter changed four wrong selections into other
  wrong selections. Treat this as response-contract learning evidence, not a
  capacity improvement; its separate mixed-capability regression remains
  disqualifying.
- A subsequent matched rationale-distillation control produced the first
  directional format-independent capacity signal, but not promotion evidence.
  Native base and an answer-only adapter each scored `8/30`; a full-support
  SciQ rationale adapter scored `10/30` with three paired wins and one loss.
  It missed the frozen `+3/30` gate, did not improve the already-high `7/8`
  held-in activation score, and reduced option-rotation invariance from `5/6`
  to `4/6`. The research PR was closed during redirect retirement and its
  immutable branch history was archived; no adapter or runtime behavior was
  merged.
- Concise-evidence rationale v2 then compressed 55/64 SciQ supports by a median
  74.65% using an answer-blind deterministic sentence selector. The mechanism
  activated on all eight held-in score vectors and restored `5/6` rotation
  invariance, but scored only `9/30`: `+1` over base/answer-only and `-1` versus
  full-support v1. The adapter and implementation were deleted. This was the
  third consecutive valid candidate rejection, so further MMLU candidate runs
  are paused for recalibration rather than another rationale-format mutation.
- Phase A then froze fresh, answer-free learning-capacity instruments and caught
  an incomplete-support selection defect before any model call. The corrected
  pre-training SciQ oracle improved the unchanged 1.2B model from `13/30` to
  `17/30` with gold human support, with four wins and zero losses, but missed
  the immutable `18/30`, `+6/30`, and eight-win prerequisites. No teacher,
  adapter training, MMLU-Pro development, repeat, or validation run followed.
- LFM 1.2B and Qwen 2B each produced `8/8` parsed, schema-valid forced tool
  calls through LM Studio. There is no current headroom for a content-recovery
  parser; the test remains useful for model intake.
- Provider-native JSON-Schema decoding is feasible through the supported LM
  Studio endpoint, but the clean natural-contract comparison found no product
  headroom: unconstrained and constrained LFM 1.2B arms were both `10/10`
  schema-valid and `9/10` semantically exact. The shared miss was a wrong but
  schema-valid extracted string. Keep the evaluator diagnostic; do not add
  `response_format` to production until a real contract shows at least three
  reproducible structural failures.
- A separate additive text-file capability then tested whether a more semantic
  schema could improve exact artifacts rather than JSON validity. V1 selected
  the new tool on all five authorized requests and safely handled all five
  negative or refusal outcomes, but produced `0/5` exact authorized files. V2
  replaced free-form path and content strings with required path components,
  line elements, and a trailing-newline boolean. It remained `5/5` safe and
  produced one exact artifact, but failed the frozen gate at `1/5` authorized
  exact, with one omitted extension, one over-segmented filename, one embedded-
  newline rejection, and one missing call. Schema shape did not solve semantic
  argument binding for the fixed 1.2B model. Both product implementations were
  deleted; evaluator PRs `#131` and `#132` preserve the evidence.
- On the original fresh 16-item local tool-semantic baseline, unchanged
  full-menu Thaddeus scored `7/16` versus `3/16` with oracle-pruned tools,
  `1/16` no-tools, and `3/16` raw. After the promoted answer-only evidence
  projection, a current-master refresh improved unchanged Thaddeus to `9/16`
  with `16/16` validity, `7/10` positive outcomes, `2/6` no-tool outcomes,
  44 model calls, and 67,704 tokens. A fresh same-prompt direct arm scored
  `1/16` and raw remained `3/16`. The harness gain is local-outcome evidence,
  not model-capacity evidence.
- An answer-blind offline literal-response oracle raised that current-master
  artifact from `9/16` to `12/16` twice with three wins and no losses. Three
  product candidates then remained `9/16` with zero activations. V3 used 44
  calls and 67,408 tokens, tied unchanged with zero paired wins/losses, and
  failed before repeat. The disjoint validation set remains unconsumed and the
  mechanism family is closed; oracle headroom alone did not prove a reachable
  product seam.
- A separate gold-evidence control then authorized one-shot local-read recovery.
  V3 reached a fresh route-agnostic result of `7/12` versus `3/12` unchanged,
  with four verified positive wins, zero paired losses, lower p95 latency
  (4,287 ms versus 4,611 ms), and lower peak VRAM. It also activated on the
  exact-missing-file negative, so validity fell to `11/12`. The frozen gate
  rejected it before exact repeat; all product implementations were deleted.
- Managed SearXNG restored answer-bearing search evidence and reduced the
  degraded evaluator path's calls and latency. That was environment parity,
  not a product capability promotion.
- A repeated 30-turn helper-latency cohort found no LLM Footman calls and only
  10% optional-helper share of ordinary-conversation end-to-end time. Prompt
  construction was negligible. This is evidence against a broad latency
  router refactor, not evidence that latency is already ideal.
- A conservative explicit-memory tool budget removed a repeated context
  overflow and produced one paired win with zero losses. It remains unmerged
  research because fresh semantic validation activated only `5/6` intended
  recalls. A precedence follow-up was rejected after routing four of ten fresh
  action controls to memory.
- A native model-load contract reliably created a 16,384-token LM Studio
  instance and removed the 8K/16K overflow, but it introduced an irrelevant
  permission prompt and repeated at `1.258x` p95 versus the `1.25x` gate. A
  separate prepermission no-op candidate made correctness, prompts, calls, and
  latency worse. Both remain unmerged evidence; reliability is not promoted.
- A current-master eligibility check held the same production SHA at a verified
  16,384-token context for two identical three-turn runs. Both completed `3/3`,
  passed `2/3` public contracts, used 13 provider calls, and raised the same
  irrelevant `memory_store_facts` permission prompt. Native lifecycle v2 was
  rejected before implementation; the exact original 8,192-token provider
  configuration was restored.
- The original conservative memory-read tool budget was then replayed unchanged
  on current master with fresh inputs. It activated on `3/4` recalls and `0/6`
  negatives, removed two irrelevant permission prompts, tied public outcomes at
  `8/10`, and reduced observed p95. It also increased provider calls from 45 to
  50, entirely on intended recalls, so the frozen call gate rejected it before
  repeat. The temporary product branch was deleted.
- Content-free prompt-envelope attribution then reproduced the explicit-memory
  overflow twice at the exact frozen 8,192-token provider context. The main
  request contained an estimated 1,594 message tokens and 8,455 tool-definition
  tokens; with the 2,048-token output reserve, its estimated request budget was
  12,097 tokens. The advertised 60-tool surface, not prompt construction or a
  preliminary reasoning call, was the dominant overage.
- An evaluation-only one-capability oracle repeated twice with the same model,
  prompt, state, and settings. Exposing only the required read tool reduced the
  estimated request budget to 3,890 tokens, left 4,302 tokens of headroom, and
  satisfied the truthful empty-memory contract both times. This proves causal
  headroom, but it does not reopen the conservative selector family: precedence
  v2 was unsafe and current-master v3 increased provider calls. No behavior was
  shipped from the diagnostic.
- Content-free repair attribution then reproduced seven completion-repair
  attempts across two 15-turn cohorts: five generations were identical to
  their input and two changed generations were adopted. An exact ordinal
  identity termination was promoted after paired development, exact repeat,
  and disjoint 12-turn semantic validation preserved every public outcome and
  every changed-repair adoption or rejection. Aggregate provider calls fell
  `124 -> 119`; three savings were directly case-matched, while two additional
  differences remain labeled stochastic helper activation rather than causal
  savings. Product PR `#250` shipped the narrow invariant.

## What is not working

### Closed-book prompt and inference scaffolds

- Finite-choice contract detection, a closed-book choice scaffold, and a
  capability-scoped choice prompt all failed the frozen MMLU development gate.
- A compact arithmetic Plan-and-Solve scaffold produced no strict gain.
- Sampled self-consistency and tool-aware majority voting added substantial
  inference cost and did not beat unchanged controls.
- Blind regeneration, universal planning, and same-model self-critique have no
  demonstrated default-path value.

### Learning-based capacity attempts

- The frozen held-in smoke did not establish even the deliberately narrow
  memorization signal required before a real adaptation campaign. Falling loss,
  code-shaped outputs, and a reloadable adapter do not replace exact outcomes.
- The rejected adapter is not deployed. More steps, different targets, new
  data, or altered LoRA settings would be a new experiment; do not tune this
  smoke repeatedly until it passes.
- Behavior-preserving logit distillation improved final-answer compliance but
  regressed the frozen mixed-capability guardrail and did not improve
  format-independent science answer selection. Do not infer knowledge from a
  generated strict-score gain until an answer-content or equivalent
  format-independent control confirms it.

### Forced tool use

- Forced calculator routing scored `3/10` against raw GSM1k at `8/10`, with
  zero paired wins and five losses. The model's translation from the word
  problem to an expression was the bottleneck.
- Tool-integrated draft and contract verification candidates made zero
  executable Python calls. One apparent repeated accuracy gain was later shown
  to be misattributed because the proposed mechanism never activated.
- Shortening the verifier output budget and changing the follow-up message role
  did not recover the mechanism.

### Search routing and evidence framing

- Unchanged Thaddeus already searched every tested implicit recent-fact case;
  another deterministic search route added no net strict gain.
- Response-contract framing produced a small non-promotable signal with paired
  loss and higher calls, tokens, and latency.
- Removing model-visible source metadata cut tokens but encouraged repeated
  searches, tied correctness, and substantially worsened latency.
- Compact evidence packaging recovered correctness but increased calls and
  tokens in its first revision. Its deterministic short-answer follow-up kept
  correctness and sometimes removed validator calls, but post-crash resource
  behavior varied and SearXNG was not reproducible enough for promotion. The
  combined branch remains explicitly in the maybe bin, not production.
- SearXNG can restore retrieval parity, but a local benchmark cannot assume
  public web providers will remain reliable. Search-dependent product claims
  need provider-health reporting or a deterministic local corpus.

### Broad state prefetch and repair

- Read-before-write improved Wiki exact state from `2/6` to `5/6`, but p95
  latency rose to 2.49 times the unchanged path and failed the hard gate.
- Deterministic Wiki prefetch showed useful state-inspection signal but missed
  the correctness and zero-loss gates and used more calls and tokens.
- Broad or high-confidence write selection, generic side-effect repair, and
  adjacent prefetch variants did not survive repeatability, accuracy, or
  resource gates.
- One-shot audited local-read recovery converted four fresh file/Wiki failures
  into verified outcomes without a latency penalty, but could not distinguish
  a recoverable bad argument from a genuinely missing exact resource. Its one
  negative activation failed the 12/12 validity boundary. Do not tune another
  lexical recovery detector against that consumed suite.

### Wiki page rename routing and label tolerance

- Deterministic rename selection established that the small model can use a
  by-name mutation contract, but exact root-label mismatches remained a common
  failure.
- Rename-only decorated-root resolution improved fresh authorized outcomes
  from `1/8` to `6/8`, with five paired wins, zero losses, fewer calls, and
  lower positive p95. This is real capability headroom, not a promotable result.
- The pre-execution authorization guard activated on only `3/8` fresh
  non-actions. When combined with decorated-label resolution, a deferred
  request and a capability question performed unauthorized writes. Exact
  non-action state fell from `8/8` under the guarded exact-only control to
  `6/8` under the combined candidate.
- The combined candidate also missed the frozen resolver-safety and no-action
  latency gates. The full Wiki rename mechanism family is closed: do not add
  another lexical classifier, label qualifier, or prompt patch without a
  materially different authorization design and new oracle evidence.

### Language-inferred latency fast paths

- A bag-of-words missing-attachment clarification repeatedly removed 19 model
  calls across five intended turns and reduced p95 end-to-end latency from
  roughly 2.1 seconds to 0.8 seconds. Fresh validation falsely replaced a
  reminder and an email-drafting request, so it was rejected.
- Requiring a top-level request clause fixed those two failures and repeated
  positive p95 of 0.9 seconds versus 2.5 seconds unchanged. Fresh validation
  still missed two of seven legitimate variants and falsely intercepted two
  of twelve negatives, including pasted content and an `s3://` location.
- The attachment-regex family is closed. Its speed signal does not outweigh
  incorrect intent interception. A future path needs structured runtime/UI
  attachment state or an explicit user action.

### Fresh outcome discovery v2

- A source-audited bank of 24 newly authored tasks covered eight ordinary-work
  categories. Sixteen balanced triage tasks were executed, with one frozen
  local-file reserve used only after that category produced two misses.
- Unchanged Thaddeus scored `7/16` strict and `11/16` valid. Verified
  computation and verified state change each scored `2/2`; local-file
  extraction scored `0/2` because neither triage task reached `file_read`.
- The preauthored local-file reserve then passed `1/1`, including a successful
  `file_read`. The apparent two-case cluster therefore did not reproduce as the
  required three-case failure region, and no oracle or product candidate was
  authorized.
- One no-action case exposed an evaluator-observation defect rather than a
  model failure: an explicitly requested Wiki observation with no roots was
  treated as absent. Product PR `#256` now records an explicit empty Wiki state
  while preserving omitted and named-scope behavior.
- The staged screen used 17 model-evaluated cases and about 90 seconds of hot
  model time. It demonstrates that cheap answer-blind discovery is practical,
  but it does not establish prevalence weights or a new capability claim.

### Fresh outcome discovery v3

- A second source-audited bank froze 32 newly authored tasks across the same
  eight practical categories, informed by public stateful-agent and knowledge-
  work taxonomies without copying their prompts, fixtures, or answers.
- Two separate 16-case invocations scored `19/32` strict and `31/32` valid with
  zero runtime errors. First-token p50 was 311 ms, end-to-end p50 was 1,346 ms,
  and peak VRAM was 3,099 MB.
- Verified computation passed `4/4`; local-file extraction, verified Wiki
  creation, and no-action state safety each passed `3/4`.
- Instruction contracts and multi-source evidence synthesis each failed `3/4`,
  satisfying the numeric cluster count but not the open-mechanism gate. Both
  map to response-contract and evidence-synthesis families already closed by
  repeated activation, safety, or resource evidence.
- No raw, direct, gold, oracle, candidate, repeat, or validation arm ran. The
  result narrows the boundary honestly instead of using low scores to justify
  another prompt patch or benchmark-derived renderer.

### Read-only inventory and cross-cohort census

- An isolated eight-case read-only inventory diagnostic scored `2/8` strict
  and `7/8` valid with zero runtime errors. File inventory passed `1/4`; Wiki
  inventory passed `1/4`. The misses were wrong local-path binding, failure to
  commit to a required read, Wiki root-name-versus-ID binding, and incomplete
  multi-step Wiki traversal. Those mechanisms map to already closed path,
  typed-interface, and sequence families, so no oracle or candidate ran.
- The first batched inventory artifact was rejected as invalid infrastructure:
  evaluator-created file and Wiki fixtures leaked between cases because the
  production harness reset does not own those evaluator resources. The
  evaluator now isolates this suite at one case per process; production
  behavior did not change.
- A zero-model-call answer-blind census then combined five compatible frozen
  cohorts: 57 outcomes across three product SHA strata, all using the same LFM
  1.2B Q4_K_M model, context, provider settings, and production prompt. It
  measured `29/57` strict, `50/57` valid, one previously documented evaluator
  runtime error, first-token p50/p95 of `394/1,821 ms`, end-to-end p50/p95 of
  `1,726/6,556 ms`, and 3,099 MB peak VRAM.
- Verified computation was `6/6` strict and verified state change was `5/6`.
  Six numeric failure buckets met the three-failure screen, but manual
  answer-blind attribution found no coherent open mechanism. The apparent
  attached-Wiki cluster contained two already-closed literal response-contract
  misses and one semantic counting miss. The remaining clusters belonged to
  previously closed response-contract, evidence-synthesis, path-binding,
  typed-interface, or tool-sequence families.
- The 57 outcomes are coverage evidence only. They do not establish population
  prevalence, satisfy the 300-outcome complementarity gate, or support the
  final 80-percent product claim.

### Local document-reading outcome discovery

- One fresh, local-only invocation evaluated 12 newly authored CSV and RTF
  questions in 86.854 seconds. Unchanged Thaddeus scored `8/12` strict and
  `11/12` valid with zero runtime errors.
- Field extraction and row selection each reached `3/4`; table aggregation
  reached `2/4`. The run used 35 model calls, 11 `file_read` calls, 33,673
  tokens, 482/784 ms first-token p50/p95, 5,337/16,023 ms end-to-end p50/p95,
  and 3,071 MB peak VRAM.
- Ten document reads returned usable evidence. The four misses were one wrong
  CSV column interpretation, one failure to call the reader, one transposed
  path, and one unfinished average after a successful read. They are
  heterogeneous and include already closed path/tool-commitment families.
- No category met the predeclared three-valid-failure gate. No oracle, exact
  repeat, validation, or product candidate ran. DocBench and Office
  Comprehension Benchmark informed taxonomy only; no public task content was
  copied or downloaded. Evaluator PR `#129`.

### Native document-reading outcome discovery

- Product PR `#263` added harness-only, traversal-safe Base64 file fixtures
  with a 10 MiB decoded limit and suite-load validation. It did not change the
  assistant pipeline or production file tools. All twelve generated native
  fixtures passed the real PDF, DOCX, and XLSX readers before inference.
- One frozen unchanged-harness invocation evaluated four newly authored files
  per format and four tasks per category in 83.035 seconds. It scored `6/12`
  strict, `10/12` valid, and zero runtime errors.
- Field extraction scored `3/4`, row selection `2/4`, and table aggregation
  `1/4`. PDF, DOCX, and XLSX scored `2/4`, `3/4`, and `1/4`; format was a
  balance dimension, not the predeclared oracle gate.
- The run used 35 model calls, 11 `file_read` calls with nine usable evidence
  returns, 33,577 tokens, 589/1,424 ms first-token p50/p95,
  5,128/11,700 ms end-to-end p50/p95, and 3,074 MB peak VRAM.
- The six misses were heterogeneous: two invalid path mutations, one failure
  to read, one adjacent-column interpretation, one response that named the
  date but omitted the requested row label, and one counting error. No category
  supplied three aligned valid failures. No oracle, repeat, validation, or
  product candidate ran; evaluator PR `#130`.

### XLSX column-fidelity headroom diagnostic

- Static code inspection and the Open XML cell-reference contract identify a
  real representation risk: `XlsxDocumentReader` joins only physically present
  cells, so an absent middle cell can shift later values left in model-visible
  text.
- A predeclared ten-case diagnostic used six fresh sparse-cell workbooks and
  four dense controls. The unchanged harness scored `0/10` strict and `7/10`
  valid, with 24 model calls, one tool call, 15,253 input tokens, 1,122 output
  tokens, 362/412 ms first-token p50/p95, 4,815/7,112 ms end-to-end p50/p95,
  and 3,047 MiB peak VRAM.
- No case produced a successful `file_read`. Nine turns skipped the sole exposed
  tool; one hallucinated a different filename and received an access denial.
  Full-composition diagnostics confirmed that the one-tool prompt was present.
- The frozen gate required three valid sparse misses after successful reads and
  coordinate-loss activation. It therefore failed at zero activations. The
  conditional gold arm did not run, no product code changed, and the one-off
  evaluator implementation was removed. Evaluator PR `#133`.
- This result does not prove the reader is good. It proves only that this cohort
  cannot justify fixing it for model-outcome gain because the upstream tool
  commitment/path boundary prevented the representation layer from running.

### System-command outcome discovery

- A predeclared local-only diagnostic exercised six authorized allowlisted
  command outcomes, three no-action controls, and one metacharacter safety
  control against unchanged current master.
- The run completed in 36.24 seconds at `6/10` strict and `10/10` valid, with
  27 model calls, five tool calls, 21,924 tokens, 340/2,553 ms reported
  first-token p50/p95, 3,224/7,809 ms end-to-end p50/p95, and 3,049 MiB peak
  VRAM.
- All no-action and safety controls passed. `system_execute` was selected on
  five of six authorized requests, and all five selected calls succeeded.
- The four authorized strict misses split into one omitted tool, one incorrect
  command argument choice, and two final-response contract failures after
  successful execution. Only one miss was eligible for forced tool-name
  selection, below the frozen three-case gate.
- No product candidate, repeat, or validation ran. The raw `systeminfo` result
  contains local machine details and remains ignored; evaluator PR `#134`
  records only answer-blind public evidence.

### System-command binding oracle follow-up

- A fresh six-positive/four-control evaluator-only screen compared unchanged,
  tool-name-guided, and exact-command-argument arms on
  `lfm2.5-8b-a1b` Q4_K_M. It completed all 30 planned evaluations locally in
  59.9 seconds of measured arm time.
- Unchanged selected `system_execute` on only `2/6` positives, below the frozen
  `5/6` binding-dominance prerequisite. Tool-name guidance and gold command
  arguments each reached `6/6` selection, but every arm remained `0/6` on
  strict positive outcomes.
- All four no-action and metacharacter controls passed in every arm. No
  stronger-model or judge calls ran. The screen is valid, but its prerequisite
  failure means the gold arm cannot establish argument binding as the next
  narrow seam.
- Close the system-command binding family. No product implementation, repeat,
  or validation is authorized. The diagnostic suggests mixed selection and
  final-response failures, but it does not authorize bundling selection,
  argument rewriting, and projection into one candidate.
- The requested Unsloth Gemma 4 12B Q4_K_XL artifact was excluded because its
  LM Studio metadata reported no tool-use training and it returned empty
  harness responses. Official Google Gemma 4 12B Q4_K_M passed a direct
  `system_execute` schema smoke but exceeded the bounded development-time
  projection; retain it for transfer or full-benchmark confirmation after a
  mechanism survives the development loop.

### Approved-plan capability-menu oracle

- A fresh evaluator-only screen tested whether the user-approved work plan
  supplies a safer tool-selection signal than earlier lexical selectors. Six
  multi-step positives, three no-action controls, and one nonplanned parity
  control ran through raw, unchanged production routing, and a broad
  plan-derived local capability menu on `lfm2.5-8b-a1b` Q4_K_M.
- Raw scored `4/10`, unchanged scored `3/10`, and the menu oracle scored
  `5/10`. The oracle produced two independently verified Wiki-state wins, zero
  losses, and eight ties, but missed the frozen three-win gate. Both harness
  arms missed the nonplanned exact-response parity control; every no-action
  state remained unchanged.
- The candidate expanded model calls from `12` to `38`, tool calls from `0` to
  `19`, input tokens from `10,367` to `179,743`, and p50 from `1,733` to
  `3,314 ms`. P95 improved from `7,862` to `6,444 ms`, and peak VRAM tied at
  `9,326 MiB`, but the call and token ceilings failed decisively.
- Close the broad approved-plan capability-menu union. The directional wins
  show that approved plans contain useful orchestration signal, but the current
  abstract `Context` and `DurableOutput` capabilities do not identify a precise
  source or outcome family. Do not tune a narrower menu against these consumed
  tasks or proceed to step-by-step enforcement.
- A materially different future attempt belongs in the decision basket:
  enrich the user-visible approved plan with typed source and outcome families,
  then run a fresh oracle before changing production tool exposure.

### Deterministic date-arithmetic headroom and candidate

- A fresh eight-task fixed-date screen covered two cases each for calendar
  differences, date offsets, calendar properties, and schedule/time arithmetic.
  Raw minimal scored `1/8`, unchanged Thaddeus `0/8`, and compact gold evidence
  `8/8`, with full harness validity and no network, judge, or stronger model.
- The authorized candidate added one read-only, side-effect-free
  `date_calculate` capability with explicit operations and strict ISO dates or
  offset-bearing timestamps. The 12-case development slice added malformed,
  missing-input, hypothetical, and negated controls.
- The real candidate scored `2/8` strict positives and `4/4` controls with
  `12/12` validity. It selected and successfully executed the date tool on
  `2/8` positives; both corresponding outcomes were correct. The malformed
  date also called the tool and failed closed, while the three no-action
  controls made no call.
- End-to-end p50/p95 was `446/6,848 ms`, first-token p50/p95 was `269/495 ms`,
  peak VRAM was `3,049 MiB`, and the run used 29 model calls and 24,168 tokens.
  Resource and safety gates passed; the frozen `6/8` outcome and `7/8`
  selection gates failed. The result was below the `5/8` gray zone.
- No repeat or validation ran. The product implementation and branch were
  deleted. Evaluator PR `#135` preserves the manifests, answer-blind suite,
  hashes, oracle verdict, and candidate rejection.
- The failure layer is narrow and causal: verified date evidence is useful and
  execution is correct, but the fixed model does not reliably discover a newly
  exposed specialist date tool. Reusing the consumed tasks to tune a lexical
  detector would not establish generalization.

### Date-arithmetic deterministic selection prerequisite

- A second bank used eight positives disjoint from v1 plus six malformed,
  missing-input, hypothetical, negated, date-mention, and unsupported-date
  controls. It was frozen before any selector implementation.
- Raw minimal and unchanged Thaddeus each scored `0/8` positives with `8/8`
  validity. Tool-only v1 scored `3/8` positives, `3/6` controls, and `14/14`
  validity. It used 35 model calls, seven tool calls, 32,533 tokens,
  `341/610 ms` first-token p50/p95, `612/6,610 ms` end-to-end p50/p95, and 3,049 MiB
  peak VRAM.
- The tool-only arm already selected `date_calculate` on `6/8` positives. Two
  calls were omitted; three selected calls chose incorrect operations or
  arguments for forward offset, backward offset, and recurrence. The three
  strict control misses were response-contract failures without forbidden tool
  activation.
- Selection-only could causally address at most two outcomes, below the frozen
  +3 gate, so no selector code or fourth arm ran. One initial zero-call
  missing-restore artifact was classified as infrastructure failure and
  replaced by the clean recovery. Evaluator PR `#136` preserves the verdict.
- The next evidence question is typed semantic binding. It requires a separate
  oracle and fresh disjoint tasks; the consumed v2 failures are diagnostic and
  must not become lexical fixtures in runtime code.

### Typed date-argument oracle prerequisite

- A third disjoint bank isolated two forward offsets, two backward offsets, two
  recurrences, and six malformed/ambiguous/missing/hypothetical/negated/
  unsupported controls. No product mutation preceded the baseline.
- Tool-only scored `1/6` positives, `3/6` controls, and `12/12` valid. It used
  32 model calls, five tool calls, 29,530 tokens, `376/567 ms` first-token
  p50/p95, `762/4,593 ms` end-to-end p50/p95, and 3,049 MiB peak VRAM.
- Positive selection reached only `4/6`, below the frozen `5/6` prerequisite.
  Three selected calls bound the wrong operation or arguments; both recurrence
  requests omitted the tool. Typed binding therefore was not reliably dominant.
- The invalid date failed closed. The five no-action/unsupported controls made
  zero forbidden calls, although three missed exact response contracts.
- Gold, repeat, and product code did not run. The one-off executable evaluator
  code was removed after freezing the public selection. Evaluator PR `#137`
  preserves the manifest, hashes, and verdict.
- Across v1-v3, selection moved from `2/8` to `6/8` to `4/6`, while strict
  positives moved `2/8`, `3/8`, and `1/6`. No stable narrow date layer remained.
  Close this family until ordinary labeled outcomes independently reopen it.

## What remains uncertain

- Whether continued labeled outcome accumulation reveals a repeated,
  oracle-correctable failure region in a materially open mechanism family. The
  combined 57-outcome census found six numeric clusters, but manual attribution
  found each closed or heterogeneous; none of the synthetic cohorts establishes
  prevalence weights.
- Whether XLSX cell-coordinate preservation improves user outcomes remains
  untested. Reopen only after at least three fresh successful XLSX reads expose
  aligned downstream errors; do not force the tool or tune the consumed prompts
  merely to reach the reader.
- Audited system execution did not expose a stable argument-binding cluster.
  Historical discovery selected the tool on `5/6`, while the fresh binding
  screen selected it on only `2/6`; exact gold commands still produced `0/6`
  strict positives. Close the family rather than forcing the tool globally or
  bundling selection, binding, and response projection.
- Date specialization is closed on current evidence. Three disjoint slices did
  not establish one dominant layer across discovery, semantic arguments, and
  response fidelity. Do not add a bundled date parser/router/projector merely
  to lift these authored fixtures.
- Whether compact local Wiki/document retrieval can approach the demonstrated
  gold-evidence ceiling and the conditional `6/6` compact-evidence result
  without flooding context or depending on unreliable public search. The V3
  recovery result proves useful headroom is reachable, but not with its current
  exact-missing-resource discrimination.
- Whether a capability-specific deterministic postcondition can trigger only on
  observed failure and recover state without another universal model judge.
- Whether a capability-specific argument or postcondition mechanism can fix a
  fresh, oracle-proven failure cluster. The literal-response oracle found three
  correctable artifacts, but three product candidates never activated. A new
  attempt requires a materially different observable seam, not another grammar
  revision.
- Whether a learning-based adapter can improve fresh capacity holdouts remains
  unknown, but the immediate rationale sequence is stopped. Full-support
  rationale supervision produced a mixed `10/30` signal versus `8/30` controls;
  aggressive evidence compression fell to `9/30`. Reopening requires a
  materially different data-scale, coverage, or learning hypothesis plus a new
  predeclared campaign counter—not a third formatting variant, relaxed scorer,
  or silent extension of the consumed development slice. The
  [learning-capacity recalibration](LEARNING_CAPACITY_RECALIBRATION.md) records
  the aggregate rank/margin evidence and the later gold-evidence diagnostic.
  The model moved `13/30 -> 17/30` with support but missed the frozen prerequisite,
  so the 512-example scale test never trained and is not the next action.
- Whether runtime prompt budgeting can safely align with the provider's actual
  loaded context without weakening compaction, memory, safety, or continuity.
- Whether native streaming can improve perceived latency without exposing a
  draft that later validation must replace.

The current method-by-method disposition and next experiment contracts are in
[INFERENCE_METHOD_GAP_MAP.md](INFERENCE_METHOD_GAP_MAP.md).

## Reusable stop rules

- Do not score a candidate until instrumentation proves the mechanism
  activated on the intended cases.
- Do not use a larger or newer model as the candidate when the claim is
  improvement to a fixed model. Larger models are ceilings or explicit
  escalation only.
- Do not count a tool-assisted answer as closed-book model-capacity uplift.
- Stop adding routing when oracle-route or gold-evidence controls still fail.
- Stop forcing a tool when a gold tool result helps but model-generated
  arguments remain wrong; the next mechanism must target translation or
  training.
- Do not remove global validation or retry based on latency alone.
- Retire a mechanism family after three valid consecutive rejections unless a
  materially different causal hypothesis is predeclared.
