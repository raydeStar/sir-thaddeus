# Clear Inherited Runtime Safety In Harness Sandbox

- Status: In Progress
- Branch: `task/triage-quality-harness`
- Started: 2026-04-01
- Objective: Stop harness headless runs from inheriting local `runtimeSafety.safeMode` and `panicMode` state so tool-enabled suites measure real product behavior instead of host-machine safety posture.
- Selection Basis: The latest `quality` suite failure artifacts show a common root cause: allowed tool calls are blocked with `Safe mode is active.`, and prior project notes already identified inherited safe mode as a harness-isolation defect.

## Phase 1

- Selected as the current fix because `quality_weather_clarity` hard-fails before substantive answer synthesis, and the failure is caused by harness sandbox settings rather than test expectations.
- Evidence:
  - `quality_weather_clarity` score artifacts show `Disallowed tools used: memory_retrieve, web_search` and `Required tool not called: weather_forecast` after `weather_geocode` was blocked by safe mode.
  - `quality_no_bare_answers` also shows all allowed web tools blocked by safe mode, leading to a weak fallback answer.
  - `HarnessRuntimeSandbox.Create(...)` copies the loaded host settings into a sandbox file but does not clear inherited runtime safety flags.

## Notes

- This is a legitimate harness-isolation fix, not a scoring change.
- Keep the patch narrow: normalize runtime safety in the sandbox and add deterministic regression coverage.

## Phase 2

- Updated `tools/SirThaddeus.Harness/Execution/HarnessRuntimeSandbox.cs` to clear inherited `safeMode`, `panicMode`, and stale safe-mode metadata in sandboxed settings.
- Added `tests/SirThaddeus.Tests/Harness/HarnessRuntimeSandboxTests.cs` to verify sandboxed settings no longer preserve inherited runtime safety flags.
