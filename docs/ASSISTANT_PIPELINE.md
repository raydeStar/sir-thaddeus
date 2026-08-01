# Assistant Pipeline

Model-dependent tool families can be narrowed before the normal pipeline by
the user-controlled capability certificate policy. See
[MODEL_CAPABILITY_CERTIFICATION.md](MODEL_CAPABILITY_CERTIFICATION.md) for the
`Auto / On / Off` contract, fingerprint limits, probe budget, and current Wiki
write evidence. Certification never replaces permissions or target guards.

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

## Current local date and time utility

High-confidence requests for the machine's current local date or time terminate
through the existing deterministic utility step before model inference. The
recognizer accepts bounded greeting, politeness, and concise response-style
wrappers while rejecting event dates, future or historical dates, scheduling,
elapsed-time questions, location-scoped time, timezone conversion, and compound
requests that need another capability.

Eligible responses come from the application clock and use no model or tool
call. Ambiguous or non-current requests continue through the ordinary pipeline;
the utility does not change memory, personality, permissions, safety,
validation, retry, or streaming composition.

## Live runtime policy utility

After the safety boundary, an explicit question about one current runtime
policy field can terminate through a deterministic read-only utility. Eligible
fields are panic mode, safe mode, budget enablement and limits, and the six
tool-group permission values. The step calls the existing audited
`policy.get_state` MCP tool, validates its typed JSON response, and renders only
the requested Boolean, number, or permission word. It performs no model call
and contains no configured policy values.

The recognizer requires one current field and rejects compound, conceptual,
hypothetical, historical, deferred, future, negated, and mutation requests.
For an explicit non-current boundary request, only `policy.get_state` is
withheld from the model's tool menu for that turn; mutation tools and all other
capabilities remain available. Desktop and headless runtimes compose the same
step after safety and before other deterministic or model-backed utilities.

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

For an eligible immediate root creation with no user-owned location, the first
model-visible `wiki_root_create` schema contains only the required root name.
The configured Wiki library remains application-owned, while the actual MCP
tool, permission boundary, and persisted result are unchanged. Requests that
name a path, folder, directory, location, or drive retain the complete schema;
deferred and non-action requests remain ineligible before this projection is
considered.

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

## Explicit Wiki mutation targets

Wiki reference context and Wiki write authorization are separate user-visible
controls. For a turn that may change Wiki state, the user can select one
existing root or page as the write target without attaching it as evidence.
The runtime resolves that selection to typed root/page identity before model
execution; display names are supplied to the model, while opaque identifiers
remain runtime-owned.

Immediately before the audited MCP boundary, every proposed Wiki mutation is
checked against that typed identity. An exact target match proceeds through the
normal permission policy. A mismatch, ambiguous argument shape, new-root
creation, or mutation whose containment cannot be proven returns the structured
`wiki_mutation_target_mismatch` failure and performs no side effect. Read-only
Wiki tools and non-Wiki capabilities are unaffected. With no selected target,
the guard is inactive and existing behavior is unchanged.

This contract does not infer authorization from prose, repair tool arguments,
choose a substitute resource, bypass permission prompts, or turn attached Wiki
context into write scope. Desktop and headless paths carry the same typed turn
field, and activation is observable through content-free experiment diagnostics.

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

## Live turn execution control

Desktop chat turns are registered with a `TurnRunCoordinator` before assistant
execution begins. Each run owns the cancellation token passed through the
assistant pipeline, model client, permission boundary, MCP tool calls, response
streaming, and persistence. `RuntimeStopAllService` cancels these tokens before
stopping voice and MCP sidecars, so STOP covers active chat work as well as
managed processes.

Pause, resume, redirect, and take-over are cooperative controls. The pipeline
declares safe checkpoints before each stage, model round, and tool call. Pause
holds at the next checkpoint; it never claims to freeze a side effect midway.
Redirect releases a paused or taken-over run and supplies a user steering
instruction to the remaining pipeline. When steering arrives before a proposed
tool batch, the stale calls are recorded as skipped and the model receives the
correction before selecting further work.

Run state is exposed through `/api/runs` and `chat.run.state` events. These
events are the authority for progress UI and local audit records; clients must
not invent elapsed percentages or synthetic execution steps. The shared agent
pipeline exposes the same execution-control port to every host. Desktop supplies
the live coordinator; hosts that do not yet expose a control transport use the
no-op implementation and therefore retain their previous uninterrupted
behavior.

### User-approved intent plans

Desktop requests with deterministic multi-step or consequential-action signals
receive a typed intent plan before assistant execution. Casual chat, simple
questions, and safe single-step synthesis stay on the direct path. Plan
selection makes no model call, names abstract capabilities rather than concrete
tools, and never widens the tool list.

A planned run remains in `AwaitingApproval`; its assistant task waits on the
run's approval signal before calling `IAssistant.RespondAsync`. Users may edit,
add, remove, or reorder steps. Edits are server-validated and increment an
optimistic plan version, so a stale approval fails rather than starting work
under an unseen revision. The approved plan is supplied to the production
pipeline as a user-approved constraint while normal safety, routing, permission,
audit, completion-validation, and retry behavior remain authoritative.

This is distinct from the retired shadow `TurnPlan` experiment: it is a
user-control and execution-gating contract, not an LLM router, hidden planner,
benchmark mechanism, or extra inference stage.

### Structured effects, receipts, and undo

Every production tool call is classified into a typed effect before it crosses
the MCP boundary. The effect records whether the call mutates state, whether it
is reversible, its local or outbound boundary, the resolved target, and the
supported undo strategy. `chat.effect.proposed` is emitted before execution and
`chat.effect.completed` after the audited result. Both events are persisted in
the per-turn trace and rendered from the same contract in the Work Receipt.

The result is intentionally explicit about verification. A successful tool
transport is not automatically called independently verified. Wiki mutations
earn that label only when the returned evidence contains a versioned, deleted,
or restored Wiki state. The receipt otherwise says that only the tool result is
available. Wiki undo/redo invokes the real versioned Wiki APIs; it does not
optimistically remove a receipt or pretend the external state changed.

### Per-turn and global memory policy

`AssistantTurnOptions.EphemeralMemory` carries the incognito decision from the
desktop request into the supported assistant pipeline. Ephemeral turns skip
dynamic memory retrieval, core-memory injection, automatic extraction, chunk
writes, and all model-visible memory tools. The same policy applies to typed and
voice-submitted turns.

The persisted runtime memory policy can independently pause durable memory.
When paused, existing records remain inspectable but the assistant neither
reads nor writes them. Reset is a separate, confirmation-gated administrative
operation that transactionally deletes facts, events, chunks, profile cards,
and nuggets from the local SQLite store. Pause/resume/reset actions are
recorded in the append-only audit log.

### Local audit and assistant insights

The Settings audit surface reads only the local append-only audit log. It is
browsable, filterable, and exportable as JSON Lines. Outcome, intervention,
permission, recovery, escalation, and approval-fatigue measurements are
computed from that same ledger; no telemetry service is added.

Metrics expose their numerator, denominator, definition, and evidence status.
An empty denominator is reported as insufficient data. Each receipt displays
its evidence confidence (verified, source-backed, tool-result-only, or
unverified), and optional user outcome feedback creates the paired observation
used for local trust calibration. Until that pair exists, calibration reports
insufficient data; routing confidence and activity volume are not substituted
as flattering proxies.

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
