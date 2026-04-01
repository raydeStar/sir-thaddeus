# Decouple SearXNG Python Runtime

- Status: Done
- Branch: `task/decouple-searxng-python-runtime`
- Started: 2026-04-01
- Objective: Remove the runtime and script-level fallback that lets SearXNG borrow Python from the voice-backend package so the SearXNG sidecar owns its own runtime contract.
- Selection Basis: Local fallback from repo planning docs because the `.project` chain ended at `t09-profile-gating` and Notion state is not available in this workflow.

## Phase 1

- Selected from the repo's own cleanup sequence after completing baseline profile hardening and optional-feature gating.
- Evidence:
  - `06-risk-and-bloat-report.md`: explicitly calls out cross-sidecar coupling where the SearXNG script seeks the voice-backend Python runtime and lists decoupling as a next cleanup step.
  - `02-sidecar-audit.md`: marks SearXNG and voice as separately optional and flags their runtime coupling as an investigation target.
  - Existing runtime notes show the current fallback chain still probes `apps/voice-backend/runtime/python/python.exe` even though the SearXNG package build already copies Python into the sidecar payload.

## Notes

- Scope should stay narrow: remove the voice-backend Python fallback from the SearXNG startup script and launcher, then verify the package/build assumptions still hold.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Removed the external voice runtime fallbacks from `apps/searxng/start-searxng.ps1` so the bootstrap script only accepts `runtime/python/python.exe` inside the SearXNG package.
- Removed the same external fallback candidates from `SearxngHostLauncher.EnumerateBundledPythonCandidates(...)` so runtime payload validation no longer treats a voice-sidecar Python install as satisfying the bundled SearXNG contract.
- Added focused runtime-host tests covering the new contract: the bundled candidate list now contains only the local SearXNG runtime, and an external voice-backend Python path no longer makes the packaged script payload look valid.

## Phase 3

- Focused validation:
  - `dotnet test tests/SirThaddeus.Tests/SirThaddeus.Tests.csproj -c Release --filter SearxngHostLauncherTests`: passed (2/2).
  - Verified current repo payload contains `apps/searxng/package/runtime/python/python.exe` and `apps/searxng/package/source/searxng-upstream/searx/webapp.py` before removing fallbacks.
- `dotnet build SirThaddeus.sln --no-restore -c Release`: passed.
- `dotnet test SirThaddeus.sln -c Release --no-build`: passed after clearing inherited `ST_AUDIT_PATH` and `ST_SETTINGS_PATH` overrides from the terminal session. Total: 2096 passed, 0 failed.

## Outcome

- The SearXNG sidecar now owns its Python runtime contract explicitly instead of silently depending on the voice-backend package layout.
- Future launcher consolidation can assume a cleaner package boundary because SearXNG payload validation now matches the deploy/package diagnostics.