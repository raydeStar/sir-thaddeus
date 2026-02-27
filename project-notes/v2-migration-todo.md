# Orchestrator Refactor: Remaining Migration Plan

## Context
We have completed Phase 1 through Phase 10 of the `AgentOrchestrator` refactoring blueprint.
- **Layers implemented:** Route → Plan → Validate → Execute → Verify → Repair.
- **Components extracted:** Tool names, prompts, tool alias resolution, and deterministic utility response builders (Weather, Time, Utilities).
- **Result:** The `AgentOrchestrator` is now decomposed into focused sub-modules while retaining 100% of the original behavior.

## Next Steps (V2 Migration)
The next major architectural milestone is to fully port over to the `V2AgentOrchestratorAdapter` and the `TurnPipeline` (the V2 architecture). The following items are required to complete this transition:

### Phase 11: Tool Capability Parity in RouterV2
- Port remaining `IntentFeatureExtractor` heuristics into the Tier 1 router (`RouterV2`).
- Specifically, the following 19 tests are currently failing when running purely under V2 routing due to missing feature ports:
  - (Review and fix the 19 failing `RouterV2Tests` related to deep dive lookups, news lookups, local business discovery, and screen requests).

### Phase 12: Memory Fallback & Context Injection
- Ensure that the memory context injection (`MemoryContextProvider` / `ContextAnchoringService`) behaves identically in the `TurnPipeline` as it does in the legacy `AgentOrchestrator`.
- The dynamic context decoupler is wired, but we need to ensure legacy memory retrieval tools trigger correctly in the new loop.

### Phase 13: Deprecate Legacy Pipeline
- Switch the main DI registration in `App.xaml.cs` to default to `V2AgentOrchestratorAdapter` (if not already done).
- Move `AgentOrchestrator.cs` and `AgentOrchestrator.Internal.cs` to a `Legacy/` folder or deprecate them entirely.
- Clean up unused dependencies and dead code paths in `AgentOrchestrator`.

### Phase 14: Polish the Footman Router
- Consolidate `IFootmanRouter` integration directly into the V2 `TurnPipeline`.
- Eliminate the duplicated state enums if any exist between V1 and V2 routing logic.

## Goal
By the end of these phases, the entire conversational turn lifecycle will flow strictly through the clean `TurnPipeline`, with strict validation and completion contracts running natively on every turn.
