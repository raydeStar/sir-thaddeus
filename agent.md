# Avalonia Migration Plan (Codex‑Ready)

**Epic:** Replace the existing Windows‑only UI with an Avalonia UI while preserving the headless runtime and the orchestration/pipeline reliability.

**Non‑negotiables (top priorities):**
1) **Do not break the orchestration pipeline.** The router → policy gate → tool loop must behave identically.
2) **Headless mode remains first‑class.** UI must be optional.
3) **Remove/retire the old UI cleanly** (no zombie projects, dead references, or split brains).
4) **UI design polish is Phase 2.** Phase 1 is correctness and parity.

---

## 0) Operating Principles (Codex: follow strictly)

### 0.0 Upgrade target: .NET 10
- **Upgrade all projects to .NET 10** as part of this migration.
- Perform the upgrade in a controlled pass with build/test gates.
- **Do not mix large behavioral refactors and framework upgrade in the same commit** unless necessary; prefer staged commits.



### 0.1 Hard boundaries
- **Core/Agent runtime must not reference UI assemblies.**
- UI talks to core through a **stable interface** (IPC boundary), not through deep class references.
- **Voice (Piper/Whisper) remains hosted by the headless core**, not the UI.

### 0.2 “Parity before polish”
- Implement minimal UI surfaces that prove the pipeline works end‑to‑end.
- After parity: reskin/reflow/animations.

### 0.3 Keep builds green
- Every major step must end with:
  - `dotnet build` solution
  - `dotnet test` (if tests exist)
  - A quick manual smoke run (headless + UI)

---

## 1) Inventory & Freeze the Current Pipeline

### 1.0 Preserve existing front-end functionality (minimum viable UI parity)
Even though we are reskinning, the new Avalonia UI must preserve the **functional surfaces** needed for testing:
- Enter app quickly (startup + connect to runtime)
- Chat (send/receive, streaming)
- STOP/cancel
- Permissions approve/deny
- Logs/Audit view
- Settings (at least connection + key toggles)

The visual design can be crude in Phase 1, but these behaviors must exist.



### 1.1 Snapshot known‑good behavior
Create a short **Pipeline Parity Checklist** (markdown) in `/docs/migration/avalonia-parity.md` that includes:
- Known intents that must route correctly (web search, logic puzzle, tool call, etc.)
- Known tool calls that must require permission.
- Known “STOP” cancellation behavior.
- Logging expectations (audit log entries, timing, tool loop iterations).

### 1.2 Add a “golden” smoke script (if missing)
Add a small script or command sequence that:
- Runs core in headless mode
- Sends a few test prompts
- Verifies expected outputs exist in logs

If there is already a CLI/terminal runtime, reuse it.

---

## 2) Target Architecture

### 2.1 Project layout goal
- `SirThaddeus.Core` (or existing Agent package): orchestration pipeline, tools, memory, policy
- `SirThaddeus.Runtime` (headless host): process entrypoint, hosting, config, voice hosting
- `SirThaddeus.UI.Avalonia` (new): cross‑platform UI client
- `SirThaddeus.Contracts` (new, tiny): shared DTOs/interfaces for IPC (NO heavy deps)

**Rule:** `UI` depends on `Contracts` only. `Runtime` depends on `Core` + `Contracts`.

### 2.2 IPC boundary (choose one)
Prefer simplest robust option:
- **Option A (recommended): Local HTTP + WebSocket/SSE** hosted by `Runtime`
  - UI connects to `http://127.0.0.1:<port>`
  - WebSocket for streaming tokens/events
- **Option B:** stdio JSON‑RPC if you already have this pattern running well

**Codex instruction:** pick the option that best matches current architecture and requires the least new plumbing.

---

## 3) Phase 1: Preserve Headless Runtime & Extract a Stable Host API

### 3.1 Ensure headless stays operational
- Confirm current headless mode can run without any UI assembly loaded.
- If there are UI references in host code, refactor:
  - move UI‑specific code behind interfaces
  - or move it into the UI project.

### 3.2 Add host API endpoints/events (if not already present)
Minimum endpoints:
- `POST /api/chat` → starts a run (returns runId)
- `POST /api/runs/{runId}/cancel` → STOP
- `GET /api/runs/{runId}/events` → WebSocket/SSE stream
- `GET /api/audit` → audit log view
- `GET /api/health` → health + version

Event types (examples):
- `token.delta`
- `run.completed`
- `run.failed`
- `tool.requested` (with permission payload)
- `tool.approved/denied`
- `audit.appended`

**Important:** UI should never call tools directly; it only approves/denies and displays.

---

## 4) Phase 1: Add Avalonia UI (Minimal Parity Shell)

