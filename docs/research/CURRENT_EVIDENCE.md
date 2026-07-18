# Current Evidence

**Evidence cutoff:** July 18, 2026

**Production baseline:** `b72ebb2`; answer-only successful-tool-evidence
projection was promoted through product PR `#219`
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

### Narrow harness gains

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

- The four-arm MMLU attribution baseline found no two-item prompt or
  orchestration-loss trigger. On the lower-bound LFM 1.2B development slice,
  raw scored `10/20` twice and unchanged harness scored `13/20` twice. This is
  encouraging development evidence, not validated universal uplift.
- The 50-item general-capability battery is a useful fast portfolio. On the 8B
  diagnostic run, raw scored `37/50` and unchanged harness `36/50`, so it did
  not support a general routing or prompt-expansion candidate.
- A contamination-audited native-checkpoint QLoRA smoke completed in 89.25
  seconds on the RTX 4090. Loss fell from `6.8134` to `2.2826`, 884,736 LoRA
  parameters trained, and the saved adapter reloaded consistently. It remained
  `0/4` exact on its four held-in nonce mappings, below the frozen `3/4` gate,
  so the smoke is rejected. This proves the local training/save/reload plumbing
  is usable; it is not model-capacity or benchmark evidence.
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

## What is not working

### Closed-book prompt and inference scaffolds

- Finite-choice contract detection, a closed-book choice scaffold, and a
  capability-scoped choice prompt all failed the frozen MMLU development gate.
- A compact arithmetic Plan-and-Solve scaffold produced no strict gain.
- Sampled self-consistency and tool-aware majority voting added substantial
  inference cost and did not beat unchanged controls.
- Blind regeneration, universal planning, and same-model self-critique have no
  demonstrated default-path value.

### First QLoRA training-path smoke

- The frozen held-in smoke did not establish even the deliberately narrow
  memorization signal required before a real adaptation campaign. Falling loss,
  code-shaped outputs, and a reloadable adapter do not replace exact outcomes.
- The rejected adapter is not deployed. More steps, different targets, new
  data, or altered LoRA settings would be a new experiment; do not tune this
  smoke repeatedly until it passes.

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

## What remains uncertain

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
- Whether a separately designed rationale-distillation or QLoRA candidate can
  improve fresh capacity holdouts remains unknown, but the first QLoRA
  training-path smoke missed its own held-in learning gate. Reopening the lane
  requires a materially different, predeclared data/mechanism hypothesis—not
  a relaxed scorer or silent extension of the rejected run.
- Whether runtime prompt budgeting can safely align with the provider's actual
  loaded context without weakening compaction, memory, safety, or continuity.
- Whether no-change completion-repair attempts can be identified without
  storing response text and then removed at a capability-specific seam.
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
