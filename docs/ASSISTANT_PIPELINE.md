# Assistant Pipeline

This document is the production contract for Sir Thaddeus chat orchestration.
It describes behavior that is active on the default desktop and headless paths,
the diagnostics that are intentionally supported, and the experiments that have
been retired.

## Production path

Desktop and headless chat use the same ordered responsibilities:

1. Apply the safety boundary and deterministic utility fallbacks.
2. Add personality, request features, memory, onboarding, and dialogue context.
3. Narrow tool exposure through the footman and freshness policies.
4. Run the primary model/tool loop through the audited MCP permission boundary.
5. Sanitize the draft while preserving explicit safe response contracts.
6. For explicit answer-only contracts, project one uniquely shared verbatim
   scalar from the sanitized draft and successful tool evidence when provable;
   otherwise validate completion and perform at most the configured bounded
   repair.
7. Apply search fallback only when search is available and applicable.
8. Persist automatic memory asynchronously and compose the final response.

The desktop composition is owned by
`src/Thaddeus.Runtime/Chat/LmStudioAssistant.Pipeline.cs`. The headless runtime
maintains a parity composition for harness and terminal execution. Composition
tests should verify security and ordering invariants rather than optional
experiments.

## Supported deterministic selection

An explicit, unambiguous request to create a Wiki root may select the advertised
`wiki_root_create` contract for the first model round. The policy rejects page
requests, explanatory or hypothetical prompts, negation, deferred intent,
unavailable tools, and turns with an upstream forced tool.

Selection does not parse arguments, execute a write, change conversation
messages, or bypass permission policy. The model still supplies the arguments,
the normal audited MCP boundary executes the call, and subsequent rounds remain
free to use the ordinary tool loop. Other Wiki mutations continue through
normal model selection and may use by-name contracts that resolve unique local
targets inside the tool.

When that same deterministic policy recognizes a Wiki-root request as
informational, hypothetical, negated, or deferred, `wiki_root_create` is not
advertised to the model for the turn. Read-only Wiki tools remain available,
an upstream explicit forced-tool decision is preserved, and the permission
boundary remains the final authority. This prevents a small model from turning
a recognized non-action request into an unnecessary mutation attempt or
permission prompt.

Temporal deferral is evaluated at the same narrow Wiki-root seam. Future-date
scheduling, a leading condition such as approval or event completion, and
explicit "not now" language can withhold `wiki_root_create`. Immediate markers
such as `now`, `today`, or `immediately`, future-purpose clauses, and temporal
words inside root names remain eligible for ordinary creation. The policy is
deterministic, adds no model call, and never schedules work for later.

## Attached local Wiki evidence

When the user explicitly attaches an entire Wiki library, root, or folder, the
runtime ranks pages against the current request with deterministic lexical
overlap and supplies at most four extractive passages within a 4,000-character
model-visible budget. The full attachment identity and selected source metadata
remain available outside the prompt for the UI and audit path. A single-page
attachment keeps its existing page-context behavior.

This compilation issues no embedding or model call, does not search across the
selected Wiki scope, and stays inactive when no Wiki context is attached. It is
a prompt-load and evidence-selection seam, not implicit retrieval or a general
conversation router.

## Evidence-backed answer-only projection

After tool execution and sanitization, an explicit answer-only request may be
reduced to one verbatim scalar only when the same unique span already occurs in
both the model draft and a successful tool result. The projection never
generates, infers, summarizes, or ranks an answer. It fails closed for failed or
missing tools, explanations, plural contracts, ambiguous shared spans, and
multi-value full-content requests.

When the proof succeeds, the projected response is already an independently
grounded postcondition, so the pipeline skips the later LLM completion-validator
call for that turn. When it does not succeed, the existing completion validation
and bounded repair path is unchanged. This is a narrow response-contract seam,
not a router, retriever, benchmark path, or global validation removal.

## Exact-identity repair termination

Completion repair remains bounded and validation-led. After a failed completion
check, the repair loop may request corrected text. If that non-empty generation
is ordinally identical to the draft it was asked to repair, the loop retains the
existing validation failure and stops the attempt without revalidating unchanged
text. Revalidation cannot discover different content when no content changed.

This short circuit occurs only after repair generation. It does not skip initial
completion validation, create a conversational fast path, or weaken safety and
response-contract checks. Any changed repair text still follows the complete
existing validation and adoption path.

## Supported diagnostics

`ST_ROUTING_LATENCY_TRACE=1` enables duration-only routing diagnostics. It may
record stage names, identifiers, and timings, but must not record prompts or
memory contents and must not change response behavior.

The v2 harness derives a sanitized per-turn `diagnostics.json` from its isolated
runtime logs before deleting the sandbox. The artifact is restricted to stage
names, outcomes, booleans, counts, and durations. Prompts, responses, memory,
tool payloads, suite identifiers, and expected answers are excluded.

Completion-repair diagnostics may additionally record content-free attempt,
non-empty-generation, changed-generation, revalidation-pass, and adoption
counts. They expose repair outcomes without retaining either the draft or the
generated text.

`ST_HARNESS_PRESERVE_SANDBOX=1` is a test-support option for retaining v1 or v2
local logs and audit records during diagnosis. It is not a production route.

These diagnostics are intentionally opt-in because they increase logging or
retain temporary test state. They are not dormant product features.

## Retired experiments

The following experiments are not part of the supported architecture and have
been removed rather than left behind feature flags:

- sampled self-consistency and tool-aware majority voting;
- shadow `TurnPlan` compilation;
- high-confidence conversation validation skipping;
- the uncomposed experimental `RouterV2` and LLM task-plan builder.

The experiments did not establish repeatable quality improvement. Global retry
or validation removal reduced benchmark quality, while sampled voting added
latency and did not beat unchanged controls. The normal validation and retry
path therefore remains authoritative.

Future experiments belong on a short-lived branch or in evaluation tooling.
They may enter production only after an exact repeat, a disjoint holdout, broad
product regression coverage, and an explicit architecture decision. A disabled
environment flag is not an acceptable long-term storage mechanism for rejected
behavior.

## Complexity and ownership

- Keep pipeline composition separate from request lifecycle and streaming code.
- Prefer one implementation of a routing rule shared by desktop and headless
  paths; parity-only composition should remain small and directly tested.
- Split a touched production file when it combines unrelated responsibilities
  or cannot be reviewed confidently as one unit. Avoid mechanical splitting of
  unrelated legacy subsystems in the same behavior change.
- The active desktop lifecycle and pipeline composition are split between
  `LmStudioAssistant.cs` and `LmStudioAssistant.Pipeline.cs`. The transitional
  headless host remains a documented legacy hotspot; retire or decompose it in
  a dedicated parity-focused change.
- Keep benchmark datasets, expected answers, suite identifiers, and promotion
  thresholds outside production assemblies.

## Release verification

Changes to this pipeline require:

1. focused tests for the affected stages and composition invariants;
2. the repository CI-equivalent gate, `dev/test.ps1`;
3. at least one conversation-level smoke suite when behavior changes;
4. package smoke tests when the runtime or release composition changes.
