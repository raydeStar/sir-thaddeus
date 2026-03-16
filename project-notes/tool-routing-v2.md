# Tool Routing V2 — Current Pipeline Notes

This document tracks the **implemented** routing pipeline and the MCP hook points used in production.

## Status

- Routing model: **Implemented**
- Deterministic policy gate: **Implemented**
- Tool conflict matrix: **Implemented**
- MCP permission + audit hooks: **Implemented**
- **Footman authority recalibration: Implemented** (March 2026)

---

## Footman Authority Model

The Footman is a fast LLM-based routing classifier that provides a second opinion on deterministic routing decisions. Its authority is bounded by the **Action Tier** model to prevent stochastic downgrades of safe retrieval operations.

### Action Tiers

| Tier | Class | Footman Role | Examples |
|------|-------|-------------|----------|
| 0 | `RetrievalSafeLocal` | **Bypassed** — deterministic direct execution | Memory read, utility (time/math), greetings, logic puzzles |
| 1 | `RetrievalSafeExternal` | **Advisory** — may refine query/arguments but cannot veto without typed block reason | Web search, news, deep dive, local business, browse, screen observe |
| 2 | `PlanComplex` | **Authoritative** — full planning authority | File write, system commands, memory write, ambiguous/multi-step tasks |

### Authority Boundaries

- **Deterministic router = traffic cop**: classifies intent, selects tool family, determines action tier.
- **Footman = butler / query refiner**: improves query quality, normalizes arguments, requests clarification only when truly necessary.
- **Tool runner = executor**: executes what the routing pipeline decides.

The Footman should never be a second planner with broad veto power over safe retrieval operations.

### Typed Block Reasons

When Footman wants to block or downgrade a deterministic route, it must return a machine-readable reason code:

| Reason Code | Description | Valid for Tier 0 | Valid for Tier 1 | Valid for Tier 2 |
|-------------|-------------|:---:|:---:|:---:|
| `SAFETY_BLOCK` | Hard safety/content policy concern | ✓ | ✓ | ✓ |
| `TOOL_UNAVAILABLE` | Target tool disabled or not connected | ✓ | ✓ | ✓ |
| `POLICY_SCOPE_MISMATCH` | Request outside tool's documented scope | ✗ | ✓ | ✓ |
| `MISSING_REQUIRED_PARAM` | Required parameter missing and cannot be inferred | ✗ | ✓ | ✓ |
| `AMBIGUOUS_INTENT` | Genuinely ambiguous, needs clarification | ✗ | ✗ | ✓ |
| `UNKNOWN` / unrecognized | Free-form decline without structured reason | ✗ | ✗ | ✗ |

Unrecognized reason codes are mapped to `Unknown` and rejected for Tier 0/1 downgrades.

### Disagreement Logging

When the deterministic router and Footman disagree on intent, a structured `ROUTER_DISAGREEMENT` audit event is emitted containing:

- User message (truncated)
- Deterministic intent + confidence
- Footman intent + confidence
- Footman reason code (raw)
- Footman block reason (typed)
- Action tier
- Arbitration result (`downgrade_blocked` or `footman_accepted`)

### Implementation Files

- `packages/agent/SirThaddeus.Agent/Routing/ActionTier.cs` — tier enum + classifier
- `packages/agent/SirThaddeus.Agent/Routing/FootmanBlockReason.cs` — reason enum + parser + tier policy
- `packages/agent/SirThaddeus.Agent/Routing/RoutingDecision.cs` — carries `BlockReason` property
- `packages/agent/SirThaddeus.Agent/Routing/FastLlmFootmanRouter.cs` — populates `BlockReason` during parse
- `packages/agent/SirThaddeus.Agent/AgentOrchestrator.Routing.cs` — tier-aware `ShouldRunFootmanForRoute`, `ShouldBlockFootmanLookupDowngrade`, disagreement logging
- `packages/agent/SirThaddeus.Agent/AgentOrchestrator.cs` — arbitration block uses tiers and block reasons

---

## End-to-end flow

```text
User message
  -> Intent router (RouterOutput)
  -> Policy gate (PolicyDecision)
  -> Tool exposure filter (capability-based)
  -> Conflict resolution matrix (winner/skip per turn)
  -> Tool loop executor (allowed tools only)
  -> Post-processing + response assembly
```

Key outcome: the model cannot call tools that are not exposed by policy.

---

## MCP hook points (runtime enforcement)

All MCP calls are wrapped by `AuditedMcpToolClient` and gated by `IToolPermissionGate` (`WpfPermissionGate` in desktop runtime).

### 1) Pre-call permission hook

Before the MCP call executes:

- tool name is canonicalized
- tool group is resolved (screen/files/system/web/memoryRead/memoryWrite)
- effective group policy is applied (`off` / `ask` / `always`)
- if `ask`, runtime prompts user and optionally caches session grant

### 2) Audit start hook

Before execution:

- emit `MCP_TOOL_CALL_START`
- include redacted input summary + request metadata

### 3) Post-call audit hook

After execution (success or failure):

- emit `MCP_TOOL_CALL_END`
- include duration, success/failure, redacted output, permission result

### 4) Settings persistence hook

If user selects "Allow always":

- `WpfPermissionGate` raises `PersistGroupAsAlways`
- desktop runtime persists group policy to `settings.json`

---

## Routing + policy contracts

### Router output contract

- `RouterOutput.Intent` chooses primary route
- capability flags and `RequiredCapabilities` refine the route
- confidence + risk are available for deterministic fallback behavior

### Policy gate contract

- `PolicyGate.Evaluate(router)` returns `PolicyDecision`
- `AllowedCapabilities` drives exposed tool set
- explicit allow/deny tool exceptions are additive/subtractive
- unknown or unmapped tools are hidden by default

### Conflict matrix contract

Before MCP execution, requested tool calls are filtered by deterministic rules:

- policy forbids
- tool-specific exceptions
- capability-level conflicts
- deterministic tie-break

Reference: `project-notes/tool-conflict-matrix.md`.

---

## Core implementation files

- `packages/agent/SirThaddeus.Agent/RouterOutput.cs`
- `packages/agent/SirThaddeus.Agent/PolicyGate.cs`
- `packages/agent/SirThaddeus.Agent/ToolLoop/ToolLoopExecutor.cs`
- `packages/agent/SirThaddeus.Agent/AuditedMcpToolClient.cs`
- `packages/agent/SirThaddeus.Agent/ToolGroupPolicy.cs`
- `apps/desktop-runtime/SirThaddeus.DesktopRuntime/Services/WpfPermissionGate.cs`
- `apps/desktop-runtime/SirThaddeus.DesktopRuntime/App.xaml.cs`

---

## Review checklist

- Route classification is deterministic under fallback
- Policy blocks are enforced before tool exposure
- Unmapped tools are not exposed
- Permission decisions are visible, explicit, and auditable
- Every MCP call has start/end audit events
- Headless behavior remains safe-default (deny when required)

---

## Notes

- This file intentionally documents the live architecture, not an aspirational rewrite plan.
- For full runtime architecture context, see `project-notes/architecture-nuts-bolts.md`.
