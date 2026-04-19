# Extract Event Stream Handler From MainWindow

- Status: Done
- Branch: `task/extract-event-stream-handler`
- Started: 2026-04-01
- Objective: Extract the runtime event-stream loop and event dispatch logic from `MainWindow.axaml.cs` into a dedicated partial class so MainWindow state-management decomposition starts with a narrow, behavior-preserving seam.
- Selection Basis: Next repo-grounded cleanup slice after `t12-mcp-tool-ownership`, based on the MainWindow decomposition step in `06-risk-and-bloat-report.md` and the event-stream complexity hotspot called out in that report.

## Phase 1

- Selected as the narrowest verifiable slice of the broader MainWindow decomposition work.
- Evidence:
  - `06-risk-and-bloat-report.md`: lists MainWindow as a top complexity source and recommends UI state-management decomposition after the MCP ownership cleanup.
  - `06-risk-and-bloat-report.md`: specifically calls out the event-stream region as a large responsibility hotspot.
  - Extracting the event-stream methods into a partial class is a behavior-preserving move with full build/test validation and avoids a broad UI architecture rewrite.

## Notes

- Keep scope narrow: move only the event-stream loop, dispatch helpers, and payload parsing helpers.
- Do not redesign UI state or message flow in this task.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added `MainWindow.EventStream.cs` as a dedicated partial class for the runtime event-stream loop, event dispatch switch, and payload deserialization helper.
- Removed the extracted methods from `MainWindow.axaml.cs` without changing their call sites or surrounding MainWindow state.
- Kept the extraction narrow: no UI workflow redesign, no control wiring changes, and no behavioral refactor beyond relocating the existing methods.

## Phase 3

- Solution build: `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: passed with 0 errors.
- Solution tests: `dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed.

## Phase 4

- Confidence: 100%
- Reason: This task only extracts the existing MainWindow event-stream handling into a partial class while preserving method boundaries and behavior. The full Release build and full Release test suite both passed unchanged.