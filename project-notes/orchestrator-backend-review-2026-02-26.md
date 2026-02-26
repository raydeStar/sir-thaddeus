# Orchestrator Backend Review (Refactor Reliability Audit)

Date: 2026-02-26  
Scope: `packages/agent/SirThaddeus.Agent` orchestration pipeline (router → policy gate → utility/search/tool execution loop)

## Executive summary

The orchestrator has a strong architectural skeleton (explicit route object, deterministic policy gate, bounded tool loop), but it is still brittle in exactly the ways you described:

1. **Routing is over-coupled to string heuristics and weak LLM fallbacks**, with no strict typed slot contract between routing and planning.
2. **Tool selection is policy-filtered but not relevance-ranked**, so the planner/model still sees broad menus in fallback paths.
3. **There is no explicit deterministic "plan validation" layer** between proposed tool calls and execution.
4. **Recovery behavior is mostly post-failure**, not pre-failure (it cleans up after bad calls instead of rejecting bad plans up front).
5. **Observability is decent for logs, but thin for eval-grade metrics** (confidence calibration, per-intent precision/recall, clarify-vs-act quality).

Net: the system is **not fundamentally broken**, but currently behaves like a collection of good subsystems with weak contracts between them.

## What is already solid (keep this)

- **Deterministic policy gate is the right direction.** `PolicyGate` cleanly maps intents to capability allow/deny lists and keeps side-effectful capabilities constrained. Keep this model and expand it.  
- **Tool-loop safety rails exist.** Max round-trips, tool conflict resolution, and structured error fallback handling reduce blast radius when calls fail.  
- **Separation of specialized deterministic paths is good.** Search orchestration, utility handling, and screen-capture deterministic flow reduce random tool-calling for known domains.  
- **Conversation segmentation for multi-intent turns is a strong reliability investment.** It reduces mixed-intent contamination and gives you a path toward per-segment execution policies.

## High-risk brittleness points

### 1) Router contract is too thin (intent flags, no strong slots)

Current route output has intent + boolean flags + capability hints, but lacks a first-class typed slot payload and clarification contract.

- This forces slot/argument logic to be reconstructed downstream in multiple places.
- Confidence is present, but not used as a strict fail-closed decision boundary before execution.

### 2) Heuristic-heavy routing will always drift at the edges

`DefaultRouter` and `IntentFeatureExtractor` rely on phrase lists and "contains" checks plus a minimal 3-class classifier fallback (`chat/search/tool`). This is fast, but brittle for phrasing variation, mixed requests, and domain-overlap prompts.

Symptoms this causes:
- Correct intent but wrong capability requirements.
- Ambiguous prompts routed into `general_tool` too often.
- False positives from phrase collisions.

### 3) Tool exposure is policy-filtered, but not relevance-curated

You do apply capability filtering, but there is no semantic "tool search" step that reduces candidate tools to a small top-k set for the specific utterance/intent.

Consequences:
- The model/planner still competes across tools that are technically allowed but contextually poor.
- Similar tool descriptions increase wrong-tool probability.

### 4) Missing deterministic plan validator pre-execution

The tool loop validates conflicts and policy permission, but it does not appear to enforce a deterministic semantic validator such as:
- "Do all proposed calls match selected intent?"
- "Are required slots present and normalized?"
- "Is this a domain jump from user ask?"

Without this, nonsensical but policy-allowed plans can still execute.

### 5) Overloaded orchestration method increases contract leakage

`AgentOrchestrator.ProcessAsync` coordinates many concerns (routing, policy, memory, utility, search, guardrails, chat fallback, tool loop). Even with partial extraction, the turn contract remains hard to reason about and easy to regress.

Practical impact:
- New features patch branches instead of tightening shared interfaces.
- Behavior differs by path (chat-only vs utility vs search vs tool-loop), increasing inconsistency for users.

### 6) Clarification behavior is underpowered

There is some location confirmation handling in `ToolPlanner`, but no globally enforced clarification gate based on low route confidence or missing required slots.

Result: the system often "tries something" instead of asking one disambiguating question.

### 7) Test coverage is broad, but not eval-oriented for routing quality

The repo has many orchestrator/policy/tool tests, but reliability issues described are about *decision quality under prompt variation*. That requires corpus-style routing/tool-choice evals, confusion tracking, and regression thresholds.

## Recommended refactor (priority order)

## P0 — Lock the contracts (highest ROI)

