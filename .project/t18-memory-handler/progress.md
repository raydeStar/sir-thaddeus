# Extract Memory Handler From MainWindow

- Status: Done
- Branch: `task/extract-memory-handler`
- Started: 2026-04-01
- Objective: Extract the memory tab refresh/edit workflow and its row view models from `MainWindow.axaml.cs` into a dedicated partial class so the ongoing MainWindow decomposition continues through a cohesive admin-facing subsystem.
- Selection Basis: Next repo-grounded cleanup slice after `t17-action-permission-handler`, based on the remaining MainWindow responsibility clusters and the fact that the memory tab handlers and row models are already grouped around one runtime API surface.

## Phase 1

- Selected as the next narrow verifiable slice of the broader MainWindow decomposition work.
- Evidence:
  - The memory tab handlers form a cohesive cluster: refresh, four grid commit handlers, and four row view model types.
  - The cluster is operator-facing and isolated from the core send/composer path.
  - The extraction is behavior-preserving because the methods already operate through existing MainWindow state, controls, and runtime API calls without requiring a new abstraction.

## Notes

- Keep scope narrow: move only the memory tab workflow and its local row view models.
- Do not redesign the memory editing UX or API contract in this task.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.Memory.cs` and moved the memory tab workflow into it.
- Moved the memory refresh handler, four data grid commit handlers, and the four memory row view model types out of `MainWindow.axaml.cs`.
- Kept behavior unchanged by retaining the existing MainWindow collections, control bindings, and runtime API calls.

## Phase 3

- Build:
  - `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: first run failed once with a missing `Avalonia.Interactivity` import in `MainWindow.Memory.cs`; second run passed with 0 errors after adding the import.
- Tests:
  - `Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue; Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue; dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed, 0 skipped.

## Phase 4

- Confidence: 100%.
- Reason: the extraction remains a narrow partial-class split, the one compile issue was resolved cleanly, and the full solution test suite passed without regression.