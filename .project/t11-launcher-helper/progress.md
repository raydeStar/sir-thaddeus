# Consolidate UI Launcher Helper

- Status: Done
- Branch: `task/consolidate-ui-launcher-helper`
- Started: 2026-04-01
- Objective: Extract the duplicated loopback-process health-wait and shutdown behavior used by the Avalonia runtime and voice launchers into a shared helper so launcher consolidation starts from a testable reusable seam.
- Selection Basis: Local fallback from repo planning docs because the `.project` chain ended at `t10-searxng-decoupling` and Notion state is not available in this workflow.

## Phase 1

- Selected as the narrowest verifiable slice of the repo's launcher/supervision consolidation recommendation.
- Evidence:
  - `06-risk-and-bloat-report.md`: lists launcher/supervisor consolidation as the next high-ROI cleanup step after profile gating and sidecar decoupling.
  - `04-dead-code-and-duplication.md`: calls out duplicated launch/probe implementations across runtime, voice, and search launchers.
  - The Avalonia `RuntimeHostLauncher` and `VoiceHostLauncher` currently each carry their own loopback checks, retry loops, and best-effort process shutdown logic.

## Notes

- Scope should stay narrow: consolidate shared process lifecycle helpers for the two UI-managed launchers only, not a full cross-repo launcher rewrite.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added a shared `LoopbackProcessSupport` helper in `SirThaddeus.Core` for loopback URI validation, retry-based readiness polling, and best-effort managed-process shutdown.
- Switched the Avalonia `RuntimeHostLauncher` and `VoiceHostLauncher` to the shared helper instead of keeping duplicate local implementations.
- Added focused unit tests covering the shared loopback and retry behavior.

## Phase 3

- Focused tests: `dotnet test tests/SirThaddeus.Tests/SirThaddeus.Tests.csproj -c Release --filter LoopbackProcessSupportTests`
  - Result: 5 passed, 0 failed.
- Solution build: `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: passed with 0 errors.
- Solution tests: `dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed.

## Phase 4

- Confidence: 100%
- Reason: The change only extracts already-duplicated launcher behavior into a shared helper and is covered by focused helper tests plus a clean full Release build and full Release test suite.