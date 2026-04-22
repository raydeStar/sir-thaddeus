# Pipeline Migration — AI Handoff Log

**Status:** Phase 2F complete. UI runtime is fully on the pipeline; CLI (`HeadlessRuntime`) has a pipeline-backed path **behind an env flag** (`ST_RUNTIME_USE_PIPELINE=1`) with **12 steps** including safety-boundary short-circuit. Integration tests exercise the full composition end-to-end. Default CLI behavior unchanged — legacy `AgentOrchestrator` still authoritative until harness validates the pipeline. Safe to ship / push at any point between phases.

**Purpose of this doc:** Any AI or contributor picking this up mid-stream should be able to read this file once and continue. Each phase ends with updates here. Keep it terse — it's a ledger, not prose.

---

## TL;DR — what and why

The codebase had two divergent chat-execution paths:
- **UI** (`src/Thaddeus.Runtime` → `LmStudioAssistant`) — lean, had tool-chip streaming + permission gate.
- **CLI** (`apps/headless-runtime` → `AgentOrchestrator`) — big, had ~9 orchestrator behaviors (utility router, logic-puzzle scaffold, memory, personality, etc.) the UI lacked.

We're unifying on a **pipeline of stateless steps behind per-runtime session facades**. Each chat turn flows through an ordered `ChatPipeline` of `ITurnStep`s. Runtime-specific concerns (permission gate, event transport, virtual tools, memory provider) are wired in via ports.

After migration: both runtimes share the same pipeline; only their adapters differ. Adding a behavior = adding one step file + one line in the pipeline builder.

---

## Current state

### Shipped (pushed to master)

| Phase | Deliverable | Location |
|---|---|---|
| 2A.1 | `TurnContext`, `StepResult` | [packages/agent/SirThaddeus.Agent/Pipeline/](../../packages/agent/SirThaddeus.Agent/Pipeline/) |
| 2A.2 | `ITurnStep`, `ChatPipeline` runner | same |
| 2A.3 | `IChatEventSink` port, `NullChatEventSink`, `ChatTurnPublisherEventSink` runtime adapter | same + [src/Thaddeus.Runtime/Chat/](../../src/Thaddeus.Runtime/Chat/) |
| 2A.4 | `RuntimePermissionGateAdapter` (bridges runtime `ToolPermissionGate` → agent `IToolPermissionGate`) | [src/Thaddeus.Runtime/Tools/RuntimePermissionGateAdapter.cs](../../src/Thaddeus.Runtime/Tools/RuntimePermissionGateAdapter.cs) |
| 2B.1 | `FeatureExtractorStep` | [packages/agent/SirThaddeus.Agent/Pipeline/Steps/](../../packages/agent/SirThaddeus.Agent/Pipeline/Steps/) |
| 2B.2 | `LogicPuzzleScaffoldStep` | same |
| 2B.3 | `FootmanRouterStep` | same |
| 2B.4 | `ToolLoopStep`, `PostProcessStep`, `ResponseComposerStep`, `ToolCallAbstractions` (`IToolCallInterceptor`, `IToolArgsRewriter`, `IToolGroupClassifier`, `ToolCallOutcome`) | same |
| 2B.5 | Runtime-side interceptors: `ProposeAutomationInterceptor`, `AutomationSearchRecencyRewriter`, `RuntimeToolGroupClassifier`. Also: refactored `LmStudioAssistant.RespondAsync` to run the pipeline; deleted `RunToolLoopAsync` / `ApplyFootmanFilterAsync` / `BuildCallSignature` (~280 lines). | [src/Thaddeus.Runtime/Chat/Pipeline/](../../src/Thaddeus.Runtime/Chat/Pipeline/) + [src/Thaddeus.Runtime/Chat/LmStudioAssistant.cs](../../src/Thaddeus.Runtime/Chat/LmStudioAssistant.cs) |
| 2C.1 | `UtilityFastPathStep` (deterministic time/unit/math) | Steps/ |
| 2C.2 | `MemoryContextStep` | Steps/ |
| 2C.3 | `AutoMemoryExtractStep` | Steps/ |
| 2C.4 | `OnboardingInjectionStep` + `PromptSuffixAppender` helper (LogicPuzzleScaffoldStep refactored to use it) | Steps/ + [packages/agent/SirThaddeus.Agent/Pipeline/PromptSuffixAppender.cs](../../packages/agent/SirThaddeus.Agent/Pipeline/PromptSuffixAppender.cs) |
| 2C.5 | `SearchFallbackStep` | Steps/ |
| 2C.6 | `PersonalityInjectionStep` | Steps/ |
| 2D.1 | `StdoutChatEventSink` (CLI-facing event sink) | Pipeline/ |
| 2D.2a | `IHeadlessAgent` interface + `PipelineBackedAgentOrchestrator` (both the legacy `AgentOrchestrator` and the new pipeline-backed class implement it) | [packages/agent/SirThaddeus.Agent/IHeadlessAgent.cs](../../packages/agent/SirThaddeus.Agent/IHeadlessAgent.cs), [Pipeline/PipelineBackedAgentOrchestrator.cs](../../packages/agent/SirThaddeus.Agent/Pipeline/PipelineBackedAgentOrchestrator.cs) |
| 2D.2b | HeadlessRuntime types swapped `AgentOrchestrator` → `IHeadlessAgent` across 5 files; env flag `ST_RUNTIME_USE_PIPELINE=1` picks pipeline-backed impl; off by default | [apps/headless-runtime/SirThaddeus.HeadlessRuntime/Program.cs](../../apps/headless-runtime/SirThaddeus.HeadlessRuntime/Program.cs) + `RuntimeApiServer.cs` + `RuntimeApiServer.EndpointMappings.Runs.cs` + `WorkflowChatRunCoordinator.cs` |

