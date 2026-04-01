# Explain Lane

Status: Needs Human Testing
Priority: P1 - High
Area: Architecture
Branch: task/implement-explain-lane
Commit: not yet committed
Last updated: 2026-04-01

## Summary
Implemented a conservative Explain Lane execution path for explicit web-backed explain and summarize requests, then validated it through build and full xUnit regression coverage. Harness validation remains blocked by search/runtime environment issues, so this task is paused for human review rather than self-certified as done.

## Progress Log
- Phase 1: Selected as the next highest-priority remaining architecture task based on the last known board ordering.
- Phase 1: Created branch task/implement-explain-lane.
- Phase 2: Reconstructing task scope from local code, tests, and routing heuristics because Notion is currently unavailable.
- Phase 2: Added ExplainLane request extraction and search-summary formatting helpers.
- Phase 2: Wired a conservative Explain Lane fast-path into AgentOrchestrator for explicit web-backed explain and summarize requests only.
- Phase 2: Added 25 ExplainLane unit tests.
- Phase 3: dotnet build SirThaddeus.sln --no-restore -c Release passed.
- Phase 3: Targeted regression tests passed after narrowing the fast path and restoring personality-prompt compatibility.
- Phase 3: dotnet test SirThaddeus.sln -c Release --no-build passed with 2083 tests total (2038 + 45).
- Phase 3: Fresh quality harness rerun completed after clearing a stale headless-runtime lock.
- Phase 3: `quality` suite run `20260401_133542` finished 5/7 passing. The two failures were unrelated search/weather cases: `quality_weather_clarity` hard-failed because the runtime reported safe-mode tool denials for `weather_geocode`/`weather_forecast`, and `quality_no_bare_answers` failed on weak web-search content after tool errors.
- Phase 3: `web-search` suite rerun started after manually bringing up SearXNG, but the harness still reported SearXNG unavailable and reproduced broader search-environment failures outside the Explain lane seam.
- Phase 4: Confidence is below 100% because harness validation is not trustworthy in the current environment even though the product build and all xUnit tests pass.
- Phase 6: Routing to Needs Human Testing and preserving the branch as a local WIP commit for review.

## Current Understanding
- LaneRouter already classifies queries like "What is this PDF about?" and "Summarize this page" as Explain.
- There is no ExplainLane executor yet.
- CheckLane provides the current pattern for lane-specific fast-path execution.

## Files Changed
- packages/agent/SirThaddeus.Agent/Lanes/ExplainLane.cs
- packages/agent/SirThaddeus.Agent/AgentOrchestrator.Lanes.cs
- packages/agent/SirThaddeus.Agent/AgentOrchestrator.cs
- packages/agent/SirThaddeus.Agent/AgentOrchestrator.Configuration.cs
- tests/SirThaddeus.Tests/Agent/Lanes/ExplainLaneTests.cs

## Open Risks
- The original Notion acceptance criteria are unavailable, so implementation needs to stay conservative and testable.
- Tool access must remain within existing policy constraints.
- Harness validation is blocked by environment/runtime issues outside the Explain lane code path.

## Verification Details
- `dotnet build SirThaddeus.sln --no-restore -c Release`: passed.
- `dotnet test SirThaddeus.sln -c Release --no-build`: passed with 2083 total tests (2038 main + 45 Windows).
- Targeted regressions fixed and re-run: `WebLookup_KnownLatestVersionQuestion_UsesPinnedDeterministicAnswer`, `CrossProfile_SameInput_DifferentPersonality_SameTrustAndTask`, `GuardrailsOff_WebSearchRouting_RemainsUnchanged`, and `DriftResistance_30Turns_AnchorPresentEveryTurn` all passed.

## Handoff
- Review branch `task/implement-explain-lane` after the WIP commit.
- If you want clean harness verification, first stabilize the local search runtime so the harness stops reporting `Safe mode is active` for weather/web tools and consistently detects SearXNG on `http://127.0.0.1:8080`.