1. **Introduce `IntentDecision` v2 as strict schema**
   - `intent` (enum)
   - `slots` (typed object per intent family)
   - `confidence` (calibrated 0..1)
   - `requiresClarification` (bool)
   - `clarificationQuestion` (string?)

2. **Enforce fail-closed routing**
   - If `confidence < threshold` OR required slots missing → ask clarification, do not plan tools.

3. **Add deterministic `PlanValidator` before MCP execution**
   - Validate intent/tool compatibility
   - Validate required slot presence and shape
   - Reject domain jumps
   - Cap plan size/tool budget per intent

4. **Make validator rejection observable**
   - Emit reason codes (`missing_slot`, `domain_jump`, `policy_mismatch`, etc.) for replay and eval.

## P1 — Improve routing stability without huge latency cost

5. **Adopt 3-tier router pipeline**
   - Tier 1: hard deterministic command rules
   - Tier 2: embedding-based intent nearest-neighbor (intent exemplar bank)
   - Tier 3: LLM classifier for ambiguous leftovers only (strict JSON)

6. **Keep regex/phrase heuristics only for slot extraction and hot commands**
   - Do not let phrase heuristics be the primary fallback classifier for ambiguous language.

7. **Calibrate confidence per intent family**
   - Different thresholds for high-risk actions (system/file/screen) vs low-risk chat/search.

## P2 — Tool relevance and description hygiene

8. **Add semantic tool retrieval (top-k 3..7) after policy filtering**
   - Policy decides what is legal.
   - Tool retrieval decides what is relevant.
   - Planner sees only relevant legal tools.

9. **Rewrite tool descriptions with exclusion lines**
   - `Use when ...`
   - `Do NOT use when ...`
   - tiny example

10. **Add negative examples for confusing tool pairs**
   - Especially around memory vs search vs browse vs screen.

## P3 — Architectural cleanup for maintainability

11. **Extract a `TurnPipeline` coordinator with explicit stages**
   - route → clarify gate → policy → tool retrieval → plan → validate → execute → compose

12. **Define one canonical response composer contract**
   - Ensure consistent post-processing across chat/search/utility/tool-loop paths.

13. **Move path-specific fallback logic behind stage interfaces**
   - Prevent branch-specific behavior drift over time.

## P4 — Evals and operational discipline

14. **Add routing eval corpus**
   - prompt → expected intent/slots/clarify
   - mixed-intent cases
   - adversarial phrasing and typos

15. **Add tool-choice eval corpus**
   - prompt + allowed tools → expected tool subset / no-call

16. **Track metrics in audit pipeline**
   - route confidence, clarify rate, validator reject rate, wrong-tool rate, retry rate, user correction rate.

17. **Set regression budgets in CI**
   - e.g., cannot merge if intent accuracy drops > X% on gold set.

## Tactical code-level changes (where to start)

- `Routing/DefaultRouter.cs`
  - Replace direct chat/search/tool LLM text classifier with strict JSON response schema.
  - Add confidence threshold behavior that returns clarification request.

- `RouterOutput.cs`
  - Evolve into typed `IntentDecision` + `Slots` rather than boolean-only capability hints.

- `PolicyGate.cs`
  - Keep as-is conceptually; add checks to ensure high-risk intents require high confidence or explicit user confirmation token.

- `Tools/ToolDefinitionBuilder.cs`
  - Add compact tool metadata index + semantic ranking endpoint in agent layer.

- `ToolLoop/ToolLoopExecutor.cs`
  - Insert `IPlanValidator` before executing winner calls.
  - Return deterministic clarification/replan on validation failure.

- `AgentOrchestrator.cs`
  - Pull stage orchestration into dedicated pipeline class and shrink `ProcessAsync` to glue + telemetry.

## Risk and migration strategy

- **Do not big-bang this rewrite.**
- Add v2 contracts behind feature flags:
  1. Introduce `IntentDecisionV2` + validator while retaining old route path.
  2. Run both in shadow mode and compare decisions in logs.
  3. Flip execution to v2 once divergence + quality metrics stabilize.

## Bottom line

You are very close structurally, but the brittleness you feel is real and expected from the current contract boundaries. The fastest path to "boringly reliable" behavior is:

1. strict intent+slots contract,
2. fail-closed clarification gate,
3. deterministic plan validator,
4. relevance-ranked tool curation,
5. eval-driven regression control.

That sequence will produce a visible reliability jump without discarding your current architecture.