### UI pipeline shape (already running)

[LmStudioAssistant.BuildTurnPipeline](../../src/Thaddeus.Runtime/Chat/LmStudioAssistant.cs):

```
1. UtilityFastPathStep     ← terminates on deterministic match
2. FeatureExtractorStep
3. LogicPuzzleScaffoldStep
4. FootmanRouterStep        ← emits gatekeeper chip
5. ToolLoopStep             ← emits tool-start/complete chips
6. PostProcessStep          ← sanitizer + automation-refusal collapse
7. ResponseComposerStep     ← builds final AgentResponse
```

Memory / onboarding / search-fallback / personality steps exist but aren't wired into the UI yet (constructed with null providers on the facade side = no-op). See "next session" below.

### Not shipped — deferred to later phases

| Phase | Deliverable | Notes |
|---|---|---|
| 2D.2 | Migrate `HeadlessRuntime` to the pipeline | **Next up.** Scope: wire `AgentOrchestrator`-shaped facade that delegates to `ChatPipeline`. ~8 files touched. |
| 2D.3 | Retire / thin `AgentOrchestrator` | Only possible after 2D.2 ships. |
| 2D.4 | Full 89-test harness run end-to-end | Depends on 2D.2/2D.3. |

---

## Architecture — just enough to pick up

### Ports

Agent package defines, runtimes adapt:

- `IChatEventSink` — turn.start / delta / complete / tool.started / tool.completed / footman.decision. UI wires via `ChatTurnPublisherEventSink`; CLI via `StdoutChatEventSink`; harness via a capturing sink (to be built).
- `IToolPermissionGate` — already in agent package. UI wires via `RuntimePermissionGateAdapter`. CLI currently bypasses (uses `AuditedMcpToolClient` directly).
- `IToolCallInterceptor` — virtual-tool handlers (e.g. `propose_automation`). One per concern.
- `IToolArgsRewriter` — mutates tool args before execution (e.g. automation `recency=week` default).
- `IToolGroupClassifier` — maps tool name → group label for UI icons. Default = "Unknown".
- `IMemoryContextProvider`, `IAutoMemoryExtractor`, `ISearchFallbackExecutor`, `IPersonalityRuntime`, `IFootmanRouter`, `IDeterministicUtilityEngine` — existing interfaces the steps consume.

### TurnContext shape

Immutable record, threaded through the pipeline via `with`-expressions:

- `ThreadId`, `MessageId`, `UserText` (required)
- `IsAutomationRun`
- `Features` (nullable, populated by `FeatureExtractorStep`)
- `LlmMessages` (seeded by facade with system prompt + history; mutated via new list per step)
- `ToolDefs` (seeded by facade; narrowed by `FootmanRouterStep`)
- `AssistantDraft` (populated by `ToolLoopStep`; cleaned by `PostProcessStep`)
- `ToolCallsMade` (accumulated in the loop)

### StepResult

