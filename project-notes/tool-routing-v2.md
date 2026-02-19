# Tool Routing V2 — Current Pipeline Notes

This document tracks the **implemented** routing pipeline and the MCP hook points used in production.

## Status

- Routing model: **Implemented**
- Deterministic policy gate: **Implemented**
- Tool conflict matrix: **Implemented**
- MCP permission + audit hooks: **Implemented**

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
