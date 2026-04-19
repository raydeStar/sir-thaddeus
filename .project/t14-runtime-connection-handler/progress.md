# Extract Runtime Connection Handler From MainWindow

- Status: Done
- Branch: `task/extract-runtime-connection-handler`
- Started: 2026-04-01
- Objective: Extract the runtime connection, launch, retry, and connection-status helper workflow from `MainWindow.axaml.cs` into a dedicated partial class so MainWindow decomposition continues through a narrow, behavior-preserving seam.
- Selection Basis: Next repo-grounded cleanup slice after `t13-event-stream-handler`, based on the MainWindow decomposition step in `06-risk-and-bloat-report.md` and the connect/launch hotspot called out there.

## Phase 1

- Selected as the next narrow verifiable slice of the broader MainWindow decomposition work.
- Evidence:
  - `06-risk-and-bloat-report.md`: identifies MainWindow workflow state as the next cleanup area after MCP tool ownership and highlights the connect/launch region as a major responsibility hotspot.
  - `03-entrypoints-and-flows.md`: calls out UI runtime auto-start and connect/launch flow as a core path.
  - The runtime connection methods already form a self-contained cluster with URI parsing, retry logic, connection-state updates, and managed-runtime launch integration.

## Notes

- Keep scope narrow: move only the runtime connection workflow and direct status helpers into a partial class.
- Do not redesign the runtime UX or change launch behavior in this task.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added `MainWindow.RuntimeConnection.cs` as a dedicated partial class for the runtime connect/start/stop entrypoints plus the URI parsing, retry, connect, and connection-status helper methods.
- Removed the extracted connection workflow from `MainWindow.axaml.cs` without changing its call sites or surrounding UI logic.
- Kept the extraction narrow: no runtime UX redesign, no launch-behavior changes, and no new retry or connection semantics.

## Phase 3

- Solution build: `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: passed with 0 errors.
- Solution tests: `dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed.

## Phase 4

- Confidence: 100%
- Reason: This task only extracts the existing runtime connection workflow into a partial class while preserving behavior and UI flow. The full Release build and full Release test suite both passed unchanged.