`StepResult.Continue(TurnContext)` or `StepResult.Terminate(AgentResponse)`. The `ChatPipeline` runner short-circuits on Terminate.

### ToolLoopStep return contract

Returns `Continue` with `AssistantDraft` set on the happy path (so downstream post-process + composer can finalize). Returns `Terminate` only on round-trip cap. Cancellation bubbles.

---

## Resumption pointer — DO THIS NEXT

**Phase 2D.2: Migrate HeadlessRuntime to pipeline.**

### Target files (current orchestrator touch-points)

```
apps/headless-runtime/SirThaddeus.HeadlessRuntime/Program.cs           (5 sites: BuildOrchestrator, orchestrator.ResetConversation, GetAvailableToolCountAsync, ProcessAsync, orchestrator param)
apps/headless-runtime/SirThaddeus.HeadlessRuntime/RuntimeApiServer.cs  (1 site: Func<AppSettings, AgentOrchestrator>)
apps/headless-runtime/SirThaddeus.HeadlessRuntime/RuntimeApiServer.EndpointMappings.Runs.cs  (2 sites)
apps/headless-runtime/SirThaddeus.HeadlessRuntime/WorkflowChatRunCoordinator.cs  (SeedHistory, TimeBudgetedAgentOrchestrator decorator, ProcessAsync)
```

### Strategy — pragmatic, two sub-PRs

**2D.2a — Introduce `PipelineBackedAgentOrchestrator` in agent package.** Implements `IAgentOrchestrator` (2 methods: `ProcessAsync` with + without conversationId). Holds session state externally (simple `List<ChatMessage>` history per conversation, or a `IDialogueSessionStore` abstraction). Builds a `TurnContext` per turn, runs the 7-step pipeline, absorbs result back into state. Unit tests for: ProcessAsync produces reply, history is appended, ResetConversation clears, multiple conversation IDs don't cross-contaminate.

**2D.2b — Migrate HeadlessRuntime's `BuildOrchestrator(settings)` factory.** Behind a feature flag (settings toggle or env var `ST_RUNTIME_USE_PIPELINE=1`), return `PipelineBackedAgentOrchestrator` instead of `AgentOrchestrator`. Both implement `IAgentOrchestrator` so `WorkflowChatRunCoordinator`, `TimeBudgetedAgentOrchestrator`, and the REST endpoints work unchanged. Run the smoke suite first (8 tests, ~2 min), then reasoning (18 tests), then the rest. Delete the flag once the harness passes fully.

### Non-obvious calls the new class must support

`AgentOrchestrator` exposes **more than `IAgentOrchestrator`**. The headless runtime uses these on the concrete type:

- `ResetConversation()` — clears internal `_history`.
- `SeedHistory(IEnumerable<(string role, string content)>)` — pre-populates history for workflow-run continuity.
- `GetAvailableToolCountAsync(CancellationToken)` — diagnostic endpoint.
- Properties: `ActiveProfileId`, `DeepDiveEnabled`, `AdvancedPlaceDiscoveryEnabled`, `MemoryEnabled`, `UserLocationHint`, `UserTimezone`, `PreferredUnits`, `MaxTokensBudget`, `ContextLocked`.

**Recommendation:** make these part of a new `IHeadlessAgent` interface (extends `IAgentOrchestrator`) and implement on both the legacy `AgentOrchestrator` and the new `PipelineBackedAgentOrchestrator`. Avoid the concrete-type coupling that blocked us before.

### Scope estimate

- 2D.2a: ~150 lines new, ~200 lines of tests. 1-2 hours.
- 2D.2b: ~50 lines of rewiring + feature flag. 1 hour.
- Harness verification: run-time only (each test is one LLM call; full 89 is ~30-60 min depending on model).

---

## Testing — copy-paste commands

```bash
# Agent-package pipeline tests (fast, 120 tests, ~100ms)
dotnet test tests/SirThaddeus.Tests/SirThaddeus.Tests.csproj --no-build \
  --filter "FullyQualifiedName~Agent.Pipeline"

# Runtime tests relevant to the pipeline swap (fast, ~100ms)
dotnet test tests/runtime/Thaddeus.Runtime.Tests.csproj --no-build \
  --filter "FullyQualifiedName~LmStudioAssistantTests"
dotnet test tests/runtime/Thaddeus.Runtime.Tests.csproj --no-build \
  --filter "FullyQualifiedName~ChatTurnPublisherEventSinkTests"
dotnet test tests/runtime/Thaddeus.Runtime.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RuntimePermissionGateAdapterTests"

# Harness (slow, real LLM, needs LM Studio running)
./dev/harness.ps1 --suite smoke --judge none
./dev/harness.ps1 --suite reasoning --judge none
./dev/harness.ps1 --all --judge none
```