### 4.1 Create Avalonia project
- Add `SirThaddeus.UI.Avalonia` using the Avalonia template.
- Confirm it builds on Windows first.

### 4.2 Implement minimal screens (do not overbuild)
**Screen A: Chat**
- Input box + send button
- Transcript view (supports streaming)
- STOP button

**Screen B: Permissions**
- When event `tool.requested` arrives, show a modal/panel:
  - tool name
  - why needed
  - parameters
  - Approve / Deny

**Screen C: Logs/Audit**
- Simple list view of audit entries

No theming polish yet—just functional parity.

### 4.3 Wire up streaming
- Connect to runtime stream and append tokens/events.
- Make sure cancellation works.

### 4.4 Settings stub
- Show settings page, but it can be minimal. It must include:
  - “Headless mode” info
  - Server port / connection status
  - Voice engine selection (if applicable) displayed from runtime capabilities

---

## 5) Retire the Old UI Cleanly

### 5.1 Deprecation approach
- Mark old UI project as deprecated and remove from default solution build.
- Ensure runtime can be launched either:
  - headless only
  - with Avalonia UI

### 5.2 Remove dead code
- Delete unused view models, WPF resources, old UI services.
- Remove references from solution/projects.
- Keep a migration branch tag so you can recover if needed.

**Codex instruction:** do not delete core logic that is still referenced by runtime—only UI surface code.

---

## 6) .NET 10 Upgrade (Required)

### 6.1 Upgrade sequencing (recommended order)
**Pass A — Baseline to .NET 10 (no UI rewrite yet):**
1) Create branch: `migration/dotnet10-baseline`.
2) Update `global.json` (if present) to .NET 10 SDK.
3) Update all `.csproj` files:
   - set `TargetFramework` / `TargetFrameworks` to `net10.0` (and platform TFMs as needed).
4) Update NuGet packages to versions compatible with .NET 10.
5) Fix build breaks and run tests.
6) Tag baseline commit so you can diff regressions.

**Pass B — Avalonia migration on top of .NET 10:**
- Branch from the baseline and proceed with UI replacement.

**If Pass A is impossible (because Avalonia requires newer deps):**
- Combine the minimal required changes, but keep commits small and gated.

### 6.2 Compatibility guardrails
- Keep nullable / analyzers stable (avoid introducing new warning floods mid-migration).
- If `LangVersion` is set, keep it unless it blocks .NET 10.
- Ensure CI uses .NET 10 SDK.

---

## 7) Cross‑Platform Build & Packaging (Phase 1.5)

### 7.1 Publish targets
Add CI/publish profiles for:
- Windows: `win-x64`
- Linux: `linux-x64`
- macOS: `osx-x64` and/or `osx-arm64`

### 7.2 Distribution shape
Prefer a simple layout per OS:
```
SirThaddeus/
  sir-thaddeus-runtime
  sir-thaddeus-ui
  assets/
  config/
```
- UI launches runtime if not running.
- Runtime chooses an open port or uses configured port.

### 7.3 POS-friendly defaults
- Avoid heavy background animations.
- Limit transcript virtualization costs.
- Lazy-load optional components.



### 6.1 Publish targets
Add CI/publish profiles for:
- Windows: `win-x64`
- Linux: `linux-x64`
- macOS: `osx-x64` and/or `osx-arm64`

### 6.2 Distribution shape
Prefer a simple layout per OS:
```
SirThaddeus/
  sir-thaddeus-runtime
  sir-thaddeus-ui
  assets/
  config/
```
- UI launches runtime if not running.
- Runtime chooses an open port or uses configured port.

### 6.3 POS-friendly defaults
- Avoid heavy background animations.
- Limit transcript virtualization costs.
- Lazy-load optional components.

---

## 7) Pipeline Reliability Guardrails (Must Implement)

### 7.1 Regression tests
Add at least 5 “golden path” tests or scripted validations:
1) Simple Q&A without tools
2) Web search tool call + approval
3) Tool denied path
4) STOP cancels run cleanly
5) Memory read/write path (if applicable)

If unit testing is hard due to LLM variability, implement a **mock LLM** and **mock tool server** for deterministic tests.

### 7.2 Observability
Ensure logs clearly show:
- route selection
- policy decisions
- tool loop iteration count
- time spent per tool
- cancellation handling

---

## 8) Phase 2: Reskin & UX Pass (After Parity)

### 8.1 Visual system
- Create a theme token file (colors, spacing, typography)
- Apply consistent layout grid

### 8.2 UX improvements
- Command palette
- Better permission UX (diff view, parameter highlighting)
- Memory browser enhancements

### 8.3 Performance tuning
- Virtualized chat list
- Backpressure on token streaming
- Coalesce token deltas to reduce UI churn

---

