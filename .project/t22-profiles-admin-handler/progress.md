# Extract Profiles Admin Handler From MainWindow

- Status: Done
- Branch: `task/extract-profiles-admin-handler`
- Started: 2026-04-01
- Objective: Move the profiles and personalities refresh/list-state workflow from `MainWindow.axaml.cs` into the existing profiles partial so the ongoing MainWindow decomposition keeps the full identity-management subsystem together.
- Selection Basis: Next repo-grounded cleanup slice after `t21-chat-surface-handler`, based on the remaining MainWindow responsibility clusters and the fact that profile refresh, profile selection state, active profile switching, and the local list-item view models all belong to the same operator-facing admin workflow already centered in `MainWindow.Profiles.cs`.

## Phase 1

- Selected as the next narrow verifiable slice of the broader MainWindow decomposition work.
- Evidence:
  - The cluster is cohesive around identity management: refresh, list selection state, active-profile and active-personality changes, and the backing list-item view models.
  - The cluster already depends on helpers in `MainWindow.Profiles.cs`, so merging it there reduces split ownership rather than increasing it.
  - The extraction is behavior-preserving because the methods already operate through existing MainWindow state, controls, and runtime API calls without requiring a new abstraction.

## Notes

- Keep scope narrow: move only the profiles/personality refresh and list-state workflow plus the local list-item view models.
- Do not redesign profile CRUD UX or runtime profile APIs in this task.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Expanded `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.Profiles.cs` so it now owns the full profiles/personality admin workflow instead of only CRUD operations.
- Moved profile refresh, list selection state, active profile/personality handlers, and the two local list-item view model types out of `MainWindow.axaml.cs` and into the existing profiles partial.
- Kept behavior unchanged by preserving the same MainWindow state, control bindings, runtime API calls, and helper interactions already used by the profile CRUD actions.

## Phase 3

- Build:
  - `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: passed with 0 errors.
- Tests:
  - `Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue; Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue; dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed, 0 skipped.

## Phase 4

- Confidence: 100%.
- Reason: the extraction reduced split ownership inside the profile subsystem, compiled cleanly on the first pass, and the full solution suite passed without regression.