---

## Known gotchas

1. **NU1605 error on `SirThaddeus.sln`.** `tests/shell/Thaddeus.Shell.Tests.csproj` pins `Microsoft.Extensions.Logging.Abstractions 9.0.0` while `SirThaddeus.Agent` transitively wants 10.0.3. **Pre-existing on master**, not introduced by the pipeline migration. One-line fix: bump to 10.0.3 in that csproj.

2. **DLL lock on `dotnet test tests/runtime/`**. A running `Thaddeus.Runtime.exe` holds `SirThaddeus.Agent.dll` + friends in its own output folder, and the runtime test project tries to copy the same DLLs for its own output → MSB3027 "file in use". Kill the runtime (or just its testhost children) before running runtime tests.

3. **Testhost zombies.** `dotnet test` sometimes leaves an orphan `testhost.exe` behind after a test-run is cancelled mid-flight (e.g. `TaskStop`). `taskkill //IM testhost.exe //F` before the next run.

4. **AgentOrchestrator permission gate.** The legacy CLI runs through `AuditedMcpToolClient` which has its own permission hooks (`ConsolePermissionGate` / `ApiPermissionGate`). Don't confuse it with the pipeline's `IToolPermissionGate` — they live in different layers. Harness uses `ApiPermissionGate` that auto-approves via a REST endpoint.

5. **`tool_ping` vs `ping`.** `ToolGroupClassifier.Classify` only matches `ping` exactly (not `tool_ping`) for the Safe group. Tests that pick a Safe tool should use `ping` / `time_now` / `propose_*`.

6. **`propose_automation` is runtime-specific.** Lives in UI runtime only; CLI doesn't emit it. When migrating the CLI, skip wiring `ProposeAutomationInterceptor` — it has no analog in headless mode.

---

## Phase log

Update this section when landing each sub-PR.