## 9) How to Run This With Codex (Optimal Workflow)

### 9.1 Recommended multi-pass approach
Because this is a large change, run Codex in **separate passes** to reduce derailment:

**Pass 1 — .NET 10 baseline upgrade (no UI rewrite):**
- Goal: build + tests green on .NET 10.

**Pass 2 — Core/Runtime boundary hardening + IPC contract (if needed):**
- Goal: headless runtime stable, contract documented.

**Pass 3 — Add Avalonia UI parity shell:**
- Goal: functional parity (chat/approve/logs/settings/stop).

**Pass 4 — Remove old UI + cleanup:**
- Goal: no WPF dependencies remain in default build.

**Pass 5 — Publish targets + packaging polish:**
- Goal: win/linux builds and a simple distribution layout.

### 9.2 Codex CLI prompt template (copy/paste)
Create `agent.md` at repo root with this instruction (then run Codex CLI against it):

**Title:** “Execute Avalonia Migration Plan — Pass X”

**Prompt body:**
- Read `/docs/migration/avalonia-parity.md` (create if missing) and the canvas plan.
- Execute **only Pass X**.
- Keep commits small and logically separated.
- After each commit: `dotnet build` and `dotnet test`.
- If a decision is required, prefer the lowest-risk option consistent with the plan.

### 9.3 Guardrails to include in agent.md
- Do not change orchestration semantics.
- Headless must work without UI.
- UI is a client; voice stays in core/runtime.
- Prefer deletions only after parity is proven.

---

## 10) Deliverables Checklist

**Code deliverables**
- [ ] .NET 10 upgrade completed (baseline pass), build + tests green
- [ ] New `SirThaddeus.UI.Avalonia` project builds and runs
- [ ] Old UI removed from default build and/or deleted
- [ ] Headless runtime remains functional
- [ ] Stable IPC boundary between UI and runtime
- [ ] Permissions workflow working end‑to‑end
- [ ] Streaming + STOP works
- [ ] Cross-platform publish scripts

**Docs**
- [ ] `/docs/migration/avalonia-parity.md`
- [ ] `/docs/runtime/ipc-contract.md` (events + endpoints)
- [ ] `/docs/build/publish.md` (how to build for win/linux/mac)

**Validation**
- [ ] Smoke script passes
- [ ] 5 regression validations pass

---

## 11) Final Cleanup Checklist (Do Not Skip)
- [ ] Delete unused WPF resources, viewmodels, and DI services
- [ ] Remove old UI NuGet packages and references
- [ ] Remove dead config keys
- [ ] Confirm `dotnet build` passes clean
- [ ] Confirm `dotnet test` passes
- [ ] Confirm runtime can run headless on a machine without UI installed
- [ ] Confirm UI can start runtime and connect reliably
- [ ] Confirm logs/audit are consistent with pre-migration
- [ ] Tag a release candidate branch for rollback


**Code deliverables**
- [ ] New `SirThaddeus.UI.Avalonia` project builds and runs
- [ ] Old UI removed from default build and/or deleted
- [ ] Headless runtime remains functional
- [ ] Stable IPC boundary between UI and runtime
- [ ] Permissions workflow working end‑to‑end
- [ ] Streaming + STOP works
- [ ] Cross-platform publish scripts

**Docs**
- [ ] `/docs/migration/avalonia-parity.md`
- [ ] `/docs/runtime/ipc-contract.md` (events + endpoints)
- [ ] `/docs/build/publish.md` (how to build for win/linux/mac)

**Validation**
- [ ] Smoke script passes
- [ ] 5 regression validations pass

---

## 10) Final Cleanup Checklist (Do Not Skip)
- [ ] Delete unused WPF resources, viewmodels, and DI services
- [ ] Remove old UI NuGet packages and references
- [ ] Remove dead config keys
- [ ] Confirm `dotnet build` passes clean
- [ ] Confirm `dotnet test` passes
- [ ] Confirm runtime can run headless on a machine without UI installed
- [ ] Confirm UI can start runtime and connect reliably
- [ ] Confirm logs/audit are consistent with pre-migration
- [ ] Tag a release candidate branch for rollback


## Progress Update (2026-03-05)
- [x] Restored Avalonia startup by fixing `App.axaml` resource load order and switching to `avares://SirThaddeus.UI.Avalonia/Themes/ThemeResources.axaml`.
- [x] Verified compile for UI project: `dotnet build apps/ui-avalonia/SirThaddeus.UI.Avalonia/SirThaddeus.UI.Avalonia.csproj -m:1 -v m`.
- [x] Verified launch stability with a startup probe (`dotnet run --no-build ...` remained running for 8 seconds).
- [ ] Next pass: manual visual trim toward previous design and runtime-connect smoke validation.
