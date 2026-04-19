# Extract Chat Surface Handler From MainWindow

- Status: Done
- Branch: `task/extract-chat-surface-handler`
- Started: 2026-04-01
- Objective: Extract the prompt-box events, suggestion chip workflow, empty-state/layout updates, runtime status strip updates, and their local helper types from `MainWindow.axaml.cs` into a dedicated partial class so the ongoing MainWindow decomposition continues through a cohesive chat-surface subsystem.
- Selection Basis: Next repo-grounded cleanup slice after `t20-message-surface-handler`, based on the remaining MainWindow responsibility clusters and the fact that prompt-box interactions, landing-state toggles, and runtime status presentation already operate together as one chat-surface workflow.

## Phase 1

- Selected as the next narrow verifiable slice of the broader MainWindow decomposition work.
- Evidence:
  - The cluster is cohesive around chat-surface behavior: prompt-box events, suggestion chip actions, composer state, empty-state layout, header connection display, and runtime status strip updates.
  - The cluster is bounded away from the core prompt submission logic and the lower-level runtime event-stream handling.
  - The extraction is behavior-preserving because the methods already operate through existing MainWindow state, controls, and helper calls without requiring a new abstraction.

## Notes

- Keep scope narrow: move only the chat-surface state/update helpers and their local helper types.
- Do not redesign prompt submission, connection behavior, or landing-page UX in this task.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.ChatSurface.cs` and moved the chat-surface workflow into it.
- Moved prompt-box event handlers, suggestion chip actions, composer state updates, landing/empty-state helpers, runtime status strip updates, and their local helper types out of `MainWindow.axaml.cs`.
- Kept behavior unchanged by retaining the existing MainWindow state, control bindings, and shared helper calls, while deliberately leaving prompt submission and reasoning-output helpers in place.

## Phase 3

- Build:
  - `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: first run failed once with a missing `Avalonia.Controls.Primitives` import for `ScrollBarVisibility`; second run passed with 0 errors after adding the import.
- Tests:
  - `Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue; Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue; dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed, 0 skipped.

## Phase 4

- Confidence: 100%.
- Reason: the extraction stayed narrow, the one compile issue was resolved cleanly, and the full solution test suite passed without regression.