- **2D.1 — StdoutChatEventSink.** Shipped. 8 tests pass. Consumed in 2D.2b.
- **2D.2a — PipelineBackedAgentOrchestrator + IHeadlessAgent.** Shipped. 11 tests pass. Legacy `AgentOrchestrator` also implements `IHeadlessAgent` (just had to add the inheritance; methods already existed).
- **2D.2b — HeadlessRuntime env-flag migration.** Shipped. Type signatures swapped to `IHeadlessAgent` across `Program.cs` + 3 server files. `BuildOrchestrator` branches on `ST_RUNTIME_USE_PIPELINE=1` env var — flag off by default so harness still tests the legacy path. CLI pipeline composition in `BuildPipelineBackedOrchestrator`: UtilityFastPath → FeatureExtractor → LogicPuzzleScaffold → FootmanRouter → ToolLoop → PostProcess → ResponseComposer. CLI-specific omissions: `AlwaysGrantGate` (permission check happens at `AuditedMcpToolClient`, not inside the loop), no propose_automation interceptor, no automation-args rewriter, stdout event sink.
- **2D.3a — Personality + auto-memory + memory-context + location wired into CLI pipeline.** Shipped. `BuildPipelineBackedOrchestrator` now constructs `PersonalityRuntime` + `MemoryContextProvider` (with `SmartIntentClassifier` against the gatekeeper LLM) and adds `PersonalityInjectionStep`, `MemoryContextStep`, `AutoMemoryExtractStep` to the pipeline. System prompt is pre-wrapped with a location block via `BuildHeadlessSystemPrompt` (mirrors the UI's `BuildLocationBlock`). CLI pipeline is now **10 steps**: UtilityFastPath → PersonalityInjection → FeatureExtractor → LogicPuzzleScaffold → MemoryContext → FootmanRouter → ToolLoop → PostProcess → AutoMemoryExtract → ResponseComposer. Fills gaps that would have caused personality-suite (22 tests) and memory-dependent suites to regress when the flag is on.

- **2E.1 — CapturingChatEventSink.** Shipped. Agent-package class + 6 tests. Records every event to thread-safe queues; `Snapshot()` / `SnapshotOfKind()` / `Clear()` API. Useful for integration tests + future harness capture.
- **2E.2 — Full-pipeline integration tests.** Shipped. 8 tests in `PipelineIntegrationTests.cs` compose the CLI-equivalent pipeline with fakes (`CountingLlm`, `QueuedLlm`, `StubMcp`, `StubMemoryProvider`, `RecordingExtractor`) and validate: utility bypass, lifecycle events, tool start/complete pairing, logic-puzzle scaffold injection, memory block injection, history persistence across turns, auto-memory extraction, reset.
- **2E.3 — SearchFallbackStep wired into CLI.** Shipped. CLI now constructs a `SearchOrchestrator` + `SearchFallbackExecutor` when tools are available, and adds `SearchFallbackStep` to the pipeline after `PostProcessStep`. Trigger reuses `AgentOrchestrator.HasRefusalOrUncertaintySignals` (hoisted from `private static` to `public static`) so the pipeline's refusal heuristic matches the legacy path byte-for-byte. **CLI pipeline is now 11 steps.**
- **2F.1 — NU1605 fix.** Investigated. `tests/shell/Thaddeus.Shell.Tests.csproj` NU1605 blocks build; bumping packages surfaces **deeper** test-code drift (`ShellSessionController.OpenWorkspaceMenuId` / `StopAllMenuId` no longer exist). Not pipeline-migration scope; spawned as a side task for targeted shell-tests maintenance.
- **2F.2 — SafetyBoundaryStep.** Shipped. Added `SafetyBoundaryStep` that short-circuits high-risk illicit-instruction prompts via `OrchestratorMessageHelpers.LooksLikeHighRiskIllicitInstructionRequest` (hoisted to public) + `BuildSafetyBoundaryWithAlternativeReply`. Wired into both UI and CLI pipelines as the FIRST step (before utility fast-path). 8 tests verify pass-through for benign messages, termination for illicit shapes, and byte-for-byte text parity with the legacy orchestrator's safety reply. **UI + CLI pipelines both now 12 steps.**

### Known gaps relative to legacy orchestrator (harness will likely flag these)

The following orchestrator behaviors don't yet have pipeline steps. Each surfaces as a potential harness failure; each is a focused "add step X" PR.

- **Search orchestrator / deep-dive** — legacy delegates web research to `SearchOrchestrator` with multi-turn session tracking + deep-dive follow-up fetching. Pipeline's `ToolLoopStep` calls `web_search` directly. Expect gaps in `Suites/web-search/` and `Suites/quality/02_web_grounding.yaml`.
- **Slot extraction + tool planner** — legacy builds a deterministic tool plan before calling the LLM. Pipeline is reactive (LLM picks tools). May affect tool-contract suite.
- **Dialogue state / context anchoring** — legacy maintains `IDialogueStateStore`, place-context anchoring, context lock. Pipeline is stateless per turn (history is the only state). May affect multi-turn continuity (`Suites/continuity/`).
- **Guardrails + completion validator + repair loop** — legacy runs a reasoning-guardrails pipeline + repair loop. Pipeline has `PostProcessStep` with a sanitizer only.
- **Multi-intent bypass + conversation segmentation + lane router** — legacy-only. Low priority; add steps if harness flags them.
- **2D.4 — Harness full pass.** Not started. Run: `$env:ST_RUNTIME_USE_PIPELINE = "1"; ./dev/harness.ps1 --suite smoke --judge none` first, then `--suite reasoning`, then `--all`. Expect gaps in suites that rely on orchestrator-only behaviors (search orchestrator, deep-dive, slot extraction, dialogue state, guardrails post-processor) — each becomes a focused "add step X" PR.
- **2D.5 — Retire AgentOrchestrator.** Not started. Gated on harness pass with the flag on.

---

## What a new AI should verify before editing

1. `git status` is clean, and you're on the expected branch.
2. `dotnet build packages/agent/SirThaddeus.Agent/SirThaddeus.Agent.csproj` → 0 errors.
3. `dotnet test ... Agent.Pipeline` → 120 passing.
4. Read this doc.
5. Look at `LmStudioAssistant.BuildTurnPipeline` — that's the reference composition. The CLI migration should produce an equivalent.

When you finish a sub-PR: update the "Phase log" section above. Keep entries 1-2 lines each.
