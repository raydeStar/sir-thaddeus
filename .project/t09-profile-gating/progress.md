# Profile Gate Optional Features

- Status: Done
- Branch: `task/profile-gate-optional-features`
- Started: 2026-04-01
- Objective: Gate optional voice, local SearXNG, and deep-dive product surfaces behind explicit product-profile enablement so the Baseline profile stays typed-first and non-sidecar by default.
- Selection Basis: Local fallback from repo planning docs because the `.project` chain ends at `t08-baseline` and Notion state is not available in this workflow.

## Phase 1

- Selected from the repo's own product-focus planning documents rather than an existing `.project` task card.
- Evidence:
  - `06-risk-and-bloat-report.md`: highest-ROI sequence lists profile gating of optional features as the next step after baseline config tightening.
  - `05-product-critical-path.md`: marks voice, SearXNG, and deep-dive as optional to the typed hero flow.
  - `07-feature-to-code-map.md`: recommends optionalizing managed SearXNG, voice runtime, and deep-dive/place briefing.

## Notes

- Scope is inferred from repository planning notes, so implementation must stay narrow and evidence-driven.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added profile-capability helpers in `AppSettings` for voice, bundled SearXNG auto-start, deep-dive briefings, and advanced place discovery.
- Wired the runtime to respect effective profile settings for bundled SearXNG startup and to pass deep-dive/place-discovery flags into the agent orchestrator.
- Downgraded deep-dive routing and search-mode hints to fact-find when the active profile disables advanced research flows.
- Disabled advanced local-business enrichment tools in the search pipeline when the active profile does not allow place discovery.
- Updated the Avalonia settings UI so the Local VoiceHost and bundled SearXNG auto-start toggles render as unavailable under the baseline profile instead of advertising unsupported behavior.

## Phase 3

- Focused regression tests: passed.
  - `SettingsManagerEnvironmentOverrideTests`
  - `SearchPipelineTests`
  - `RouteNormalizationTests`
- `dotnet build SirThaddeus.sln --no-restore -c Release`: passed.
- `dotnet test SirThaddeus.sln -c Release --no-build`: passed after clearing inherited `ST_AUDIT_PATH` and `ST_SETTINGS_PATH` overrides from the terminal session. The first failure was environmental rather than caused by this task.

## Outcome

- Baseline profile now keeps local voice startup off, bundled SearXNG auto-start off, and advanced deep-dive/place-enrichment search branches off by default.
- Non-baseline profiles can still opt into those features through the existing settings model.