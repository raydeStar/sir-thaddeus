# Extract Action And Permission Handler From MainWindow

- Status: Done
- Branch: `task/extract-action-permission-handler`
- Started: 2026-04-01
- Objective: Extract the action drawer, permission request UI, and recent activity workflow from `MainWindow.axaml.cs` into a dedicated partial class so MainWindow decomposition continues through a coherent operator-facing subsystem.
- Selection Basis: Next repo-grounded cleanup slice after `t16-shutdown-handler`, based on the remaining MainWindow responsibility clusters and the explicit permission/action flow called out in the repo flow audit.

## Phase 1

- Selected as the next narrow verifiable slice of the broader MainWindow decomposition work.
- Evidence:
  - `03-entrypoints-and-flows.md`: calls out the UI permission flow as a distinct core path.
  - The action drawer and permission methods already form a cohesive cluster: drawer toggles, permission request UI, permission decision submission, activity summary, and recent activity/audit projection.
  - The extraction is behavior-preserving because the methods already operate through existing MainWindow state and controls without requiring a new abstraction.

## Notes

- Keep scope narrow: move only the action drawer, permission UI, and recent activity helpers into a partial class.
- Do not redesign approval UX or audit presentation behavior in this task.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.Actions.cs` and moved the operator-facing action subsystem into it.
- Moved drawer controls, permission request rendering, permission decision submission, runtime endpoint/detail helpers, recent activity audit projection helpers, and the local helper record types out of `MainWindow.axaml.cs`.
- Kept behavior unchanged by retaining existing MainWindow fields, control references, and helper calls rather than introducing new abstractions.

## Phase 3

- Build:
  - `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: passed with 0 errors.
- Tests:
  - `Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue; Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue; dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed, 0 skipped.

## Phase 4

- Confidence: 100%.
- Reason: the extraction is compile-clean, full-solution tests passed without regression, and the moved code remains a behavior-preserving partial-class split of an already cohesive MainWindow subsystem.