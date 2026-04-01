# Extract Shutdown Handler From MainWindow

- Status: Done
- Branch: `task/extract-shutdown-handler`
- Started: 2026-04-01
- Objective: Extract the stop and shutdown control workflow from `MainWindow.axaml.cs` into a dedicated partial class so MainWindow decomposition continues through a small, operator-facing seam with clear teardown behavior.
- Selection Basis: Next repo-grounded cleanup slice after `t15-settings-handler`, based on the remaining MainWindow responsibility hotspots and the explicit STOP ALL teardown flow called out in the repo flow audit.

## Phase 1

- Selected as the next narrow verifiable slice of the broader MainWindow decomposition work.
- Evidence:
  - `03-entrypoints-and-flows.md`: calls out STOP ALL in the UI as the control-path teardown coordinator.
  - The stop and shutdown methods already form a tight cluster: cancel current run, perform hard shutdown, kill known managed backend processes, and schedule final process exit fallback.
  - The extraction is behavior-preserving because the methods already operate entirely through existing MainWindow state and launcher fields.

## Notes

- Keep scope narrow: move only STOP, STOP ALL, and direct hard-shutdown helpers into a partial class.
- Do not change teardown behavior or exit timing in this task.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added `MainWindow.Shutdown.cs` as a dedicated partial class for the STOP and STOP ALL control workflow plus the direct hard-shutdown helpers.
- Removed the extracted stop and shutdown methods from `MainWindow.axaml.cs` without changing any call sites or surrounding MainWindow behavior.
- Kept the extraction narrow: no teardown behavior changes, no exit-timing changes, and no launcher lifecycle redesign.

## Phase 3

- Solution build: `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: passed with 0 errors.
- Solution tests: `dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed.

## Phase 4

- Confidence: 100%
- Reason: This task only extracts the existing stop and shutdown workflow into a partial class while preserving operator-facing behavior and teardown order. The full Release build and full Release test suite both passed unchanged.